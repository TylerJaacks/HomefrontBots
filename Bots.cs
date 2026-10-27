using System;
using System.Collections.Generic;
using System.Linq;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>One player bot: a GCMPBot controller that joined like a player.</summary>
sealed class Bot
{
    public required GCMPBot Controller;
    public required GameInfo Game;
    public required string Name;
    public FPackedLoadout Loadout;
    public byte LoadoutIndex;
    public long NextSpawn;   // 0 while it has a pawn
    public int Failures;     // spawn attempts refused since it last had a pawn
    public readonly Brain Brain = new();
}

/// <summary>Bot lifecycle: join, team, spawn, respawn, teleports.</summary>
sealed class Bots
{
    const long RespawnDelayMs = 5000, RetryMs = 1000;
    // How far a bot engages soldiers. GCMPBot's default is 0 (sees everyone, engages no one); the developers'
    // spawner sets 4000 (AGCWeapon::ServerAddMPBot writes it at +0x548).
    const float TargetDistSoldier = 4000f;
    // PlayerSpawnManagerMP's spawn-point chooser, vtable byte offset (0x00BFE8A0 for the FL manager).
    const int ChooseSpawnPointSlot = 0x408;

    readonly BotsScript mod;
    readonly List<Bot> bots = new();
    int nextNumber = 1;

    public Bots(BotsScript mod) => this.mod = mod;

    public IReadOnlyList<Bot> All => bots;

    static int TeamOf(Controller? c) => c?.PlayerReplicationInfo?.Team is TeamInfo t ? (int)t.TeamIndex : -1;

    static bool Alive(Actor? a) => a != null && a.Alive;

    // GCWeaponAIController keeps its own team number (TeamNum), which its native GetTeamNum returns instead of the
    // PRI's team, and Pawn.GetTeamNum asks the controller. Friendly-fire checks (FL2GameMP.ReduceDamage) and the
    // bot's choice of targets go through it, so it has to follow the PRI team. The developers' spawner set it
    // natively (AGCWeapon::ServerAddMPBot writes it at +0x57C).
    static void SyncTeamNum(GCWeaponAIController c)
    {
        int team = TeamOf(c);
        if (team >= 0)
            c.TeamNum = (ETeam)team;
    }

    // Join a new bot the way the game's own single-player AddBot does (FL2GameSP.AddBot): spawn the controller,
    // give it a PRI and a name, put it on a team, hand out a player ID. GCMPBot defaults to bIsPlayer=false, so
    // Controller.PostBeginPlay skipped InitPlayerReplicationInfo and it's done here instead.
    GCMPBot? Join(GameInfo game, int team)
    {
        UClass? cls = UObject.Get<UClass>(UObject.ClassPtr<GCMPBot>());
        if (game.Spawn(cls, bNoCollisionFail: true) is not GCMPBot c)
        {
            mod.Log.Write("bots: Spawn(GCMPBot) failed");
            return null;
        }
        c.bIsPlayer = true;
        c.MaxTargetDistSoldier = TargetDistSoldier;
        c.InitPlayerReplicationInfo();
        PlayerReplicationInfo? pri = c.PlayerReplicationInfo;
        if (pri == null)
        {
            mod.Log.Write($"bots: {c.ObjectName} got no PlayerReplicationInfo; destroying it");
            c.Destroy();
            return null;
        }
        pri.SetPlayerName($"Bot {nextNumber++}");
        pri.bBot = true;
        pri.PlayerID = game.CurrentID++;
        // 0/1 as asked; 2 has FL2GameMP.PickTeam balance the teams (255 would mean "no team" there).
        if (!game.ChangeTeam(c, (ETeam)(team < 0 ? 2 : team), true))
            mod.Log.Write($"bots: ChangeTeam refused {c.PlayerName}");
        SyncTeamNum(c);
        game.NumBots++;
        return c;
    }

