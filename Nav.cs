using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>The navigation graph, generated on the server. Multiplayer maps ship no navigation data (no PathNodes or
/// ReachSpecs, no Kynapse pathdata), so the graph is built from the level's collision: a flood fill from the spawn
/// points and objectives over a grid, where every step is tested with soldier-sized box sweeps. Nodes are spots a
/// soldier can stand on, links are moves it can make (drops are one-way). Built a slice per tick, then cached per map
/// in nav\&lt;map&gt;.nav in the mod's folder.</summary>
sealed class Nav
{
    // Grid spacing, about a metre (1 unit ~ 2 cm): fine enough that a doorway gets a grid line through it.
    const float Spacing = 48f;
    const float MaxDrop = 250f;    // highest ledge a bot drops off (a one-way link)
    const float Margin = 4000f;    // with no combat volume: explore this far beyond spawn points/objectives
    const int MaxNodes = 700000;
    const uint Magic = 0x564E4648; // "HFNV"
    const uint Version = 3;        // 2: bounded by the ground combat volumes; 3: also filled from ladder tops

    // The soldier's body. Defaults are GCChar_MP_*_AST_C's; Begin reads the live class defaults.
    float radius = 15, halfHeight = 42, step = 26, walkableZ = 0.7f;

    enum Phase { Idle, Building, Ready }

    readonly ModLog log;
    readonly string cacheDir;
    readonly Random rng = new();
    Phase phase = Phase.Idle;
    string map = "";
    GameInfo? game;
    World? world;

    // While building: nodes and their outgoing links. Once ready: the links packed per node (linkStart/links).
    readonly List<FVector> floor = new();
    readonly List<List<uint>> adj = new();
    int[] linkStart = Array.Empty<int>();
    uint[] links = Array.Empty<uint>();
    readonly Dictionary<long, List<int>> cells = new();  // grid cell -> its nodes (one per floor)
    long linkCount;

    // Ladders: climb links between the node at the bottom and the node at the top of each LadderVolume, found when the
    // graph is ready (not cached: ladders are read from the map each time).
    readonly List<LadderClimb> ladders = new();
    readonly Dictionary<int, List<(int to, int ladder)>> ladderLinks = new();

    readonly Queue<int> queue = new();
    float minX, minY, maxX, maxY;
    long traces;
    double buildMs;
    long nextProgressLog;

    public Nav(ModLog log, string directory)
    {
        this.log = log;
        cacheDir = Path.Combine(directory, "nav");
    }

    static long CellKey(int ix, int iy) => ((long)ix << 32) | (uint)iy;
    static int CellOf(float v) => (int)MathF.Floor(v / Spacing + 0.5f);

    static float Dist(FVector a, FVector b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    string CachePath => Path.Combine(cacheDir, map + ".nav");

    public bool Ready => phase == Phase.Ready;
    public int Count => Ready ? floor.Count : 0;

    bool Trace(FVector start, FVector end, FVector extent, out TraceHit hit)
    {
        ++traces;
        try
        {
            hit = world!.Trace(game, start, end, extent);
            return true;
        }
        catch (GameCrashException)
        {
            hit = default;
            return false;
        }
    }

    // The walkable floor under (x, y), searching down from `top` to `bottom` with a small box (a line slips through
    // seams). Fails on steep surfaces and when the probe starts inside geometry.
    bool FindFloor(float x, float y, float top, float bottom, out float floorZ, out float normalZ)
    {
        floorZ = normalZ = 0;
        var probe = new FVector(radius * 0.5f, radius * 0.5f, 2f);
        if (!Trace(new FVector(x, y, top), new FVector(x, y, bottom), probe, out TraceHit hit) || hit.Actor == null || hit.Time <= 0f)
            return false;
        if (hit.Normal.Z < walkableZ)
            return false;
        floorZ = hit.Location.Z - probe.Z;
        normalZ = hit.Normal.Z;
        return true;
    }

    // Sweep the soldier's body from one floor point to another, lifted by a step so kerbs and stairs don't block.
    bool BodyFits(FVector from, FVector to)
    {
        float half = halfHeight - step * 0.5f;
        float lift = step + half;
        return Trace(new FVector(from.X, from.Y, from.Z + lift), new FVector(to.X, to.Y, to.Z + lift),
                   new FVector(radius, radius, half), out TraceHit hit) && hit.Actor == null;
    }

    int FindNode(int ix, int iy, float floorZ, float tolerance)
    {
        if (!cells.TryGetValue(CellKey(ix, iy), out var list))
            return -1;
        int best = -1;
        float bestD = tolerance;
        foreach (int n in list)
        {
            float d = MathF.Abs(floor[n].Z - floorZ);
            if (d <= bestD)
            {
                best = n;
                bestD = d;
            }
        }
        return best;
    }

    int AddNode(int ix, int iy, float floorZ)
    {
        if (floor.Count >= MaxNodes)
            return -1;
        int n = floor.Count;
        floor.Add(new FVector(ix * Spacing, iy * Spacing, floorZ));
        adj.Add(new List<uint>(8));
        if (!cells.TryGetValue(CellKey(ix, iy), out var list))
            cells[CellKey(ix, iy)] = list = new List<int>(1);
        list.Add(n);
        queue.Enqueue(n);
        return n;
    }

    bool Linked(int a, int b) => adj[a].Contains((uint)b);

    void Link(int a, int b)
    {
        if (a != b && !Linked(a, b))
        {
            adj[a].Add((uint)b);
            ++linkCount;
        }
    }

    static readonly (int x, int y)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    // Try the 8 grid neighbours of node a: find the floor there, check the rise is a step or a walkable slope (or a
    // drop), and sweep the body across.
    void Expand(int a)
    {
        FVector fa = floor[a];
        int ax = CellOf(fa.X), ay = CellOf(fa.Y);
        foreach (var d in Dirs)
        {
            int bx = ax + d.x, by = ay + d.y;
            float x = bx * Spacing, y = by * Spacing;
            if (x < minX || x > maxX || y < minY || y > maxY)
                continue;
            float dist = d.x != 0 && d.y != 0 ? Spacing * 1.41421f : Spacing;
            // A neighbour on (nearly) the same level that already moves to us: the same sweep works backwards.
            int known = FindNode(bx, by, fa.Z, step);
            if (known >= 0 && Linked(known, a))
            {
                Link(a, known);
                continue;
            }
            // Over one cell a walkable slope (<= 45 degrees) rises less than the cell's width.
            if (!FindFloor(x, y, fa.Z + step + dist + 10f, fa.Z - MaxDrop, out float fz, out float nz))
                continue;
            float rise = fz - fa.Z;
            float slopeTan = MathF.Sqrt(MathF.Max(0f, 1f - nz * nz)) / nz;
            if (rise > step + dist * slopeTan)
                continue;  // a ledge too high to step onto
            var fb = new FVector(x, y, fz);
            if (rise >= -step)
            {
                if (!BodyFits(fa, fb))
                    continue;
            }
            else
            {
                var over = new FVector(x, y, fa.Z);  // a drop: walk out level, then fall
                if (!BodyFits(fa, over) || !BodyFits(over, fb))
                    continue;
            }
            int b = FindNode(bx, by, fz, step);
            if (b < 0)
                b = AddNode(bx, by, fz);
            if (b >= 0)
                Link(a, b);
        }
    }

    // A flood-fill start: the floor under a spawn point or objective, if a soldier fits there.
    void Seed(FVector at)
    {
        int ix = CellOf(at.X), iy = CellOf(at.Y);
        if (!FindFloor(ix * Spacing, iy * Spacing, at.Z + halfHeight, at.Z - 400f, out float fz, out _))
            return;
        if (FindNode(ix, iy, fz, step) >= 0)
            return;
        var f = new FVector(ix * Spacing, iy * Spacing, fz);
        if (BodyFits(new FVector(f.X, f.Y, f.Z + 4f), f))
            AddNode(ix, iy, fz);
    }

    // Soldier size and limits from the US assault soldier's class defaults (both factions share them).
    void ReadBody()
    {
        (radius, halfHeight, step, walkableZ) = (15f, 42f, 26f, 0.7f);
        Pawn? cdo = null;
        for (int i = 0; i < Objects.Count && cdo == null; ++i)
        {
            nint o = Objects.At(i);
            if (o != 0 && Names.ObjectName(o) == "Default__GCChar_MP_US_AST_C")
                cdo = UObject.Get<Pawn>(o);
        }
        CylinderComponent? cyl = cdo?.CylinderComponent;
        if (cdo != null && cyl != null && cyl.CollisionRadius > 0 && cyl.CollisionHeight > 0)
            (radius, halfHeight, step, walkableZ) = (cyl.CollisionRadius, cyl.CollisionHeight, cdo.MaxStepHeight, cdo.WalkableFloorZ);
        log.Write($"nav: soldier radius {radius:0}, half height {halfHeight:0}, step {step:0}, walkable floor z {walkableZ:0.00}" +
                  (cdo != null ? "" : " (defaults: class not found)"));
    }

    // ---- cache file: header, node floors, then per node a link count and the links
    void Save()
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            using var w = new BinaryWriter(File.Create(CachePath));
            w.Write(Magic);
            w.Write(Version);
            w.Write(Spacing);
            w.Write(radius);
            w.Write(halfHeight);
            w.Write(step);
            w.Write(walkableZ);
            w.Write((uint)floor.Count);
            w.Write((uint)linkCount);
            foreach (FVector f in floor)
            {
                w.Write(f.X);
                w.Write(f.Y);
                w.Write(f.Z);
            }
            for (int i = 0; i < floor.Count; ++i)
            {
                int n = linkStart[i + 1] - linkStart[i];
                w.Write((byte)n);
                for (int k = linkStart[i]; k < linkStart[i + 1]; ++k)
                    w.Write(links[k]);
            }
            log.Write($"nav: saved {CachePath}");
        }
        catch (IOException e)
        {
            log.Warn($"nav: can't write {CachePath}: {e.Message}");
        }
    }

    bool Load()
    {
        if (!File.Exists(CachePath))
            return false;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(CachePath), 1 << 16));
            bool ok = r.ReadUInt32() == Magic && r.ReadUInt32() == Version && r.ReadSingle() == Spacing &&
                      r.ReadSingle() == radius && r.ReadSingle() == halfHeight && r.ReadSingle() == step &&
                      r.ReadSingle() == walkableZ;
            uint nodes = r.ReadUInt32(), linksTotal = r.ReadUInt32();
            if (!ok || nodes > MaxNodes)
                throw new InvalidDataException("stale header");
            for (uint i = 0; i < nodes; ++i)
                floor.Add(new FVector(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            linkStart = new int[nodes + 1];
            var packed = new List<uint>((int)linksTotal);
            for (uint i = 0; i < nodes; ++i)
            {
                linkStart[i] = packed.Count;
                int n = r.ReadByte();
                for (int k = 0; k < n; ++k)
                {
                    uint b = r.ReadUInt32();
                    if (b >= nodes)
                        throw new InvalidDataException("link out of range");
                    packed.Add(b);
                }
            }
            linkStart[nodes] = packed.Count;
            links = packed.ToArray();
            linkCount = linksTotal;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
        {
            log.Write($"nav: cache {CachePath} is stale or damaged ({e.Message}); rebuilding");
            Reset();
            return false;
        }
        for (int i = 0; i < floor.Count; ++i)
        {
            long key = CellKey(CellOf(floor[i].X), CellOf(floor[i].Y));
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = new List<int>(1);
            list.Add(i);
        }
        return true;
    }

    // Packs the build-time link lists into linkStart/links.
    void Pack()
    {
        linkStart = new int[floor.Count + 1];
        links = new uint[linkCount];
        int at = 0;
        for (int i = 0; i < floor.Count; ++i)
        {
            linkStart[i] = at;
            foreach (uint b in adj[i])
                links[at++] = b;
        }
        linkStart[floor.Count] = at;
        adj.Clear();
    }

    void Finish()
    {
        Pack();
        phase = Phase.Ready;
        queue.Clear();
        log.Write($"nav: built {map}: {floor.Count} nodes{(floor.Count >= MaxNodes ? " (CAPPED)" : "")}, {linkCount} links, " +
                  $"{traces} traces, {buildMs / 1000.0:0.0} s of build time");
        if (floor.Count == 0)
            log.Write($"nav: empty graph for {map} (no spawn point had room for a soldier); not cached");
        else
            Save();
        FindLadders();
    }

    /// <summary>Forgets the graph (map change).</summary>
    public void Reset()
    {
        phase = Phase.Idle;
        floor.Clear();
        adj.Clear();
        cells.Clear();
        queue.Clear();
        linkStart = Array.Empty<int>();
        links = Array.Empty<uint>();
        linkCount = 0;
        ladders.Clear();
        ladderLinks.Clear();
        traces = 0;
        buildMs = 0;
    }

    /// <summary>Starts building (or loads the cache) for the current map. <paramref name="gameInfo"/> is the trace source.
    /// Call it once the map's streaming levels have settled; false if the map has no spawn points or objectives.</summary>
    public bool Begin(GameInfo gameInfo, string mapName, bool rebuild = false)
    {
        Reset();
        game = gameInfo;
        world = Game.World;
        map = mapName;
        ReadBody();
        if (world == null)
            return false;
        if (!rebuild && Load())
        {
            phase = Phase.Ready;
            log.Write($"nav: loaded {CachePath}: {floor.Count} nodes, {linkCount} links");
            FindLadders();
            return true;
        }
        // Only this map's actors: every placed actor (streamed levels too) points at the world's one WorldInfo.
        WorldInfo? worldInfo = gameInfo.WorldInfo;
        // The playable area: the ground combat volumes' bounds (leaving them warns "return to the combat area").
        // Without them, the spawn points and objectives plus a margin.
        bool bounded = false;
        foreach (GameMode_CombatVolume v in Objects.All<GameMode_CombatVolume>())
        {
            if (v.WorldInfo != worldInfo || !v.bGroundVolumeType || v.BrushComponent is not BrushComponent brush)
                continue;
            ref FBoxSphereBounds b = ref brush.Bounds;
            float x0 = b.Origin.X - b.BoxExtent.X, x1 = b.Origin.X + b.BoxExtent.X;
            float y0 = b.Origin.Y - b.BoxExtent.Y, y1 = b.Origin.Y + b.BoxExtent.Y;
            if (!bounded)
                (minX, maxX, minY, maxY) = (x0, x1, y0, y1);
            (minX, maxX) = (MathF.Min(minX, x0), MathF.Max(maxX, x1));
            (minY, maxY) = (MathF.Min(minY, y0), MathF.Max(maxY, y1));
            bounded = true;
        }
        bool Inside(FVector v) => v.X >= minX && v.X <= maxX && v.Y >= minY && v.Y <= maxY;
        // Flood-fill starts: every placed spawn point and objective in the area, in object-table order.
        var seeds = new List<FVector>();
        nint starts = UObject.ClassPtr<PlayerStart>(), objectives = UObject.ClassPtr<Objective_GameMode>();
        for (int i = 0; i < Objects.Count; ++i)
        {
            nint o = Objects.At(i);
            if (o == 0 || !(Objects.IsA(o, starts) || Objects.IsA(o, objectives)))
                continue;
            var actor = UObject.Get<Actor>(o);
            if (actor == null || actor.WorldInfo != worldInfo)
                continue;
            FVector loc = actor.Location;
            if (!bounded || Inside(loc))
                seeds.Add(loc);
        }
        if (seeds.Count == 0)
            return false;
        if (!bounded)
        {
            (minX, maxX, minY, maxY) = (seeds[0].X, seeds[0].X, seeds[0].Y, seeds[0].Y);
            foreach (FVector s in seeds)
            {
                (minX, maxX) = (MathF.Min(minX, s.X), MathF.Max(maxX, s.X));
                (minY, maxY) = (MathF.Min(minY, s.Y), MathF.Max(maxY, s.Y));
            }
            (minX, minY, maxX, maxY) = (minX - Margin, minY - Margin, maxX + Margin, maxY + Margin);
        }
        // Ladder tops too: platforms often reached only by climbing, which a fill walking out from the spawn points
        // never gets to. The spot is past the ladder on the side the climber faces, a little above the volume's top
        // (Seed searches down from there for the floor).
        foreach (LadderVolume v in Objects.All<LadderVolume>())
        {
            if (v.WorldInfo != worldInfo || v.BrushComponent is not BrushComponent brush)
                continue;
            ref FBoxSphereBounds b = ref brush.Bounds;
            FVector look = new FVector(v.LookDir.X, v.LookDir.Y, 0).Normal;
            float past = MathF.Max(b.BoxExtent.X, b.BoxExtent.Y) + 40f;
            var top = new FVector(b.Origin.X + look.X * past, b.Origin.Y + look.Y * past, b.Origin.Z + b.BoxExtent.Z + 20f);
            if (!bounded || Inside(top))
                seeds.Add(top);
        }
        phase = Phase.Building;
        foreach (FVector s in seeds)
            Seed(s);
        nextProgressLog = Environment.TickCount64 + 10000;
        log.Write($"nav: building {map} from {seeds.Count} seeds ({floor.Count} usable), " +
                  $"{(bounded ? "ground combat area" : "area around them")} {maxX - minX:0} x {maxY - minY:0}");
        return true;
    }

    /// <summary>Spends up to <paramref name="budgetMs"/> building. Call every tick; does nothing once ready.</summary>
    public void Tick(double budgetMs)
    {
        if (phase != Phase.Building)
            return;
        if (game == null || !game.IsValid)
        {
            log.Write("nav: game went away while building; dropped");
            Reset();
            return;
        }
        long start = Stopwatch.GetTimestamp();
        long limit = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
        long now = start;
        while (queue.Count > 0 && now - start < limit)
        {
            Expand(queue.Dequeue());
            now = Stopwatch.GetTimestamp();
        }
        buildMs += (now - start) * 1000.0 / Stopwatch.Frequency;
        if (queue.Count == 0)
        {
            Finish();
        }
        else if (Environment.TickCount64 >= nextProgressLog)
        {
            log.Write($"nav: {Status}");
            nextProgressLog = Environment.TickCount64 + 10000;
        }
    }

    public string Status => phase switch
    {
        Phase.Idle => "no graph",
        Phase.Building => $"building {map}: {floor.Count} nodes, {queue.Count} waiting, {traces} traces, {buildMs / 1000.0:0} s",
        _ => $"{map}: {floor.Count} nodes, {linkCount} links",
    };

    /// <summary>Where a soldier stands at a node (floor + half height).</summary>
    public FVector Stand(int node)
    {
        FVector f = floor[node];
        f.Z += halfHeight;
        return f;
    }

    /// <summary>The node nearest a standing position, preferring nodes at the same height; -1 if none within
    /// <paramref name="maxDist"/>.</summary>
    public int Nearest(FVector at, float maxDist = 250f)
    {
        if (!Ready)
            return -1;
        float floorZ = at.Z - halfHeight;
        int cx = CellOf(at.X), cy = CellOf(at.Y), r = (int)MathF.Ceiling(maxDist / Spacing);
        int best = -1;
        float bestD = maxDist * maxDist;
        for (int dx = -r; dx <= r; ++dx)
            for (int dy = -r; dy <= r; ++dy)
            {
                if (!cells.TryGetValue(CellKey(cx + dx, cy + dy), out var list))
                    continue;
                foreach (int n in list)
                {
                    FVector f = floor[n];
                    // Height counts triple: a node on this floor beats a closer one on the floor above.
                    float ex = f.X - at.X, ey = f.Y - at.Y, ez = (f.Z - floorZ) * 3f;
                    float d = ex * ex + ey * ey + ez * ez;
                    if (d < bestD)
                    {
                        best = n;
                        bestD = d;
                    }
                }
            }
        return best;
    }

    /// <summary>A random node within <paramref name="radius"/> of a point (any node for radius &lt;= 0); -1 if none.</summary>
    public int RandomNear(FVector at, float radius)
    {
        if (!Ready || floor.Count == 0)
            return -1;
        if (radius <= 0)
            return rng.Next(floor.Count);
        for (int tries = 0; tries < 40; ++tries)
        {
            float ox = (rng.NextSingle() * 2 - 1) * radius, oy = (rng.NextSingle() * 2 - 1) * radius;
            if (ox * ox + oy * oy > radius * radius)
                continue;
            if (cells.TryGetValue(CellKey(CellOf(at.X + ox), CellOf(at.Y + oy)), out var list) && list.Count > 0)
                return list[rng.Next(list.Count)];
        }
        return -1;
    }

    // A* state, stamped per search so the arrays aren't cleared every time.
    float[] cost = Array.Empty<float>();
    int[] parent = Array.Empty<int>();
    uint[] stamp = Array.Empty<uint>();
    uint search;

    /// <summary>The shortest path between two nodes, both included; empty if unreachable.</summary>
    public List<int> FindPath(int from, int to)
    {
        int n = floor.Count;
        var path = new List<int>();
        if (!Ready || from < 0 || to < 0 || from >= n || to >= n)
            return path;
        if (cost.Length != n)
        {
            cost = new float[n];
            parent = new int[n];
            stamp = new uint[n];
        }
        ++search;
        FVector goal = floor[to];
        var open = new PriorityQueue<int, float>();
        cost[from] = 0;
        parent[from] = -1;
        stamp[from] = search;
        open.Enqueue(from, Dist(floor[from], goal));
        while (open.TryDequeue(out int u, out float f))
        {
            if (u == to)
                break;
            if (f > cost[u] + Dist(floor[u], goal) + 0.5f)
                continue;  // a stale entry
            for (int k = linkStart[u]; k < linkStart[u + 1]; ++k)
            {
                int v = (int)links[k];
                Relax(u, v, cost[u] + Dist(floor[u], floor[v]));
            }
            if (ladderLinks.TryGetValue(u, out var climbs))
                foreach (var (v, _) in climbs)
                    Relax(u, v, cost[u] + MathF.Abs(floor[v].Z - floor[u].Z) * 1.5f + 150f);  // climbing is slow
        }
        if (stamp[to] != search)
            return path;
        for (int v = to; v >= 0; v = parent[v])
            path.Add(v);
        path.Reverse();
        return path;

        void Relax(int u, int v, float c)
        {
            if (stamp[v] == search && c >= cost[v])
                return;
            stamp[v] = search;
            cost[v] = c;
            parent[v] = u;
            open.Enqueue(v, c + Dist(floor[v], goal));
        }
    }

    // ---- straight lines over the graph

    // The node in cell (cx, cy) that `from` links to, or -1.
    int LinkedInCell(int from, int cx, int cy)
    {
        for (int k = linkStart[from]; k < linkStart[from + 1]; ++k)
        {
            int v = (int)links[k];
            if (CellOf(floor[v].X) == cx && CellOf(floor[v].Y) == cy)
                return v;
        }
        return -1;
    }

    /// <summary>Whether a soldier can walk straight from node <paramref name="a"/> to node <paramref name="b"/>: every
    /// grid cell on the line between them (8-connected) holds a node linked from the one before. Each link was tested
    /// with a body sweep when the graph was built, so the line only uses proven moves: no walls, no ledges to climb,
    /// drops only downward. Lines longer than <paramref name="maxCells"/> cells count as not walkable.</summary>
    public bool WalkableLine(int a, int b, int maxCells = 64)
    {
        if (a == b)
            return true;
        int x = CellOf(floor[a].X), y = CellOf(floor[a].Y);
        int x1 = CellOf(floor[b].X), y1 = CellOf(floor[b].Y);
        int dx = Math.Abs(x1 - x), dy = Math.Abs(y1 - y);
        if (Math.Max(dx, dy) > maxCells)
            return false;
        int sx = x < x1 ? 1 : -1, sy = y < y1 ? 1 : -1, err = dx - dy, at = a;
        while (x != x1 || y != y1)
        {
            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
            at = LinkedInCell(at, x, y);
            if (at < 0)
                return false;
        }
        return at == b;
    }

    /// <summary>A path with its grid zig-zags pulled straight: from each kept node, the farthest later node (up to 48
    /// ahead) reachable in a <see cref="WalkableLine"/>. Ladder climbs stay as they are.</summary>
    public List<int> Smooth(List<int> path)
    {
        if (path.Count <= 2)
            return path;
        var kept = new List<int> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int next = i + 1;
            for (int j = Math.Min(path.Count - 1, i + 48); j > i + 1; --j)
                if (WalkableLine(path[i], path[j]))
                {
                    next = j;
                    break;
                }
            kept.Add(path[next]);
            i = next;
        }
        return kept;
    }

    /// <summary>A spot to move to while fighting: a node 120-400 units away that can be walked to in a straight line,
    /// preferably sideways to the enemy (harder to hit, still facing it). -1 if none.</summary>
    public int StrafeSpot(FVector at, FVector enemy)
    {
        int from = Nearest(at);
        if (from < 0)
            return -1;
        var toEnemy = new FVector(enemy.X - at.X, enemy.Y - at.Y, 0).Normal;
        int best = -1;
        float bestScore = float.MaxValue;
        for (int tries = 0; tries < 12; ++tries)
        {
            int n = RandomNear(at, 400f);
            if (n < 0 || n == from)
                continue;
            var d = new FVector(floor[n].X - at.X, floor[n].Y - at.Y, 0);
            float len = d.Size;
            if (len < 120f || !WalkableLine(from, n, 16))
                continue;
            float score = MathF.Abs(FVector.Dot(d / len, toEnemy)) + rng.NextSingle() * 0.3f;  // 0 = sideways
            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }
        return best;
    }

    // ---- ladders

    /// <summary>The ladder linking two consecutive path nodes, or null when the step between them is walked.</summary>
    public LadderClimb? LadderBetween(int a, int b)
    {
        if (ladderLinks.TryGetValue(a, out var climbs))
            foreach (var (to, ladder) in climbs)
                if (to == b)
                    return ladders[ladder];
        return null;
    }

    public IReadOnlyList<LadderClimb> Ladders => ladders;

    // Links the foot and the top of every LadderVolume of this map. The foot is a node on the floor under the volume,
    // in front of the ladder; the top is a node level with the volume's top, beyond the ladder: a climber faces the
    // wall (LookDir) and steps off onto it.
    void FindLadders()
    {
        ladders.Clear();
        ladderLinks.Clear();
        WorldInfo? info = game?.WorldInfo;
        if (floor.Count == 0 || info == null)
            return;
        int skipped = 0;
        foreach (LadderVolume v in Objects.All<LadderVolume>())
        {
            if (v.WorldInfo != info || v.BrushComponent is not BrushComponent brush)
                continue;
            ref FBoxSphereBounds b = ref brush.Bounds;
            FVector min = b.Origin - b.BoxExtent, max = b.Origin + b.BoxExtent;
            FVector look = new FVector(v.LookDir.X, v.LookDir.Y, 0).Normal;
            FVector climb = v.ClimbDir.Normal;
            if (climb.SizeSquared < 0.5f)
                climb = new FVector(0, 0, 1);
            float reach = MathF.Max(b.BoxExtent.X, b.BoxExtent.Y) + 160f;
            int bottom = NodeNear(new FVector(b.Origin.X - look.X * 48f, b.Origin.Y - look.Y * 48f, min.Z), reach,
                min.Z - 64f, min.Z + 64f);
            // A volume can reach well above the platform it leads to (140 units on fl-angelisland).
            int top = NodeNear(new FVector(b.Origin.X + look.X * 64f, b.Origin.Y + look.Y * 64f, max.Z), reach,
                max.Z - 260f, max.Z + 64f);
            if (bottom < 0 || top < 0 || floor[top].Z - floor[bottom].Z < 96f)
            {
                ++skipped;
                continue;
            }
            int index = ladders.Count;
            ladders.Add(new LadderClimb { Volume = v, Bottom = bottom, Top = top, ClimbDir = climb, LookDir = look, Min = min, Max = max });
            AddLadderLink(bottom, top, index);
            AddLadderLink(top, bottom, index);
        }
        if (ladders.Count > 0 || skipped > 0)
            log.Write($"nav: {ladders.Count} ladder(s) linked{(skipped > 0 ? $", {skipped} without a floor at both ends" : "")}");
    }

    void AddLadderLink(int from, int to, int ladder)
    {
        if (!ladderLinks.TryGetValue(from, out var list))
            ladderLinks[from] = list = new List<(int, int)>();
        list.Add((to, ladder));
    }

    // The node nearest `at` (flat distance, within `reach`) whose floor is between zMin and zMax, or -1.
    int NodeNear(FVector at, float reach, float zMin, float zMax)
    {
        int cx = CellOf(at.X), cy = CellOf(at.Y), r = (int)MathF.Ceiling(reach / Spacing);
        int best = -1;
        float bestD = reach * reach;
        for (int dx = -r; dx <= r; ++dx)
            for (int dy = -r; dy <= r; ++dy)
            {
                if (!cells.TryGetValue(CellKey(cx + dx, cy + dy), out var list))
                    continue;
                foreach (int n in list)
                {
                    FVector f = floor[n];
                    if (f.Z < zMin || f.Z > zMax)
                        continue;
                    float d = (f.X - at.X) * (f.X - at.X) + (f.Y - at.Y) * (f.Y - at.Y);
                    if (d < bestD)
                    {
                        best = n;
                        bestD = d;
                    }
                }
            }
        return best;
    }

    /// <summary>The graph for the editor: node floor points and links, packed.</summary>
    public (IReadOnlyList<FVector> floor, int[] linkStart, uint[] links) Graph => (floor, linkStart, links);
}

/// <summary>A climbable LadderVolume, linked into the graph between the node at its foot and the node at its top.</summary>
sealed class LadderClimb
{
    public required LadderVolume Volume;
    public required int Bottom, Top;
    public FVector ClimbDir;  // up the ladder (unit)
    public FVector LookDir;   // flat, toward the wall: the way a climber faces
    public FVector Min, Max;  // the volume's bounds

    /// <summary>Whether a point is inside the volume's bounds, grown sideways by <paramref name="side"/>, upward by
    /// <paramref name="above"/> and downward by <paramref name="below"/>.</summary>
    public bool Contains(FVector p, float side, float above, float below) =>
        p.X >= Min.X - side && p.X <= Max.X + side && p.Y >= Min.Y - side && p.Y <= Max.Y + side &&
        p.Z >= Min.Z - below && p.Z <= Max.Z + above;
}
