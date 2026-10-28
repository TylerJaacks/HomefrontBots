using System;
using System.Collections.Generic;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>Where a bot goes and how it gets there: pick a goal (Ground Control: an active objective its team doesn't
/// own, else one to hold; Team Deathmatch, or nothing to take: an enemy to hunt; else anywhere), path to it over the
/// nav graph (pulled straight where the floor allows), and steer the pawn along the path, climbing ladders on the way.
/// An enemy in plain sight is faced so the game's own GCWeaponAIController notices it; that AI aims and fires
/// (states Searching/Attacking), and while it attacks the bot strafes between nearby spots.</summary>
internal sealed class Brain
{
    private const float Reach = 40f;              // a waypoint this close (flat distance) is reached
    private const float ObjectiveArea = 450f;     // hang around within this of an objective being taken or held
    private const long StuckCheckMs = 1500;
    private const float StuckMove = 40f;          // moved less than this between checks: stuck
    private const float FightSpeed = 0.6f;        // of GroundSpeed, while strafing in a fight
    private const long ClimbTimeoutMs = 12000;    // a ladder not climbed by then: give up, new goal
    private const float SightRange = 4000f;       // as far as GCMPBot engages soldiers (MaxTargetDistSoldier)
    private const long HuntCheckMs = 4000;        // how often a hunter checks its quarry moved (or died)
    private const float QuarryMoved = 800f;       // moved this far from where the route was planned to: new route

    private List<int> _path = [];               // nav nodes, start..goal (smoothed: waypoints)
    private bool _atObjective;                     // the goal is an objective: hang around it when there
    private long _waitUntil;                       // no new goal before this (ms)
    private long _checkAt;                         // next stuck check
    private FVector _checkPos;
    private int _stuck;
    private int _fightSpot = -1;                   // the node it strafes to while fighting
    private long _fightUntil;                      // when to pick the next one
    private bool _fought;                          // fought last tick: plan a new route when the fight ends
    private long _climbSince;                      // when the current ladder step started (0: not on one)
    private bool _smooth = true;                   // false after getting stuck: the next route follows the raw grid
    private Pawn? _seen;                           // an enemy in plain sight: faced so the game's AI notices it
    private long _lookAt;                          // next sight check
    private Pawn? _quarry;                         // the enemy being hunted
    private FVector _quarryAt;                     // where it was when the route was planned
    private long _huntCheckAt;

    /// <summary>The path being walked, for the editor (node indices).</summary>
    public IReadOnlyList<int> Path => _path;
    public int Next { get; private set; }

    public string Goal { get; private set; } = "";

    /// <summary>Forgets the path (death, teleport).</summary>
    public void Reset()
    {
        _path.Clear();
        Next = 0;
        _atObjective = false;
        _stuck = 0;
        _climbSince = 0;
        _quarry = null;
    }

    /// <summary>Walks a given path (for tests such as "!bots ladder").</summary>
    public void Follow(List<int> path, string goal)
    {
        Reset();

        _path = path;
        Goal = goal;
    }

    public string Describe() =>
        _fought ? "fighting" : _path.Count == 0 ? (Goal.Length == 0 ? "idle" : Goal) : $"{Goal} {Next}/{_path.Count}";

    /// <summary>Every tick, for a bot with a pawn. <paramref name="mayPlan"/> is false when another bot already
    /// searched a path this tick (a search across the map can take tens of ms).</summary>
    public bool Update(BotsScript mod, string who, Controller c, Pawn pawn, int team, long now, bool mayPlan)
    {
        var nav = mod.Nav;

        if (!nav.Ready)
            return false;

        ref var loc = ref pawn.Location;
        ref var accel = ref pawn.Acceleration;

        // Fighting is the game AI's (GCWeaponAIController aims and fires in Attacking): keep moving sideways between
        // nearby spots so the bot is harder to hit. Not halfway up a ladder, though.
        if (c.StateName == "Attacking" && pawn.Physics != EPhysics.PHYS_Ladder)
        {
            Fight(nav, c, pawn, now, mod.Random);

            return false;
        }

        // The fight moved the bot off its route: plan a new one.
        if (_fought)
        {
            _fought = false;
            _fightSpot = -1;

            Reset();
        }

        Look(mod, pawn, team, now);

        // Hunting: the quarry moved on (or died) since the route was planned.
        if (_quarry != null && now >= _huntCheckAt)
        {
            _huntCheckAt = now + HuntCheckMs;

            if (!_quarry.Alive || _quarry.Health <= 0 || FVector.Dist2D(_quarry.Location, _quarryAt) > QuarryMoved)
                Reset();
        }

        var planned = false;

        if (Next >= _path.Count)
        {
            accel = default;

            if (now < _waitUntil || !mayPlan)
                return false;

            planned = true;

            NewGoal(mod, who, loc, team, c.WorldInfo);

            _checkAt = now + StuckCheckMs;
            _checkPos = loc;

            if (_path.Count == 0)
            {
                _waitUntil = now + 2000;
                return planned;
            }
        }

        var target = nav.Stand(_path[Next]);

        while (FVector.Dist2D(loc, target) < Reach && MathF.Abs(loc.Z - target.Z) < 80f && pawn.Physics != EPhysics.PHYS_Ladder)
        {
            _climbSince = 0;

            if (++Next >= _path.Count)
            {
                // There. At an objective, wait a little and then pick another spot in its area (stays capturing).
                accel = default;

                _waitUntil = now + (_atObjective ? 2000 + mod.Random.Next(6000) : 500);
                _smooth = true;

                _path.Clear();

                return planned;
            }

            target = nav.Stand(_path[Next]);
        }

        var ladder = Next > 0 ? nav.LadderBetween(_path[Next - 1], _path[Next]) : null;

        if (ladder != null && Climb(mod, who, c, pawn, ladder, target, now))
            return planned;

        // Look where it's going, or at an enemy in sight (the AI then notices it and fights).
        var seen = _seen is { Alive: true } s ? s : null;

        Steer(c, pawn, target, 1f, look: seen == null);

        if (seen != null)
            c.SetFocalPoint(Eye(seen), false);

        if (now < _checkAt || pawn.Physics == EPhysics.PHYS_Ladder) return planned;

        if (FVector.Dist2D(loc, _checkPos) < StuckMove)
        {
            ++_stuck;

            switch (_stuck)
            {
                case 2:
                    pawn.DoJump(false);
                    break;
                case 3 when _smooth:
                    // A straightened line can clip a corner the grid route goes around: next route, the raw grid.
                    mod.Log.Write($"brain: {who} stuck near {loc} going to {Goal}; trying the grid route");
                    _smooth = false;
                    Reset();
                    break;
                case >= 3:
                    mod.Log.Write($"brain: {who} stuck near {loc} going to {Goal}; new goal");
                    _smooth = true;
                    Reset();
                    break;
            }
        }
        else
        {
            _stuck = 0;
        }

        _checkPos = loc;
        _checkAt = now + StuckCheckMs;

        return planned;
    }

    // Accelerate toward `target`, and set the walking velocity outright so turns are tight. With `look`, face where
    // it's going (the Searching state's view sweep would otherwise turn the bot around); in a fight the AI aims.
    private static void Steer(Controller c, Pawn pawn, FVector target, float speed, bool look)
    {
        ref var loc = ref pawn.Location;

        float dx = target.X - loc.X, dy = target.Y - loc.Y, len = MathF.Sqrt(dx * dx + dy * dy);

        if (len > 1f)
        {
            dx /= len;
            dy /= len;
        }

        pawn.Acceleration = new FVector(dx * pawn.AccelRate * speed, dy * pawn.AccelRate * speed, 0);

        if (pawn.Physics == EPhysics.PHYS_Walking)
        {
            ref var vel = ref pawn.Velocity;

            vel.X = dx * pawn.GroundSpeed * speed;
            vel.Y = dy * pawn.GroundSpeed * speed;
        }

        // At eye level for where it's heading (a waypoint is where the body's centre goes, below the eyes).
        if (look)
            c.SetFocalPoint(new FVector(loc.X + dx * 500f, loc.Y + dy * 500f, target.Z + pawn.BaseEyeHeight), false);
    }

    private static FVector Eye(Pawn p)
    {
        var l = p.Location;

        return new FVector(l.X, l.Y, l.Z + p.BaseEyeHeight);
    }

    // Every half second or so: the nearest enemies in range, nearest first; the first with nothing of the world between
    // their eyes and the bot's is "seen".
    private void Look(BotsScript mod, Pawn pawn, int team, long now)
    {
        if (now < _lookAt)
            return;

        _lookAt = now + 400 + mod.Random.Next(200);
        _seen = null;

        if (team < 0)
            return;

        var eye = Eye(pawn);
        var near = new List<(float Dist, Pawn Pawn)>();

        foreach (var (other, otherTeam) in mod.Soldiers)
        {
            if (otherTeam == team || other == pawn)
                continue;

            var d = FVector.Dist(eye, other.Location);

            if (d < SightRange)
                near.Add((d, other));
        }

        near.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        for (var i = 0; i < near.Count && i < 3; ++i)
        {
            if (!mod.ClearLine(eye, Eye(near[i].Pawn)))
                continue;

            _seen = near[i].Pawn;
            return;
        }
    }

    // The quarry of the last route while it lives and is an enemy (Soldiers lists the living with a team), else null.
    private static Pawn? StillHunted(BotsScript mod, int team, Pawn? quarry)
    {
        if (quarry != null)
            foreach (var (other, otherTeam) in mod.Soldiers)
                if (other.Ptr == quarry.Ptr && otherTeam != team)
                    return quarry;

        return null;
    }

    // The enemy to hunt: a near one, with randomness so the bots don't all chase the same player.
    private bool PickQuarry(BotsScript mod, int team, FVector from, out Pawn? quarry)
    {
        quarry = null;

        var best = float.MaxValue;

        foreach (var (other, otherTeam) in mod.Soldiers)
        {
            if (team < 0 || otherTeam == team)
                continue;

            var score = FVector.Dist2D(from, other.Location) + mod.Random.NextSingle() * 3000f;

            if (score >= best)
                continue;

            best = score;
            quarry = other;
        }

        return quarry != null;
    }

    // Strafing in a fight: every second or two (or on arrival), a new spot a few metres away that can be walked to
    // in a straight line, preferably sideways to the enemy.
    private void Fight(Nav nav, Controller c, Pawn pawn, long now, Random random)
    {
        _fought = true;

        var loc = pawn.Location;

        if (_fightSpot < 0 || now >= _fightUntil || FVector.Dist2D(loc, nav.Stand(_fightSpot)) < Reach)
        {
            var enemy = c.Enemy is { IsValid: true } e ? e.Location : loc;

            _fightSpot = nav.StrafeSpot(loc, enemy);
            _fightUntil = now + 1200 + random.Next(1600);
        }

        if (_fightSpot < 0)
        {
            pawn.Acceleration = default;
            return;
        }

        Steer(c, pawn, nav.Stand(_fightSpot), FightSpeed, look: false);
    }

    // A ladder step (foot -> top or top -> foot). Walk into the volume facing the wall and get on (the engine's
    // LadderVolume does that for a pawn facing the ladder; ClimbLadder makes sure), then push along the ladder. Once
    // off it at the far end, the normal steering takes the bot to the next node. False: walk as usual.
    private bool Climb(BotsScript mod, string who, Controller c, Pawn pawn, LadderClimb ladder, FVector target, long now)
    {
        ref var loc = ref pawn.Location;

        if (_climbSince == 0)
            _climbSince = now;

        if (now - _climbSince > ClimbTimeoutMs)
        {
            mod.Log.Write($"brain: {who} couldn't climb {ladder.Volume.ObjectName} near {loc}; new goal");
            Reset();

            return true;
        }

        var up = target.Z > loc.Z;
        var face = new FVector(loc.X + ladder.LookDir.X * 500f, loc.Y + ladder.LookDir.Y * 500f, loc.Z);

        if (pawn.Physics == EPhysics.PHYS_Ladder)
        {
            // At the far end's level, or at the volume's end (a ladder volume can stop short of the floor): the ladder
            // physics would keep the pawn hanging there. Get off, with a hop onto the platform the climber faces at the
            // top (a step back from the wall at the foot, then a drop to the floor).
            if (up ? loc.Z >= MathF.Min(target.Z, ladder.Max.Z) - 20f : loc.Z <= MathF.Max(target.Z, ladder.Min.Z + 50f) + 20f)
            {
                pawn.EndClimbLadder(ladder.Volume);

                var off = up ? ladder.LookDir : -ladder.LookDir;

                pawn.SetPhysics(EPhysics.PHYS_Falling);
                pawn.Velocity = new FVector(off.X * pawn.GroundSpeed * 0.6f, off.Y * pawn.GroundSpeed * 0.6f, up ? 220f : 0f);
                pawn.Acceleration = off * pawn.AccelRate;

                return true;
            }

            pawn.Acceleration = (up ? ladder.ClimbDir : -ladder.ClimbDir) * pawn.AccelRate;

            c.SetFocalPoint(face, false);

            return true;
        }

        // Already at the far end's level: off the ladder, walk to the node.
        if (MathF.Abs(loc.Z - target.Z) < 80f)
            return false;

        var center = new FVector((ladder.Min.X + ladder.Max.X) / 2, (ladder.Min.Y + ladder.Max.Y) / 2, loc.Z);

        if (up)
        {
            // The volume can start above the floor: a pawn standing under it counts (its body reaches into it).
            if (pawn.OnLadder == null && ladder.Contains(loc, 4f, 4f, 60f))
            {
                pawn.ClimbLadder(ladder.Volume);
            }
            else if (now - _climbSince > 1500 && ladder.Contains(loc, 48f, 4f, 60f))
            {
                // Held off the ladder line by its own frame: step onto it and climb from there.
                pawn.Teleport(new FVector(center.X, center.Y, MathF.Max(loc.Z, ladder.Min.Z + 50f)));
                pawn.ClimbLadder(ladder.Volume);

                return true;
            }
        }
        else if (ladder.Contains(loc, 24f, 64f, 0f))
        {
            // Going down, the pawn on the floor above stands higher than the volume's top and the edge keeps it off
            // the ladder line: step it onto the ladder, just inside the volume's top, and climb from there.
            pawn.Teleport(new FVector(center.X, center.Y, ladder.Max.Z - 50f));
            pawn.ClimbLadder(ladder.Volume);

            return true;
        }

        Steer(c, pawn, center, 0.5f, look: false);

        c.SetFocalPoint(face, false);

        return true;
    }

    private void NewGoal(BotsScript mod, string who, FVector at, int team, WorldInfo? worldInfo)
    {
        // A hunter stays after its quarry while that lives, rather than drawing another enemy each time the route
        // runs out (they flip-flopped between enemies).
        var previous = StillHunted(mod, team, _quarry);
        var previousGoal = Goal;

        Reset();

        var nav = mod.Nav;

        var from = nav.Nearest(at);

        if (from < 0)
        {
            Goal = "off the nav graph";
            return;
        }

        var to = -1;

        // Ground Control: take or hold an objective. Team Deathmatch (or no objective active): hunt the other team.
        if (!mod.Hunting && PickObjective(mod, worldInfo, team, at, out var objective, out var what))
        {
            to = nav.RandomNear(objective, ObjectiveArea);
            _atObjective = to >= 0;
            Goal = what;
        }

        var quarry = previous;

        if (to < 0 && (quarry != null || PickQuarry(mod, team, at, out quarry)))
        {
            to = nav.Nearest(quarry!.Location, 400f);

            if (to >= 0)
            {
                _quarry = quarry;
                _quarryAt = quarry.Location;
                _huntCheckAt = Environment.TickCount64 + HuntCheckMs;
                Goal = "hunt " + (quarry.Controller?.PlayerName ?? quarry.ObjectName);
            }
        }

        if (to < 0)
        {
            to = nav.RandomNear(at, 3000f);
            Goal = "roam";
        }

        var nodes = nav.FindPath(from, to);

        _path = _smooth ? nav.Smooth(nodes) : nodes;

        if (_path.Count == 0)
        {
            mod.Log.Write($"brain: {who}: no path for {Goal} (node {from} -> {to})");
            Goal += " (no path)";
            _quarry = null;  // draw another next time
            return;
        }

        if (Goal != previousGoal)
            mod.Log.Write($"brain: {who} -> {Goal}, {nodes.Count} nodes, {_path.Count} waypoints");
    }

    // The objective to go for: an active one the team doesn't own, else an active one it owns (to hold). Nearer is
    // better, with some randomness so bots spread over the objectives.
    private static bool PickObjective(BotsScript mod, WorldInfo? worldInfo, int team, FVector from, out FVector at, out string what)
    {
        at = default;
        what = "";

        float best = 0;

        Objective_GameMode? pick = null;

        foreach (var o in mod.Objectives(worldInfo))
        {
            if (!o.IsValid || !o.bIsActive)
                continue;
            var ours = (int)o.OwningTeam == team;
            var loc = o.Location;
            var score = FVector.Dist2D(from, loc) + mod.Random.NextSingle() * 2500f + (ours ? 100000f : 0f);

            if (pick != null && !(score < best)) continue;

            pick = o;
            best = score;
            at = loc;
            what = (ours ? "hold " : "take ") + o.ObjectName;
        }

        return pick != null;
    }
}