    // Places to put a pawn near `center`, nearest first: rings of 8 around it, about two pawn widths apart. With
    // `includeCenter`, the center comes first.
    static List<FVector> SpotsAround(FVector center, bool includeCenter)
    {
        var spots = new List<FVector>();
        if (includeCenter)
            spots.Add(center);
        foreach (float radius in new[] { 90f, 180f, 270f })
            for (int i = 0; i < 8; ++i)
                spots.Add(new FVector(center.X + radius * MathF.Cos(i * MathF.PI / 4), center.Y + radius * MathF.Sin(i * MathF.PI / 4), center.Z));
        return spots;
    }

    static FRotator Facing(FVector from, FVector to) =>
        new(0, (int)(MathF.Atan2(to.Y - from.Y, to.X - from.X) * 32768f / MathF.PI), 0);

    // Teleport a pawn to `spot` (a standing pawn's height near another pawn), raised a little so it doesn't clip the
    // ground, from where it falls under normal movement physics. Returns where the pawn went, or null.
    static FVector? Teleport(Pawn pawn, FVector spot)
    {
        spot.Z += 40f;
        if (!pawn.Teleport(spot))
            return null;
        pawn.SetMovementPhysics();
        return spot;
    }

    // PlayerSpawnManagerMP's spawn-point chooser (vtable slot 0x408): scores the team's spawn points (enemy/friend
    // proximity, recent deaths, randomness) for a spawn type. GetSpawnPoint (slot 0x3F0) wraps it but hands it the
    // controller, whose FL2PlayerControllerMP fields (LastPlayerStart, m_GCPRI) it then reads at fixed offsets -
    // garbage for an AI controller. With no controller it skips those.
    static unsafe NavigationPoint? ChooseSpawnPoint(PlayerSpawnManager manager, int team, byte spawnType)
    {
        nint fn = (*(nint**)manager.Ptr)[ChooseSpawnPointSlot / 4];
        try
        {
            return UObject.Get<NavigationPoint>((nint)Native.Call(fn, manager.Ptr, [(uint)team, spawnType, 0]));
        }
        catch (GameCrashException)
        {
            return null;
        }
    }

