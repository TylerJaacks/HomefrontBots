using System;
using System.Collections.Generic;
using System.Linq;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>One player bot: a GCMPBot controller that joined like a player.</summary>
internal sealed class Bot
{
    public required GCMPBot Controller;
    public required GameInfo Game;
    public required string Name;
    public FPackedLoadout Loadout;
    public byte LoadoutIndex;
    public long NextSpawn;   // 0 while it has a pawn
    public int Failures;     // spawn attempts refused since it last had a pawn
    public int DeadResets;   // times this life the game put the controller in state Dead under a living pawn
    public readonly Brain Brain = new();
}

/// <summary>A bot on the roster: its team (-1: balanced) and loadout.</summary>
internal readonly record struct RosterBot(int Team, FPackedLoadout Loadout, byte LoadoutIndex);

/// <summary>Bot lifecycle: join, team, spawn, respawn, teleports. The roster (what "!bots add" asked for, until
/// "!bots kill") outlives the map: the bots come back on the next playable one.</summary>
internal sealed class Bots(BotsScript mod)
{
    private const long RespawnDelayMs = 5000, RetryMs = 1000;

    // How far a bot engages soldiers. GCMPBot's default is 0 (sees everyone, engages no one); the developers'
    // spawner sets 4000 (AGCWeapon::ServerAddMPBot writes it at +0x548).
    private const float TargetDistSoldier = 4000f;

    // PlayerSpawnManagerMP's spawn-point chooser, vtable byte offset (0x00BFE8A0 for the FL manager).
    private const int ChooseSpawnPointSlot = 0x408;

    private readonly BotsScript _mod = mod;
    private readonly List<Bot> _bots = [];
    private readonly List<RosterBot> _roster = [];
    private int _nextNumber = 1;

    public IReadOnlyList<Bot> All => _bots;

    public IReadOnlyList<RosterBot> Roster => _roster;

    private static int TeamOf(Controller? c) => c?.PlayerReplicationInfo?.Team is { } t ? (int)t.TeamIndex : -1;

    private static bool Alive(Actor? a) => a != null && a.Alive;

    // GCWeaponAIController keeps its own team number (TeamNum), which its native GetTeamNum returns instead of the
    // PRI's team, and Pawn.GetTeamNum asks the controller. Friendly-fire checks (FL2GameMP.ReduceDamage) and the
    // bot's choice of targets go through it, so it has to follow the PRI team. The developers' spawner set it
    // natively (AGCWeapon::ServerAddMPBot writes it at +0x57C).
    private static void SyncTeamNum(GCWeaponAIController c)
    {
        var team = TeamOf(c);

        if (team >= 0)
            c.TeamNum = (ETeam)team;
    }

    // Join a new bot the way the game's own single-player AddBot does (FL2GameSP.AddBot): spawn the controller,
    // give it a PRI and a name, put it on a team, hand out a player ID. GCMPBot defaults to bIsPlayer=false, so
    // Controller.PostBeginPlay skipped InitPlayerReplicationInfo and it's done here instead.
    private GCMPBot? Join(GameInfo game, int team)
    {
        var cls = UObject.Get<UClass>(UObject.ClassPtr<GCMPBot>());

        if (game.Spawn(cls, bNoCollisionFail: true) is not GCMPBot c)
        {
            _mod.Log.Write("bots: Spawn(GCMPBot) failed");

            return null;
        }

        c.bIsPlayer = true;
        c.MaxTargetDistSoldier = TargetDistSoldier;
        c.InitPlayerReplicationInfo();

        var pri = c.PlayerReplicationInfo;

        if (pri == null)
        {
            _mod.Log.Write($"bots: {c.ObjectName} got no PlayerReplicationInfo; destroying it");
            c.Destroy();
            return null;
        }

        pri.SetPlayerName($"Bot {_nextNumber++}");
        pri.bBot = true;
        pri.PlayerID = game.CurrentID++;

        // 0/1 as asked; 2 has FL2GameMP.PickTeam balance the teams (255 would mean "no team" there).
        if (!game.ChangeTeam(c, (ETeam)(team < 0 ? 2 : team), true))
            _mod.Log.Write($"bots: ChangeTeam refused {c.PlayerName}");
        
        SyncTeamNum(c);
        
        game.NumBots++;
        
        return c;
    }

