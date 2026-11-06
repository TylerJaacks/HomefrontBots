using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomefrontImitation;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>What HomefrontBots takes from the model trained on real players (HomefrontImitation):
/// <list type="bullet">
/// <item><b>combat</b>: in a fight, the policy picks which way to move relative to the enemy (or to hold still),
/// from what humans did in the same situation; the move is made only along the nav graph.</item>
/// <item><b>stance</b>: crouching, sprinting and jumping when and as often as humans do.</item>
/// <item><b>routes</b>: A* prefers floor humans walk (an extra cost on floor they never used).</item>
/// <item><b>spots</b>: at objectives (and roaming), spots humans hold and win fights from.</item>
/// </list>
/// Files in Mods\HomefrontBots\imitation\: policy.json and &lt;map&gt;.heat from train\hfimitate.py, and
/// imitation.json (these switches). Each part falls back to the hand-written behaviour without its file.</summary>
internal sealed class Imitation
{
    internal sealed class Config
    {
        public bool Enabled { get; set; } = true;
        public bool Combat { get; set; } = true;
        public bool Stance { get; set; } = true;
        public bool Routes { get; set; } = true;
        public bool Spots { get; set; } = true;
        public float Temperature { get; set; } = 0.8f;    // < 1: closer to the likeliest human move
        public float RouteBias { get; set; } = 0.6f;      // extra A* cost (x distance) on floor humans never use
        public float FightSpeed { get; set; } = 0.85f;    // of GroundSpeed while moving in a fight
        public float MinHeatMinutes { get; set; } = 10f;  // a map's heat is used once humans spent this long on it
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Per bot: what it decided last and when to decide again.</summary>
    internal sealed class State
    {
        public long DecideAt;
        public int Node = -1;          // the nav node it moves to in a fight (-1: stand)
        public Vector2 Move;           // last movement input (world, flat), for the next decision's prev_* features
        public bool Crouch, Sprint;
        public long StanceUntil;       // stance held at least until then
        public long JumpAfter;
        public int Class;
        public int NavGeneration = -1;  // the graph Node belongs to
        public bool Unseen;             // last decision: the AI's enemy wasn't in sight (strafe the old way)

        /// <summary>Forgets the fight move (a new fight, death, teleport, another graph).</summary>
        public void Forget()
        {
            Node = -1;
            DecideAt = 0;
            Unseen = false;
        }
    }

    readonly ModLog log;
    readonly string dir;
    readonly long started = Environment.TickCount64;
    readonly Senses senses = new();
    readonly float[] features = new float[Features.Count];

    internal Config Cfg = new();
    Policy? policy;
    string policyNote = "not loaded";

    // This map's heat, on the nav graph's nodes.
    string heatMap = "";
    int heatFor = -1;            // the nav graph generation it was built for, -1 none
    float[]? score;              // where humans hold and win fights (spots)
    float[]? traffic;            // 0..1 how much humans walk there
    string heatNote = "no map";
    long decisions, fallbacks;

    public Imitation(ModLog log, string modDirectory)
    {
        this.log = log;
        dir = Path.Combine(modDirectory, "imitation");
        Directory.CreateDirectory(dir);
        LoadConfig();
        LoadPolicy();
    }

    string ConfigPath => Path.Combine(dir, "imitation.json");

    float Seconds(long nowMs) => (nowMs - started) / 1000f;

    public bool CombatActive => Cfg.Enabled && Cfg.Combat && policy != null;
    public bool StanceActive => Cfg.Enabled && Cfg.Stance && policy != null;
    bool RoutesActive => Cfg.Enabled && Cfg.Routes && traffic != null;
    bool SpotsActive => Cfg.Enabled && Cfg.Spots && score != null;