    // Spawn a pawn the way the native spawn manager does for humans (FL2GameMP.SpawnPlayer ->
    // PlayerSpawnManagerMP::SpawnPlayer, 0x00C05410). That function only accepts a GCPlayerController, so this runs
    // its steps with the manager's controller-free pieces: spawn point, team pawn class, possess, defaults, loadout
    // inventory. Returns false (and says why, now and then) when it can't spawn yet.
    bool SpawnPawn(Bot b)
    {
        GCMPBot c = b.Controller;
        GameInfo game = b.Game;
        if (!c.IsValid || !game.IsValid)
            return false;
        string why = "";
        var fl = game as FL2Game;
        var manager = fl?.SpawnManager as PlayerSpawnManagerMP;
        int team = TeamOf(c);
        NavigationPoint? point = null;
        Pawn? pawn = null;
        if (game.bGameEnded || game.bWaitingToStartMatch)
            why = "match not running";  // PlayerSpawnManager.PlayerIsAllowedToRestart's test
        else if (manager == null || game is not FL2GameMP mp)
            why = "no PlayerSpawnManagerMP / FL2GameMP";
        else if (team < 0 || team >= manager.PawnIndexToTypeMapping.Count || manager.PawnIndexToTypeMapping[team].SpawnTypes.Count < 1)
            why = "no team / spawn types";
        else if ((point = ChooseSpawnPoint(manager, team, (byte)manager.PawnIndexToTypeMapping[team].SpawnTypes[0])) == null)
            why = "no spawn point";
        else
        {
            // The soldier class, as the native picks it (0x00BFCCB0): the team that is FL2GameMP.m_LowTeam plays
            // Team0_PlayerClasses (KPA), the other Team1_PlayerClasses (US), indexed by the loadout's character
            // type. Index 0 is the assault soldier.
            ref TObjectArray<UClass> classes = ref team == (int)mp.m_LowTeam ? ref manager.Team0_PlayerClasses : ref manager.Team1_PlayerClasses;
            UClass? pawnClass = classes.Count > 0 ? classes[0] : null;
            // The native only checks the chosen point is free (0x00BF9EE0) and falls back to another one; a bot
            // standing on its spawn point keeps winning the scoring, so spawn beside the point instead.
            if (pawnClass != null)
                foreach (FVector spot in SpotsAround(point.Location, true))
                    if ((pawn = game.Spawn(pawnClass, SpawnLocation: spot, SpawnRotation: new FRotator(0, point.Rotation.Yaw, 0)) as Pawn) != null)
                        break;
            if (pawnClass == null)
                why = "no soldier class for team";
            else if (pawn == null)
                why = $"no room to spawn {pawnClass.ObjectName} at or around {point.ObjectName}";
        }
        if (why.Length > 0)
        {
            if (b.Failures++ % 30 == 0)
                mod.Log.Write($"bots: {b.Name} can't spawn yet ({why}; {b.Failures} tries); retrying every {RetryMs} ms");
            return false;
        }

        c.Possess(pawn, false);
        // Spawned pawns start without physics and hang where they appear (spawn points sit above the ground).
        pawn!.SetMovementPhysics();
        // A bot that died, or joined without a body, sits in state Dead, and Possess doesn't leave it. Searching
        // sweeps the view (PickViewDirection) and turns to Attacking when it sees an enemy (SeePlayer).
        c.GotoState(FName.Find("Searching"));
        // AddCharacterInventory gives the weapons in the PRI's loadout.
        if (c.PlayerReplicationInfo is GCPlayerReplicationInfo pri)
        {
            pri.CurrentPackedLoadout = b.Loadout;
            pri.LoadoutIndex = b.LoadoutIndex;
        }
        manager!.SetPlayerDefaults(pawn);
        manager.AddDefaultInventory(pawn);

        b.NextSpawn = 0;
        b.Failures = 0;
        mod.Log.Write($"bots: {b.Name} spawned as {pawn.ObjectName} at {point!.ObjectName} (team {team}, weapon " +
                      $"{pawn.Weapon?.ObjectName ?? "None"}, possessed {(c.Pawn == pawn ? "yes" : "NO")})");
        return true;
    }

    public void Add(CommandContext ctx, int count, int team)
    {
        GameInfo? game = Game.Info;
        if (game == null)
        {
            ctx.Reply("[bots] no game running");
            return;
        }
        // The requester's loadout (a tool has none: the first player's): what they last asked to spawn with, else
        // what they carry now.
        PlayerController? requester = ctx.Player ?? Game.Players.Find(p => p.PlayerReplicationInfo != null);
        var pri = requester?.PlayerReplicationInfo as GCPlayerReplicationInfo;
        FPackedLoadout loadout = requester is FL2PlayerControllerMP mp ? mp.RequestedPackedLoadout
            : pri != null ? pri.CurrentPackedLoadout : default;
        byte loadoutIndex = pri?.LoadoutIndex ?? 0;

        int added = 0, spawned = 0;
        for (; added < count; ++added)
        {
            GCMPBot? c = Join(game, team);
            if (c == null)
                break;
            var b = new Bot { Controller = c, Game = game, Name = c.PlayerName, Loadout = loadout, LoadoutIndex = loadoutIndex };
            mod.Log.Write($"bots: {b.Name} joined team {TeamOf(c)}");
            if (SpawnPawn(b))
                ++spawned;
            else
                b.NextSpawn = Environment.TickCount64 + RetryMs;
            bots.Add(b);
        }
        string reply = $"[bots] added {added} bot(s), {spawned} spawned";
        if (spawned < added)
            reply += "; the rest retry every second";
        if (added < count)
            reply += "; adding failed - see HomefrontBots.log";
        ctx.Reply(reply);
    }