    // Places to put a pawn near `center`, nearest first: rings of 8 around it, about two pawn widths apart. With
    // `includeCenter`, the center comes first.
    private static List<FVector> SpotsAround(FVector center, bool includeCenter)
    {
        var spots = new List<FVector>();

        if (includeCenter)
            spots.Add(center);

        foreach (var radius in new[] { 90f, 180f, 270f })
            for (var i = 0; i < 8; ++i)
                spots.Add(new FVector(center.X + radius * MathF.Cos(i * MathF.PI / 4), center.Y + radius * MathF.Sin(i * MathF.PI / 4), center.Z));

        return spots;
    }

    private static FRotator Facing(FVector from, FVector to) =>
        new(0, (int)(MathF.Atan2(to.Y - from.Y, to.X - from.X) * 32768f / MathF.PI), 0);

    // Teleport a pawn to `spot` (a standing pawn's height near another pawn), raised a little so it doesn't clip the
    // ground, from where it falls under normal movement physics. Returns where the pawn went, or null.
    private static FVector? Teleport(Pawn pawn, FVector spot)
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
    private static unsafe NavigationPoint? ChooseSpawnPoint(PlayerSpawnManager manager, int team, byte spawnType)
    {
        var fn = (*(nint**)manager.Ptr)[ChooseSpawnPointSlot / 4];

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
    private bool SpawnPawn(Bot b)
    {
        var c = b.Controller;
        var game = b.Game;

        if (!c.IsValid || !game.IsValid)
            return false;

        var why = "";
        var fl = game as FL2Game;
        var manager = fl?.SpawnManager as PlayerSpawnManagerMP;
        var team = TeamOf(c);

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
            ref var classes = ref team == (int)mp.m_LowTeam ? ref manager.Team0_PlayerClasses : ref manager.Team1_PlayerClasses;
            
            var pawnClass = classes.Count > 0 ? classes[0] : null;

            // The native only checks the chosen point is free (0x00BF9EE0) and falls back to another one; a bot
            // standing on its spawn point keeps winning the scoring, so spawn beside the point instead.
            if (pawnClass != null)
            {
                foreach (var spot in SpotsAround(point.Location, true).TakeWhile(spot => (pawn = game.Spawn(pawnClass, SpawnLocation: spot, 
                             SpawnRotation: new FRotator(0, point.Rotation.Yaw, 0)) as Pawn) == null))
                {
                }
            }

            if (pawnClass == null)
                why = "no soldier class for team";
            else if (pawn == null)
                why = $"no room to spawn {pawnClass.ObjectName} at or around {point.ObjectName}";
        }

        if (why.Length > 0)
        {
            if (b.Failures++ % 30 == 0)
                _mod.Log.Write($"bots: {b.Name} can't spawn yet ({why}; {b.Failures} tries); retrying every {RetryMs} ms");
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
        b.DeadResets = 0;
        
        _mod.Log.Write($"bots: {b.Name} spawned as {pawn.ObjectName} at {point!.ObjectName} (team {team}, weapon " +
                      $"{WeaponName(pawn)}, possessed {(c.Pawn == pawn ? "yes" : "NO")})");

        return true;
    }

    public void Add(CommandContext ctx, int count, int team)
    {
        var game = Game.Info;
        
        if (game == null)
        {
            ctx.Reply("[bots] no game running");
            return;
        }

        // The requester's loadout (a tool has none: the first player's): what they last asked to spawn with, else what
        // they carry now. Each bot draws from it and the armoury's default loadouts.
        var requester = ctx.Player ?? Game.Players.Find(p => p.PlayerReplicationInfo != null);

        var pri = requester?.PlayerReplicationInfo as GCPlayerReplicationInfo;
        var loadout = requester is FL2PlayerControllerMP mp ? mp.RequestedPackedLoadout
            : pri != null ? pri.CurrentPackedLoadout : default;

        var wanted = Enumerable.Repeat(new RosterBot(team, loadout, ChooseLoadout), count).ToList();
        var (added, spawned, joined) = Join(game, wanted);

        _roster.AddRange(joined);

        var reply = $"[bots] added {added} bot(s), {spawned} spawned";
        
        if (spawned < added)
            reply += "; the rest retry every second";
        
        if (added < count)
            reply += "; adding failed - see HomefrontBots.log";
        
        ctx.Reply(reply);
    }

    // Joins and spawns one bot per roster entry; stops at the first that can't join. Returns the entries as joined
    // (with the loadout chosen for those that asked for one).
    private (int added, int spawned, List<RosterBot> joined) Join(GameInfo game, IEnumerable<RosterBot> wanted)
    {
        int added = 0, spawned = 0;
        var joined = new List<RosterBot>();

        foreach (var w in wanted)
        {
            var c = Join(game, w.Team);

            if (c == null)
                break;

            var (loadout, loadoutIndex) = w.LoadoutIndex == ChooseLoadout ? PickLoadout(c, w.Loadout) : (w.Loadout, w.LoadoutIndex);
            var b = new Bot { Controller = c, Game = game, Name = c.PlayerName, Loadout = loadout, LoadoutIndex = loadoutIndex };

            _mod.Log.Write($"bots: {b.Name} joined team {TeamOf(c)}, loadout {Describe(loadout, loadoutIndex)}");

            if (SpawnPawn(b))
                ++spawned;
            else
                b.NextSpawn = Environment.TickCount64 + RetryMs;

            _bots.Add(b);
            joined.Add(w with { Loadout = loadout, LoadoutIndex = loadoutIndex });

            ++added;
        }

        return (added, spawned, joined);
    }

    /// <summary>Brings back the roster on a new map (the previous map's bots went with it).</summary>
    public void Restore(GameInfo game)
    {
        var (added, spawned, _) = Join(game, _roster);

        _mod.Log.Write($"bots: brought back {added} of {_roster.Count} bot(s) from the last map, {spawned} spawned");
    }

    /// <summary>Takes over the bots already in this game: a reloaded mod finds the ones its previous copy left in.
    /// Removing them on unload (GCGameBase.KillBots) crashed the server once, in the game's event dispatch.</summary>
    public void Adopt(GameInfo game)
    {
        var now = Environment.TickCount64;

        foreach (var c in Objects.All<GCMPBot>())
        {
            if (!c.Alive || c.WorldInfo != game.WorldInfo || c.PlayerReplicationInfo is not GCPlayerReplicationInfo { bBot: true } pri)
                continue;

            var b = new Bot { Controller = c, Game = game, Name = c.PlayerName, Loadout = pri.CurrentPackedLoadout, LoadoutIndex = pri.LoadoutIndex };

            if (!Alive(c.Pawn))
                b.NextSpawn = now;

            _bots.Add(b);
            _roster.Add(new RosterBot(TeamOf(c), b.Loadout, b.LoadoutIndex));

            if (b.Name.StartsWith("Bot ") && int.TryParse(b.Name.AsSpan(4), out var n) && n >= _nextNumber)
                _nextNumber = n + 1;
        }

        if (_bots.Count > 0)
            _mod.Log.Write($"bots: took over {_bots.Count} bot(s) already in the game");
    }

    // ---- loadouts

    // A roster entry's LoadoutIndex asking for a loadout to be drawn (its Loadout is the requester's, one of the draws).
    private const byte ChooseLoadout = 255;

    // The armoury's default loadouts (GCCustomLoadoutProfiles.DefaultLoadouts), packed with their loadout index.
    // Learned once; all-zero entries (no items: no weapons) are left out.
    private readonly List<(FPackedLoadout Loadout, byte Index)> _loadouts = [];

    private const int PackedItems = 12;

    // Packs the armoury's default loadouts the way the game does (the client's GetPackedLoadout, 0x00BFFF40): for each
    // inventory group of a profile, its primary item and then its secondary items, each as its position in the
    // multiplayer item list (GCAvailableInventoryList_MP_Content.AvailableObjects; 0 = none), 12 at most. The native
    // can't run here: it reads the local player's armoury, and a dedicated server has no local player.
    private unsafe void LearnLoadouts()
    {
        var list = Objects.All<GCAvailableInventoryList>().Find(l => l.AvailableObjects.Count > 1);
        var profiles = UObject.Get<GCCustomLoadoutProfiles>(Objects.Find("GCGame.Default__GCCustomLoadoutProfiles"));

        if (list == null || profiles == null)
        {
            _mod.Log.Write("bots: no item list or armoury on this server; bots copy the loadout of whoever adds them");
            return;
        }

        var items = list.AvailableObjects;  // a copy of the array header: same items
        ref var defaults = ref profiles.DefaultLoadouts;
        var missing = new List<string>();

        int IndexOf(FName name)
        {
            var text = name.ToString();

            for (var i = 1; i < items.Count; ++i)
                if (Names.ObjectName(items.Data[i]).Equals(text, StringComparison.OrdinalIgnoreCase))
                    return i;

            missing.Add(text);
            return 0;
        }

        for (var p = 0; p < defaults.Count; ++p)
        {
            ref var profile = ref defaults[p];
            var packed = new FPackedLoadout();
            var bytes = packed.Items;
            var n = 0;

            for (var g = 0; g < profile.InventoryGroups.Count && n < PackedItems; ++g)
            {
                ref var group = ref profile.InventoryGroups[g];

                if (!group.PrimaryItem.IsNone && IndexOf(group.PrimaryItem) is var primary and > 0)
                    bytes[n++] = (byte)primary;

                for (var s = 0; s < group.SecondaryItems.Count && n < PackedItems; ++s)
                    if (IndexOf(group.SecondaryItems[s]) is var secondary and > 0)
                        bytes[n++] = (byte)secondary;
            }

            if (HasItems(packed))
                _loadouts.Add((packed, (byte)p));
        }

        _mod.Log.Write($"bots: {_loadouts.Count} default loadout(s) from the armoury: " +
                       string.Join(" | ", _loadouts.Select(l => Describe(l.Loadout, l.Index, items))) +
                       (missing.Count > 0 ? $"; not in the item list: {string.Join(", ", missing.Distinct())}" : ""));
    }

    private static bool HasItems(FPackedLoadout loadout)
    {
        foreach (var item in loadout.Items)
            if (item != 0)
                return true;

        return false;
    }

    private static string Describe(FPackedLoadout loadout, byte index) => Describe(loadout, index, ItemList());

    // A loadout as its item names (CFG_RIF_ACR CFG_Scope_ACOG_var1 ...), or its bytes without the item list.
    private static unsafe string Describe(FPackedLoadout loadout, byte index, TObjectArray<UObject> items)
    {
        if (!HasItems(loadout))
            return "none (no weapons)";

        var names = new List<string>();

        foreach (var item in loadout.Items)
            if (item != 0)
                names.Add(item < items.Count ? Names.ObjectName(items.Data[item]) : item.ToString());

        return $"#{index} {string.Join(" ", names)}";
    }

    // The weapon a pawn holds, by its config (CFG_RIF_M4: GCWeapon.InfantryWeaponInfo), else its object name.
    private static string WeaponName(Pawn? pawn) =>
        pawn?.Weapon is not { } w ? "none" : (w as GCWeapon)?.InfantryWeaponInfo?.ObjectName ?? w.ObjectName;

    private static TObjectArray<UObject> ItemList() =>
        Objects.All<GCAvailableInventoryList>().Find(l => l.AvailableObjects.Count > 1)?.AvailableObjects ?? default;

    // A loadout for a new bot: one of the armoury's defaults or the requester's, at random.
    private (FPackedLoadout, byte) PickLoadout(Controller c, FPackedLoadout requested)
    {
        if (_loadouts.Count == 0)
            LearnLoadouts();

        var choices = HasItems(requested) ? _loadouts.Count + 1 : _loadouts.Count;

        if (choices == 0)
            return (requested, 0);

        var pick = _mod.Random.Next(choices);

        return pick < _loadouts.Count ? _loadouts[pick] : (requested, (byte)0);
    }

    /// <summary>The loadouts bots draw from, and which each bot has.</summary>
    public void ListLoadouts(CommandContext ctx)
    {
        ctx.Reply($"[bots] {_loadouts.Count} armoury loadout(s): {string.Join(" | ", _loadouts.Select(l => Describe(l.Loadout, l.Index)))}");

        foreach (var b in _bots)
            ctx.Reply($"[bots] {b.Name}: {Describe(b.Loadout, b.LoadoutIndex)}, holding {WeaponName(b.Controller.Pawn)}");
    }

    // Every GCMPBot controller, ours and the dummy spawner's, skipping the class default object.
    // ReSharper disable once InconsistentNaming
    private static List<GCMPBot> AllGCMPBots() =>
    [
        .. Objects.All<GCMPBot>().Where(c => !c.ObjectName.StartsWith("Default__", StringComparison.Ordinal) && c.Alive)
    ];

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
        var pc = ctx.Player;
        
        if (pc?.Pawn?.Weapon is not GCWeapon weapon)
        {
            ctx.Reply("[bots] spawn in and hold a weapon first (in game)");
            return;
        }

        var before = AllGCMPBots().Count;
        var offset = Objects.PropertyOffset(UObject.ClassPtr<Controller>(), "ReplicatedCheatInfo");
        var cheatInfo = (nint*)(pc.Ptr + offset);
        var saved = *cheatInfo;
        
        if (saved == 0)
            *cheatInfo = pc.Ptr;

        var ok = true;

        try
        {
            weapon.ServerAddMPBot((byte)count, false, false, false, false, true);
        }
        catch (GameCrashException e)
        {
            _mod.Log.Write($"adddummy: {e.Message}");

            ok = false;
        }
        finally
        {
            *cheatInfo = saved;
        }

        var after = AllGCMPBots().Count;

        _mod.Log.Write($"adddummy: asked for {count}, GCMPBots {before} -> {after}");

        ctx.Reply(ok ? $"[bots] asked for {count} dummy bot(s); GCMPBots now {after}" : "[bots] the game's bot spawner failed - see HomefrontBots.log");
    }

