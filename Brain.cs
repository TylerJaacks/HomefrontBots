using System;
using System.Collections.Generic;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>Where a bot goes and how it gets there: pick a goal (an active objective its team doesn't own, else one
/// to hold, else anywhere), path to it over the nav graph, and steer the pawn along the path. Fighting stays with the
/// game's own GCWeaponAIController (states Searching/Attacking): while it attacks, the bot stands and shoots.</summary>
sealed class Brain
{
    const float Reach = 40f;              // a waypoint this close (flat distance) is reached
    const float ObjectiveArea = 450f;     // hang around within this of an objective being taken or held
    const long StuckCheckMs = 1500;
    const float StuckMove = 40f;          // moved less than this between checks: stuck

    List<int> path = new();               // nav nodes, start..goal
    int next;                             // the node being walked to
    string goal = "";                     // what for, for "!bots list"
    bool atObjective;                     // the goal is an objective: hang around it when there
    long waitUntil;                       // no new goal before this (ms)
    long checkAt;                         // next stuck check
    FVector checkPos;
    int stuck;

    /// <summary>The path being walked, for the editor (node indices).</summary>
    public IReadOnlyList<int> Path => path;
    public int Next => next;
    public string Goal => goal;

    /// <summary>Forgets the path (death, teleport).</summary>
    public void Reset()
    {
        path.Clear();
        next = 0;
        atObjective = false;
        stuck = 0;
    }

    public string Describe() =>
        path.Count == 0 ? (goal.Length == 0 ? "idle" : goal) : $"{goal} {next}/{path.Count}";

    /// <summary>Every tick, for a bot with a pawn. <paramref name="mayPlan"/> is false when another bot already
    /// searched a path this tick (a search across the map can take tens of ms).</summary>
    public bool Update(BotsScript mod, string who, Controller c, Pawn pawn, int team, long now, bool mayPlan)
    {
        Nav nav = mod.Nav;
        if (!nav.Ready)
            return false;
        ref FVector loc = ref pawn.Location;
        ref FVector accel = ref pawn.Acceleration;
        // Fighting is the game AI's (GCWeaponAIController aims and fires in Attacking): stand and let it.
        if (c.StateName == "Attacking")
        {
            accel = default;
            return false;
        }
        bool planned = false;
        if (next >= path.Count)
        {
            accel = default;
            if (now < waitUntil || !mayPlan)
                return false;
            planned = true;
            NewGoal(mod, who, loc, team, c.WorldInfo);
            checkAt = now + StuckCheckMs;
            checkPos = loc;
            if (path.Count == 0)
            {
                waitUntil = now + 2000;
                return planned;
            }
        }

        FVector target = nav.Stand(path[next]);
        while (FVector.Dist2D(loc, target) < Reach && MathF.Abs(loc.Z - target.Z) < 80f)
        {
            if (++next >= path.Count)
            {
                // There. At an objective, wait a little and then pick another spot in its area (stays capturing).
                accel = default;
                waitUntil = now + (atObjective ? 2000 + mod.Random.Next(6000) : 500);
                path.Clear();
                return planned;
            }
            target = nav.Stand(path[next]);
        }

        // Steer: accelerate toward the waypoint, and set the walking velocity outright so turns are tight.
        float dx = target.X - loc.X, dy = target.Y - loc.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1f)
        {
            dx /= len;
            dy /= len;
        }
        accel = new FVector(dx * pawn.AccelRate, dy * pawn.AccelRate, 0);
        if (pawn.Physics == EPhysics.PHYS_Walking)
        {
            ref FVector vel = ref pawn.Velocity;
            vel.X = dx * pawn.GroundSpeed;
            vel.Y = dy * pawn.GroundSpeed;
        }
        // Look where we're going; the Searching state's view sweep would otherwise turn the bot around.
        c.SetFocalPoint(new FVector(loc.X + dx * 500f, loc.Y + dy * 500f, target.Z), false);

        if (now >= checkAt)
        {
            if (FVector.Dist2D(loc, checkPos) < StuckMove)
            {
                ++stuck;
                if (stuck == 2)
                    pawn.DoJump(false);
                if (stuck >= 4)
                {
                    mod.Log.Write($"brain: {who} stuck near {loc} going to {goal}; new goal");
                    Reset();
                }
            }
            else
            {
                stuck = 0;
            }
            checkPos = loc;
            checkAt = now + StuckCheckMs;
        }
        return planned;
    }

    void NewGoal(BotsScript mod, string who, FVector at, int team, WorldInfo? worldInfo)
    {
        Reset();
        Nav nav = mod.Nav;
        int from = nav.Nearest(at);
        if (from < 0)
        {
            goal = "off the nav graph";
            return;
        }
        int to = -1;
        if (PickObjective(mod, worldInfo, team, at, out FVector objective, out string what))
        {
            to = nav.RandomNear(objective, ObjectiveArea);
            atObjective = to >= 0;
            goal = what;
        }
        if (to < 0)
        {
            to = nav.RandomNear(at, 3000f);
            goal = "roam";
        }
        path = nav.FindPath(from, to);
        if (path.Count == 0)
        {
            mod.Log.Write($"brain: {who}: no path for {goal} (node {from} -> {to})");
            goal += " (no path)";
            return;
        }
        mod.Log.Write($"brain: {who} -> {goal}, {path.Count} nodes");
    }

    // The objective to go for: an active one the team doesn't own, else an active one it owns (to hold). Nearer is
    // better, with some randomness so bots spread over the objectives.
    static bool PickObjective(BotsScript mod, WorldInfo? worldInfo, int team, FVector from, out FVector at, out string what)
    {
        at = default;
        what = "";
        float best = 0;
        Objective_GameMode? pick = null;
        foreach (Objective_GameMode o in mod.Objectives(worldInfo))
        {
            if (!o.IsValid || !o.bIsActive)
                continue;
            bool ours = (int)o.OwningTeam == team;
            FVector loc = o.Location;
            float score = FVector.Dist2D(from, loc) + mod.Random.NextSingle() * 2500f + (ours ? 100000f : 0f);
            if (pick == null || score < best)
            {
                pick = o;
                best = score;
                at = loc;
                what = (ours ? "hold " : "take ") + o.ObjectName;
            }
        }
        return pick != null;
    }
}