    // Every GCMPBot controller, ours and the dummy spawner's, skipping the class default object.
    static List<GCMPBot> AllGCMPBots() =>
        Objects.All<GCMPBot>().Where(c => !c.ObjectName.StartsWith("Default__", StringComparison.Ordinal) && c.Alive).ToList();

    // The developers' bot spawner survives in the server: AGCWeapon::ServerAddMPBot (native, vtable 0x644) spawns
    // copies of the requester's pawn near them, driven by GCMPBot controllers named Bot_%i_%i. It's a server RPC on a
    // weapon, so call it on the requesting player's current weapon, as their dev tools did.
    //
    // It is gated on the requester's Controller.ReplicatedCheatInfo being set (only set natively when cheats are
    // allowed, never on this server). The function only null-checks it (one read, at 0x00D7B6B7), so for the
    // duration of the call we point it at the controller itself and then restore it: cheats stay off, and the change
    // never replicates because replication doesn't run inside our synchronous call.
    public unsafe void AddDummies(CommandContext ctx, int count)
    {
        PlayerController? pc = ctx.Player;
        if (pc?.Pawn?.Weapon is not GCWeapon weapon)
        {
            ctx.Reply("[bots] spawn in and hold a weapon first (in game)");
            return;
        }
        int before = AllGCMPBots().Count;
        int offset = Objects.PropertyOffset(UObject.ClassPtr<Controller>(), "ReplicatedCheatInfo");
        nint* cheatInfo = (nint*)(pc.Ptr + offset);
        nint saved = *cheatInfo;
        if (saved == 0)
            *cheatInfo = pc.Ptr;
        bool ok = true;
        try
        {
            weapon.ServerAddMPBot((byte)count, false, false, false, false, true);
        }
        catch (GameCrashException e)
        {
            mod.Log.Write($"adddummy: {e.Message}");
            ok = false;
        }
        finally
        {
            *cheatInfo = saved;
        }
        int after = AllGCMPBots().Count;
        mod.Log.Write($"adddummy: asked for {count}, GCMPBots {before} -> {after}");
        ctx.Reply(ok ? $"[bots] asked for {count} dummy bot(s); GCMPBots now {after}" : "[bots] the game's bot spawner failed - see HomefrontBots.log");
    }

    public void List(CommandContext ctx)
    {
        var line = $"[bots] {bots.Count} bot(s)";
        foreach (Bot b in bots)
            line += $" | {b.Name} team {TeamOf(b.Controller)}: {(Alive(b.Controller.Pawn) ? b.Brain.Describe() : "dead")}";
        int dummies = AllGCMPBots().Count(c => bots.All(b => b.Controller != c));
        if (dummies > 0)
            line += $" | +{dummies} dummies";
        ctx.Reply(line);
    }

    // The bot called "Bot <number>" with a body, or the first one with a body for number 0.
    Bot? FindLiving(int number) =>
        bots.FirstOrDefault(b => b.Controller.IsValid && Alive(b.Controller.Pawn) && (number == 0 || b.Name == $"Bot {number}"));

    public void GoTo(CommandContext ctx, int number)
    {
        if (ctx.Player is not PlayerController pc)
        {
            ctx.Reply("[bots] goto moves a player: use it in game");
            return;
        }
        Pawn? me = pc.Pawn;
        Bot? b = FindLiving(number);
        Pawn? target = b?.Controller.Pawn;
        if (!Alive(me) || target == null)
        {
            ctx.Reply(!Alive(me) ? "[bots] spawn in first" : "[bots] no such bot with a body (try !bots list)");
            return;
        }
        FVector there = target.Location;
        if (Teleport(me!, SpotsAround(there, false)[0]) is not FVector spot)
        {
            mod.Log.Write($"goto: engine refused the move next to {b!.Name} at {there}");
            ctx.Reply($"[bots] couldn't move you next to {b.Name}");
            return;
        }
        // The server moved the pawn; tell the client too, facing the bot.
        pc.ClientSetLocation(spot, Facing(spot, there));
        mod.Log.Write($"goto: {pc.PlayerName} -> {b!.Name}");
        ctx.Reply($"[bots] teleported to {b.Name}");
    }