    public void List(CommandContext ctx)
    {
        var line = $"[bots] {_bots.Count} bot(s)";

        line = _bots.Aggregate(line, (current, b) => current + $" | {b.Name} team {TeamOf(b.Controller)}: {(Alive(b.Controller.Pawn) ? b.Brain.Describe() : "dead")}");

        var dummies = AllGCMPBots().Count(c => _bots.All(b => b.Controller != c));

        if (dummies > 0)
            line += $" | +{dummies} dummies";

        ctx.Reply(line);
    }

    // The bot called "Bot <number>" with a body, or the first one with a body for number 0.
    private Bot? FindLiving(int number) =>
        _bots.FirstOrDefault(b => b.Controller.IsValid && Alive(b.Controller.Pawn) && (number == 0 || b.Name == $"Bot {number}"));

    public void GoTo(CommandContext ctx, int number)
    {
        if (ctx.Player is not { } pc)
        {
            ctx.Reply("[bots] goto moves a player: use it in game");

            return;
        }

        var me = pc.Pawn;
        var b = FindLiving(number);
        var target = b?.Controller.Pawn;

        if (!Alive(me) || target == null)
        {
            ctx.Reply(!Alive(me) ? "[bots] spawn in first" : "[bots] no such bot with a body (try !bots list)");
            return;
        }

        var there = target.Location;
        
        if (Teleport(me!, SpotsAround(there, false)[0]) is not { } spot)
        {
            _mod.Log.Write($"goto: engine refused the move next to {b!.Name} at {there}");
            ctx.Reply($"[bots] couldn't move you next to {b.Name}");
            return;
        }
        
        // The server moved the pawn; tell the client too, facing the bot.
        pc.ClientSetLocation(spot, Facing(spot, there));
        
        _mod.Log.Write($"goto: {pc.PlayerName} -> {b!.Name}");
        
        ctx.Reply($"[bots] teleported to {b.Name}");
    }