    void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) ?? new Config();
            else
                SaveConfig();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.Warn($"imitation: {ConfigPath}: {e.Message}; using the defaults");
            Cfg = new Config();
        }
    }

    public void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Cfg, Json));
        }
        catch (IOException e)
        {
            log.Warn($"imitation: can't save {ConfigPath}: {e.Message}");
        }
    }

    public void LoadPolicy()
    {
        policy = null;
        string path = Path.Combine(dir, "policy.json");
        if (!File.Exists(path))
        {
            policyNote = "no policy.json (train one with HomefrontImitation)";
            return;
        }
        try
        {
            var p = Policy.Load(path);
            if (p.Schema != Features.Schema)
            {
                policyNote = "policy.json was trained on other features (retrain with this version's recordings)";
                log.Warn($"imitation: {policyNote}: {p.Schema} vs {Features.Schema}");
                return;
            }
            policy = p;
            policyNote = p.Describe;
            log.Write($"imitation: policy loaded: {p.Describe}");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            policyNote = "policy.json unreadable: " + e.Message;
            log.Warn("imitation: " + policyNote);
        }
    }

    /// <summary>A new map: forget the old one's heat (built again once its nav graph is ready).</summary>
    public void OnMap(string map, Nav nav)
    {
        heatMap = map;
        heatFor = -1;
        score = traffic = null;
        nav.Bias = null;
        heatNote = "waiting for the nav graph";
        senses.Clear();
    }

    /// <summary>Every tick: once the nav graph is ready, put the map's heat on it (once).</summary>
    public void Tick(Nav nav)
    {
        if (!nav.Ready || heatFor == nav.Generation || heatMap.Length == 0)
            return;
        heatFor = nav.Generation;
        BuildHeat(nav);
        nav.Bias = RoutesActive ? bias : null;
    }

    float[]? bias;

    // Never negative: a cost below the distance would make the A* heuristic overestimate (and below -1, loop).
    float RouteBias => Math.Clamp(Cfg.RouteBias, 0f, 4f);

    // ---- heat (train\hfimitate.py writes it: "HFHT", version, spacing, cells, human seconds, recordings; per cell
    //      cx, cy (i32), floor z, seconds, fight seconds (f32), kills from, deaths at (u16))

    void BuildHeat(Nav nav)
    {
        score = traffic = bias = null;
        string path = Path.Combine(dir, heatMap.ToLowerInvariant() + ".heat");
        if (!File.Exists(path))
        {
            heatNote = $"no {Path.GetFileName(path)}";
            return;
        }
        long t0 = Environment.TickCount64;
        int n = nav.Count, matched = 0, cellsIn;
        var dwell = new float[n];
        var fight = new float[n];
        var kills = new float[n];
        var deaths = new float[n];
        float humanSeconds;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != 0x54484648 || r.ReadUInt16() != 1)
            {
                heatNote = $"{Path.GetFileName(path)}: not a version 1 heat file";
                return;
            }
            float spacing = r.ReadSingle();
            cellsIn = (int)r.ReadUInt32();
            humanSeconds = r.ReadSingle();
            r.ReadUInt16();
            if (MathF.Abs(spacing - Nav.GridSpacing) > 0.01f)
            {
                heatNote = $"{Path.GetFileName(path)}: grid {spacing}, the nav graph's is {Nav.GridSpacing}";
                return;
            }
            for (int i = 0; i < cellsIn; ++i)
            {
                int cx = r.ReadInt32(), cy = r.ReadInt32();
                float z = r.ReadSingle(), sec = r.ReadSingle(), fsec = r.ReadSingle();
                ushort k = r.ReadUInt16(), d = r.ReadUInt16();
                int node = nav.NodeInCell(cx, cy, z, 64f);
                if (node < 0)
                    continue;
                ++matched;
                dwell[node] += sec;
                fight[node] += fsec;
                kills[node] += k;
                deaths[node] += d;
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            heatNote = $"{Path.GetFileName(path)}: {e.Message}";
            return;
        }
        if (humanSeconds < Cfg.MinHeatMinutes * 60f)
        {
            heatNote = $"{humanSeconds / 60:0} human minutes on this map so far (needs {Cfg.MinHeatMinutes:0})";
            return;
        }

        // Spread along the graph's links: a recorded walk covers one cell in a corridor several cells wide, and
        // spreading over links never crosses a wall.
        var (_, linkStart, links) = nav.Graph;
        Smooth(dwell, linkStart, links, 4);
        Smooth(fight, linkStart, links, 4);
        Smooth(kills, linkStart, links, 3);
        Smooth(deaths, linkStart, links, 3);

        float dwellRef = Percentile(dwell, 0.99f), fightRef = Percentile(fight, 0.99f);
        traffic = new float[n];
        score = new float[n];
        bias = new float[n];
        for (int i = 0; i < n; ++i)
        {
            float t = dwellRef > 0 ? MathF.Min(1f, MathF.Log(1f + dwell[i]) / MathF.Log(1f + dwellRef)) : 0f;
            float f = fightRef > 0 ? MathF.Min(1f, MathF.Log(1f + fight[i]) / MathF.Log(1f + fightRef)) : 0f;
            float win = (kills[i] - deaths[i]) / (kills[i] + deaths[i] + 3f);
            traffic[i] = t;
            score[i] = MathF.Max(0f, 0.6f * t + 0.4f * f + 0.5f * win);
            bias[i] = RouteBias * (1f - t);
        }
        heatNote = $"{Path.GetFileName(path)}: {matched}/{cellsIn} cells on the graph, {humanSeconds / 60:0} human minutes, " +
                   $"built in {Environment.TickCount64 - t0} ms";
        log.Write("imitation: heat " + heatNote);
    }

    static void Smooth(float[] v, int[] linkStart, uint[] links, int passes)
    {
        var next = new float[v.Length];
        for (int pass = 0; pass < passes; ++pass)
        {
            for (int u = 0; u < v.Length; ++u)
            {
                int a = linkStart[u], b = linkStart[u + 1];
                if (b == a)
                {
                    next[u] = v[u];
                    continue;
                }
                float sum = 0;
                for (int k = a; k < b; ++k)
                    sum += v[links[k]];
                next[u] = 0.5f * v[u] + 0.5f * sum / (b - a);
            }
            Array.Copy(next, v, v.Length);
        }
    }

    static float Percentile(float[] v, float q)
    {
        var nz = new List<float>();
        foreach (float x in v)
            if (x > 0)
                nz.Add(x);
        if (nz.Count == 0)
            return 0;
        nz.Sort();
        return nz[Math.Min(nz.Count - 1, (int)(q * nz.Count))];
    }

    /// <summary>A node within <paramref name="radius"/> of a point, drawn with more weight where humans hold and
    /// win fights (spots on) or simply walk (roaming). -1 without heat (use Nav.RandomNear).</summary>
    public int PickSpot(Nav nav, FVector at, float radius, bool hold, Random random)
    {
        if (!SpotsActive || score == null || traffic == null)
            return -1;
        float[] w = hold ? score : traffic;
        Span<int> cand = stackalloc int[16];
        Span<float> weight = stackalloc float[16];
        int count = 0;
        float total = 0;
        for (int tries = 0; tries < 24 && count < cand.Length; ++tries)
        {
            int node = nav.RandomNear(at, radius);
            if (node < 0)
                continue;
            float x = 0.1f + w[node] * w[node] * 4f;   // squared: favour the best spots, keep some variety
            cand[count] = node;
            weight[count++] = x;
            total += x;
        }
        if (count == 0)
            return -1;
        float pick = random.NextSingle() * total;
        for (int i = 0; i < count; ++i)
            if ((pick -= weight[i]) <= 0)
                return cand[i];
        return cand[count - 1];
    }

    // ---- decisions

    /// <summary>A movement for a bot in a fight, every 0.1 s: the policy's movement class (directions blocked by
    /// a wall left out), turned into a nav node reachable in a straight line that way, held until the next
    /// decision. Stance from the policy too. False: no decision possible (fall back to strafing).</summary>
    public bool Fight(BotsScript mod, Nav nav, State st, Controller c, Pawn pawn, int team, long now)
    {
        if (policy == null || Game.World is not World world || Game.Info is not GameInfo game)
            return false;
        if (st.NavGeneration != nav.Generation)
        {
            st.NavGeneration = nav.Generation;
            st.Forget();
        }
        if (now >= st.DecideAt)
        {
            Pawn? preferred = c.Enemy is { IsValid: true } e && e.Alive ? e : null;
            senses.SetMove(pawn, st.Move);
            senses.Observe(pawn, team, mod.Soldiers, preferred, world, game, Seconds(now), out var ctx);
            // The movement head learned from fights with an enemy in sight; the AI can be Attacking one it lost
            // sight of: strafe the old way then.
            st.DecideAt = now + (long)(Features.SampleSeconds * 1000);
            st.Unseen = !ctx.HasEnemy;
            if (st.Unseen)
            {
                st.Node = -1;
                return false;
            }
            Features.Write(ctx, features);
            var d = policy.Decide(features, Cfg.Temperature);
            ++decisions;
            Vector2 frame = Features.FrameOf(ctx);
            int from = nav.Nearest(pawn.Location);
            st.Node = -1;
            st.Move = Vector2.Zero;
            var tried = new bool[Features.MoveClasses];
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                int k = Policy.Sample(d.Move, i => !tried[i] && (i == 0 || ctx.Probe[i - 1] >= 0.35f), mod.Random);
                if (k < 0)
                    break;
                tried[k] = true;
                st.Class = k;
                if (k == 0)
                    break;  // hold still
                Vector2 dir = Features.FromFrame(Features.Direction(k - 1), frame);
                int node = StepToward(nav, from, pawn.Location, dir);
                if (node >= 0)
                {
                    st.Node = node;
                    st.Move = dir;
                    break;
                }
                ++fallbacks;
            }
            if (StanceActive)
                ApplyStance(st, pawn, d, now, fighting: true, perSecond: 10f, mod.Random);
        }
        if (st.Unseen)
            return false;
        if (st.Node < 0 || st.Node >= nav.Count)
        {
            pawn.Acceleration = default;
            if (pawn.Physics == EPhysics.PHYS_Walking)
                pawn.Velocity = new FVector(0, 0, pawn.Velocity.Z);
            return true;
        }
        FVector target = nav.Stand(st.Node);
        if (FVector.Dist2D(pawn.Location, target) < 30f)
            st.DecideAt = Math.Min(st.DecideAt, now);   // there early: decide again now
        Brain.Steer(c, pawn, target, Cfg.FightSpeed * SpeedFactor(pawn, st), look: false);
        return true;
    }

    // The node a few steps along `dir` that can be walked to in a straight line (the graph's links were all tested
    // with a body sweep, so this never walks into a wall or off a ledge it couldn't climb back).
    static int StepToward(Nav nav, int from, FVector at, Vector2 dir)
    {
        if (from < 0)
            return -1;
        foreach (float dist in stackalloc float[] { 260f, 170f, 100f })
        {
            int n = nav.Nearest(new FVector(at.X + dir.X * dist, at.Y + dir.Y * dist, at.Z), 60f);
            if (n >= 0 && n != from && nav.WalkableLine(from, n, 10))
                return n;
        }
        return -1;
    }

    /// <summary>Out of a fight: the stance the policy picks for the bot's situation, decided every half second.
    /// Returns the speed factor for steering.</summary>
    public float Travel(BotsScript mod, State st, Controller c, Pawn pawn, int team, FVector heading, long now)
    {
        var flat = new Vector2(heading.X - pawn.Location.X, heading.Y - pawn.Location.Y);
        st.Move = flat.LengthSquared() > 1f ? Vector2.Normalize(flat) : Vector2.Zero;
        if (!StanceActive || policy == null || Game.World is not World world || Game.Info is not GameInfo game)
            return 1f;
        if (now >= st.DecideAt)
        {
            st.DecideAt = now + 500;
            senses.SetMove(pawn, st.Move);
            senses.Observe(pawn, team, mod.Soldiers, null, world, game, Seconds(now), out var ctx);
            Features.Write(ctx, features);
            ApplyStance(st, pawn, policy.Decide(features, Cfg.Temperature), now, fighting: false, perSecond: 2f, mod.Random);
        }
        return SpeedFactor(pawn, st);
    }

    // Crouch and sprint with some hysteresis and a minimum hold, so a bot doesn't bob up and down every decision;
    // jumps as often as the policy says humans do, at most every 1.5 s.
    static void ApplyStance(State st, Pawn pawn, Policy.Decision d, long now, bool fighting, float perSecond, Random random)
    {
        // What the pawn really does (a respawned body stands; the game can refuse a sprint).
        st.Crouch = pawn.bWantsToCrouch || pawn.bIsCrouched;
        st.Sprint = pawn.bIsSprinting;
        if (now >= st.StanceUntil)
        {
            bool crouch = st.Crouch ? d.Crouch > 0.35f : d.Crouch > 0.65f;
            bool sprint = !crouch && !fighting && (st.Sprint ? d.Sprint > 0.35f : d.Sprint > 0.65f);
            if (crouch != st.Crouch || sprint != st.Sprint)
            {
                st.Crouch = crouch;
                st.Sprint = sprint;
                st.StanceUntil = now + 800;
                try
                {
                    pawn.ShouldCrouch(crouch);
                    pawn.SetSprinting(sprint);
                }
                catch (GameCrashException)
                {
                }
            }
        }
        // d.Jump is the chance per recorded sample (0.1 s); decided less often, the chance covers the whole interval.
        float p = 1f - MathF.Pow(1f - Math.Clamp(d.Jump, 0f, 1f), 10f / perSecond);
        if (now >= st.JumpAfter && pawn.Physics == EPhysics.PHYS_Walking && !st.Crouch && random.NextSingle() < p)
        {
            st.JumpAfter = now + 1500;
            try
            {
                pawn.DoJump(false);
            }
            catch (GameCrashException)
            {
            }
        }
    }

    // Steering overrides the walking velocity outright, so a stance's speed has to be applied here.
    static float SpeedFactor(Pawn pawn, State st)
    {
        if (st.Crouch)
            return pawn.CrouchedPct is > 0.05f and < 1f ? pawn.CrouchedPct : 0.45f;
        if (st.Sprint)
            return pawn.SprintingPct is > 1f and < 3f ? pawn.SprintingPct : 1.35f;
        return 1f;
    }

    // ---- status

    public string Status =>
        $"{(Cfg.Enabled ? "on" : "off")}: combat {On(Cfg.Combat, policy != null)}, stance {On(Cfg.Stance, policy != null)}, " +
        $"routes {On(Cfg.Routes, traffic != null)}, spots {On(Cfg.Spots, score != null)}; policy: {policyNote}; heat: {heatNote}";

    static string On(bool wanted, bool available) => !wanted ? "off" : available ? "on" : "no data";

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Cfg.Enabled,
        ["combat"] = CombatActive,
        ["stance"] = StanceActive,
        ["routes"] = RoutesActive,
        ["spots"] = SpotsActive,
        ["policy"] = policyNote,
        ["heat"] = heatNote,
        ["decisions"] = decisions,
        ["blockedMoves"] = fallbacks,
        ["traces"] = senses.Traces,
        ["schema"] = Features.Schema,
    };

    /// <summary>Applies the switches after a change (routes need the graph's bias set or cleared).</summary>
    public void Apply(Nav nav)
    {
        SaveConfig();
        if (bias != null)
            for (int i = 0; i < bias.Length && traffic != null; ++i)
                bias[i] = RouteBias * (1f - traffic[i]);
        nav.Bias = RoutesActive ? bias : null;
    }

    /// <summary>Everything read again from disk: the switches, the policy, this map's heat.</summary>
    public void Reload(Nav nav)
    {
        LoadConfig();
        LoadPolicy();
        heatFor = -1;
        nav.Bias = null;
        Tick(nav);
    }
}