    public void Bring(CommandContext ctx, int number)
    {
        if (ctx.Player is not PlayerController pc)
        {
            ctx.Reply("[bots] bring moves bots to a player: use it in game (or drag them in HomefrontEditor)");
            return;
        }
        Pawn? me = pc.Pawn;
        if (!Alive(me))
        {
            ctx.Reply("[bots] spawn in first");
            return;
        }
        FVector here = me!.Location;
        // A spot 300 units ahead of the player, bots spread around it, one spot each.
        float yaw = me.Rotation.Yaw * MathF.PI / 32768f;
        var ahead = new FVector(here.X + 300 * MathF.Cos(yaw), here.Y + 300 * MathF.Sin(yaw), here.Z);
        List<FVector> spots = SpotsAround(ahead, true);
        int moved = 0;
        foreach (Bot b in bots)
        {
            Pawn? pawn = b.Controller.IsValid ? b.Controller.Pawn : null;
            if (!Alive(pawn) || (number != 0 && b.Name != $"Bot {number}"))
                continue;
            if (moved < spots.Count && Teleport(pawn!, spots[moved]) is FVector spot)
            {
                pawn!.SetRotation(Facing(spot, here));
                b.Brain.Reset();
                ++moved;
            }
            else
            {
                mod.Log.Write($"bring: couldn't move {b.Name} near {ahead}");
            }
        }
        mod.Log.Write($"bring: {moved} bot(s) to {pc.PlayerName}");
        ctx.Reply($"[bots] brought {moved} bot(s) in front of you");
    }

    public void RemoveAll(CommandContext ctx)
    {
        // GCGameBase.KillBots: KillBot(every AIController) = Pawn.Suicide + Destroy. Controller.Destroyed logs a
        // player bot out and takes it off its team; Tick notices it's gone and fixes the bot count.
        if (Game.Info is GCGameBase g)
        {
            g.KillBots();
            ctx.Reply("[bots] removed all bots");
        }
        else
        {
            ctx.Reply("[bots] this game mode has no KillBots");
        }
    }

    /// <summary>Drops the bots of a map that is going away.</summary>
    public void Clear()
    {
        if (bots.Count > 0)
            mod.Log.Write($"bots: map changed; {bots.Count} bot(s) went with it");
        bots.Clear();
        nextNumber = 1;
    }

    public void Tick(long now)
    {
        bool mayPlan = true;  // one path search per server tick across all bots
        for (int i = 0; i < bots.Count; ++i)
        {
            Bot b = bots[i];
            GCMPBot c = b.Controller;
            if (!c.IsValid || !c.Alive)
            {
                mod.Log.Write($"bots: {b.Name} left");
                if (b.Game.IsValid && b.Game.NumBots > 0)
                    b.Game.NumBots--;
                bots.RemoveAt(i--);
                continue;
            }
            SyncTeamNum(c);  // the game may move bots between teams (balancing)
            if (c.Pawn is Pawn pawn && pawn.Alive)
            {
                b.NextSpawn = 0;
                if (b.Brain.Update(mod, b.Name, c, pawn, TeamOf(c), now, mayPlan))
                    mayPlan = false;
            }
            else if (b.NextSpawn == 0)
            {
                mod.Log.Write($"bots: {b.Name} died; respawning in {RespawnDelayMs} ms");
                b.NextSpawn = now + RespawnDelayMs;
                b.Brain.Reset();
            }
            else if (now >= b.NextSpawn && !SpawnPawn(b))
            {
                b.NextSpawn = now + RetryMs;
            }
        }
    }
}