    public void Bring(CommandContext ctx, int number)
    {
        if (ctx.Player is not { } pc)
        {
            ctx.Reply("[bots] bring moves bots to a player: use it in game (or drag them in HomefrontEditor)");
            return;
        }

        var me = pc.Pawn;

        if (!Alive(me))
        {
            ctx.Reply("[bots] spawn in first");
            return;
        }

        var here = me!.Location;

        // A spot 300 units ahead of the player, bots spread around it, one spot each.
        var yaw = me.Rotation.Yaw * MathF.PI / 32768f;
        var ahead = new FVector(here.X + 300 * MathF.Cos(yaw), here.Y + 300 * MathF.Sin(yaw), here.Z);
        var spots = SpotsAround(ahead, true);
        var moved = 0;
        
        foreach (var b in _bots)
        {
            var pawn = b.Controller.IsValid ? b.Controller.Pawn : null;

            if (!Alive(pawn) || (number != 0 && b.Name != $"Bot {number}"))
                continue;

            if (moved < spots.Count && Teleport(pawn!, spots[moved]) is { } spot)
            {
                pawn!.SetRotation(Facing(spot, here));

                b.Brain.Reset();

                ++moved;
            }
            else
            {
                _mod.Log.Write($"bring: couldn't move {b.Name} near {ahead}");
            }
        }

        _mod.Log.Write($"bring: {moved} bot(s) to {pc.PlayerName}");

        ctx.Reply($"[bots] brought {moved} bot(s) in front of you");
    }

    /// <summary>A ladder test: puts the first bot with a body at the foot of ladder <paramref name="index"/> and sends
    /// it to the top (or the other way round with <paramref name="down"/>).</summary>
    public void LadderTest(CommandContext ctx, int index, bool down)
    {
        var nav = _mod.Nav;

        if (!nav.Ready || index < 0 || index >= nav.Ladders.Count)
        {
            ctx.Reply($"[bots] {nav.Ladders.Count} ladder(s) on this map{(nav.Ready ? "" : " (nav not ready)")}; try !bots ladder 0..{nav.Ladders.Count - 1}");
            return;
        }

        var b = FindLiving(0);

        if (b?.Controller.Pawn is not { } pawn)
        {
            ctx.Reply("[bots] add a bot first");
            return;
        }

        var ladder = nav.Ladders[index];
        var (from, to) = down ? (ladder.Top, ladder.Bottom) : (ladder.Bottom, ladder.Top);

        if (Teleport(pawn, nav.Stand(from)) == null)
        {
            ctx.Reply("[bots] couldn't move the bot to the ladder");
            return;
        }

        b.Brain.Follow([from, to], $"climb {(down ? "down" : "up")} {ladder.Volume.ObjectName}");

        _mod.Log.Write($"ladder test: {b.Name} {(down ? "down" : "up")} {ladder.Volume.ObjectName} from {nav.Stand(from)} to {nav.Stand(to)}");

        ctx.Reply($"[bots] {b.Name} climbing {(down ? "down" : "up")} {ladder.Volume.ObjectName}: from {nav.Stand(from)} to {nav.Stand(to)}");
    }

    /// <summary>A test: kills bot <paramref name="number"/> (or the first with a body), the way a suicide does.</summary>
    public void Die(CommandContext ctx, int number)
    {
        if (FindLiving(number) is not { } b || b.Controller.Pawn is not { } pawn)
        {
            ctx.Reply("[bots] no such bot with a body");
            return;
        }

        _mod.Log.Write($"die: {b.Name} ({pawn.ObjectName})");

        pawn.Suicide();

        ctx.Reply($"[bots] {b.Name} died");
    }

    public void RemoveAll(CommandContext ctx)
    {
        // GCGameBase.KillBots: KillBot(every AIController) = Pawn.Suicide + Destroy. Controller.Destroyed logs a
        // player bot out and takes it off its team; Tick notices it's gone and fixes the bot count.
        _roster.Clear();

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

    /// <summary>Drops the bots of a map that is going away (the roster stays).</summary>
    public void Clear()
    {
        if (_bots.Count > 0)
            _mod.Log.Write($"bots: map changed; {_bots.Count} bot(s) went with it ({_roster.Count} on the roster)");

        _bots.Clear();

        _nextNumber = 1;
    }

    public void Tick(long now)
    {
        var mayPlan = true;  // one path search per server tick across all bots

        for (var i = 0; i < _bots.Count; ++i)
        {
            var b = _bots[i];
            var c = b.Controller;

            if (!c.IsValid || !c.Alive)
            {
                _mod.Log.Write($"bots: {b.Name} left");

                if (b.Game is { IsValid: true, NumBots: > 0 })
                    b.Game.NumBots--;

                _bots.RemoveAt(i--);
                continue;
            }

            SyncTeamNum(c);  // the game may move bots between teams (balancing)

            if (c.Pawn is { Alive: true } pawn)
            {
                b.NextSpawn = 0;

                // Safety net: in state Dead the AI neither looks around nor fights, and ignores its pawn's death
                // (Dead.PawnDied does nothing), so the corpse would keep the bot's PRI. SpawnPawn already leaves Dead.
                if (c.StateName == "Dead")
                {
                    if (b.DeadResets++ == 0)
                        _mod.Log.Write($"bots: {b.Name} was put in state Dead with a living body; back to Searching");
                    c.GotoState(FName.Find("Searching"));
                }

                if (b.Brain.Update(_mod, b.Name, c, pawn, TeamOf(c), now, mayPlan))
                    mayPlan = false;
            }
            else if (b.NextSpawn == 0)
            {
                _mod.Log.Write($"bots: {b.Name} died; respawning in {RespawnDelayMs} ms");

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
