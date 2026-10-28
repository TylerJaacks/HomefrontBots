# HomefrontBots - player bots

A [HomefrontSDK](../HomefrontSDK/README.md) mod: bots that join as players, spawn and respawn by the game's rules,
fight with the game's own AI, and walk to objectives over a navigation graph the mod generates for each map.
This is the C# port of the earlier C++ `hfbots` mod: same behaviour, and the same nav file format, so the graphs
built by the C++ version load as they are.

## Build and install

```
HomefrontSDK\Build.ps1 -Install    build the SDK and its mods, install into Homefront Dedicated Server\Binaries (stop the server first)
python HomefrontBots\build_navs.py  generate the navigation graph of every map (see Navigation)
```

In the server: `HomefrontSDK\Mods\HomefrontBots\HomefrontBots.dll`, log `HomefrontBots.log` and graphs
`nav\<map>.nav` in the same folder. A rebuilt DLL can be swapped in while the server runs: copy it over the old
one and type `!sdk reload HomefrontBots`. The bots stay in the game and the new copy takes them over (removing
them on unload with `GCGameBase.KillBots` once crashed the server).

| File | What |
|---|---|
| `BotsScript.cs` | The script: chat commands, map changes, nav scheduling, editor-bridge handlers |
| `Bots.cs` | Bot lifecycle: join, team, spawn, respawn, teleports |
| `Brain.cs` | Goals and path following |
| `Nav.cs` | Navigation graph: generation, cache, nearest node, A* |

## Chat commands (`!bots ...`)

| Command | What |
|---|---|
| `list` (or nothing) | each bot's team and goal with progress, e.g. `take Objective_GameMode_VolumeHold_3 12/340` |
| `add [n] [team]` | add n (1-16) player bots; team 0 or 1, default balanced |
| `goto [n]` | teleport yourself next to `Bot n` (default: the first with a body) |
| `bring [n]` | teleport `Bot n` (default: all) in front of you |
| `kill` | remove every bot (`GCGameBase.KillBots`) |
| `nav` / `nav rebuild` | the graph's status / generate it again for this map |
| `dummy [n]` | the developers' dummy spawner (below) |
| `ladder [n] [down]` | test: puts the first bot at the foot (or top) of the map's ladder n and sends it up (or down); `!bots ladder` says how many |
| `die [n]` | test: kills bot n (default: the first with a body), as a suicide |
| `loadouts` | the loadouts bots draw from, and each bot's loadout and weapon in hand |

Commands are kept out of chat and answered with a chat line to the sender. They also work from a tool, without a
player: `python HomefrontSDK\Tools\hfbridge.py command "!bots add 4"`. `goto`, `bring` and `dummy` need a player in
game.

The bridge also answers `bots.list` (each bot's position, state, goal and remaining path) and `bots.nav` (the
graph's nodes, packed as base64 floats; links too with `"links": true`).

## Player bots

Each bot is a `GCMPBot` controller that joins like the game's single-player `FL2GameSP.AddBot`, then spawns the way
the native human spawn does, step by step:

1. **Join**: `Spawn(GCMPBot)`, `bIsPlayer`, `InitPlayerReplicationInfo`, `SetPlayerName("Bot N")`, `bBot`,
   `PlayerID = CurrentID++`, `ChangeTeam(bot, 2 = balance | 0 | 1, true)`, `NumBots++`. This gives a scoreboard
   entry, a name and a team. `MaxTargetDistSoldier = 4000`: `GCMPBot`'s default is 0, which makes it ignore everyone;
   the developers' spawner sets 4000.
2. **Team number**: `GCWeaponAIController.TeamNum = PRI team`, kept in sync every tick. Its native `GetTeamNum`
   returns this field, not the PRI team, and `Pawn.GetTeamNum` asks the controller. Friendly fire
   (`FL2GameMP.ReduceDamage` -> `0x00DB89F0`) and target choice go through it.
3. **Spawn**: not `FL2GameMP.SpawnPlayer`, because the native `PlayerSpawnManagerMP::SpawnPlayer` `0x00C05410`
   casts the controller to `GCPlayerController` and fails for AI. Instead:
   - only while the match runs (`!bGameEnded && !bWaitingToStartMatch`);
   - spawn point: the manager's chooser, vtable slot `0x408` (`0x00BFE8A0`), called with **no controller**. Given
     one, it reads `FL2PlayerControllerMP` fields at fixed offsets;
   - soldier class: `Team0_PlayerClasses` (KPA) if team == `FL2GameMP.m_LowTeam`, else `Team1_PlayerClasses` (US),
     index 0 = assault (`0x00BFCCB0`). `GetSpawnClass` returns a class the engine won't spawn;
   - `Spawn` at the point, or the nearest free spot around it (a standing bot blocks its point);
   - `Possess`, then `SetMovementPhysics` (without it the pawn hangs above the ground), then `GotoState('Searching')`.
     A bot that joined without a body sits in `Dead`, and possessing doesn't leave that state. Then put the bot's
     loadout (below) into `PRI.CurrentPackedLoadout`, and call the manager's `SetPlayerDefaults` and
     `AddDefaultInventory` (weapons from the loadout).
4. **Respawn**: the tick sees a bot without a pawn and respawns it 5 s later, retrying every second.
5. **Teleports** use `Actor.Teleport` (the SDK's wrapper of `UWorld::FarMoveActor`, `0x0070D920`, with `bNoCheck`).
   `Actor.SetLocation` refuses every spot for soldier pawns here, even open ground next to a player.

**Loadouts**: each bot draws one at random, when it joins, from the armoury's six default loadouts and the loadout
of whoever added it (a tool adding bots: the first player's), and keeps it. `!bots loadouts` lists them and what each
bot holds. A loadout as the server takes it (`PackedLoadout.Items`, 12 bytes) is a list of positions in the
multiplayer item list `GCAvailableInventoryList_MP_Content.AvailableObjects` (weapon configs such as `CFG_RIF_M4`,
scopes, grenades, drones, airstrikes, equipment; 0 = none). The game packs it on the client
(`GCPlayerReplicationInfo.GetPackedLoadout` -> `0x00BFFF40`, which reads the local player's armoury, so it returns
nothing on a dedicated server); the mod packs `GCCustomLoadoutProfiles.DefaultLoadouts` the same way: per inventory
group, the primary item then the secondary items, each looked up by name.

| # | Primary | Scope | Other |
|---|---|---|---|
| 0 | M4 | holographic | M9, frag, EMP, flak jacket, Panzerfaust 3 |
| 1 | Diablo (SMG) | reflex | M9, frag, flashbang, flak jacket, ground assault drone |
| 2 | M249 (LMG) | - | M9, frag, EMP, Hellfire strike, Panzerfaust 3 |
| 3 | M110 (sniper) | - | M9, frag, EMP, ground assault drone, Hellfire strike |
| 4 | ACR | ACOG | M9, frag, flashbang, flak jacket, UAV |
| 5 | SCAR-MG (LMG) | holographic | M9, frag, C4, air assault drone, proximity anti-tank |

**Across map changes**: the roster (every bot `!bots add` added, with its team and loadout, until `!bots kill`)
comes back on each playable map; the intermission lobby between maps is skipped. The bots of the old map go with
it, and new ones join (and spawn once the match runs) when the next map loads.

## Fighting

Aiming and firing are the game's own `GCWeaponAIController`. In `Searching`, it sweeps its view and goes to
`Attacking` when `SeePlayer` reports an enemy in range; in `Attacking`, it aims, locks on and fires. Meanwhile the
brain keeps the bot moving: every 1.2-2.8 s (or on arrival) it picks a spot 120-400 units away that it can walk to
in a straight line, preferably sideways to its `Enemy`, and strafes there at 60% speed without turning the bot (the
AI keeps aiming). When the fight ends it plans a new route from where it is.

## Navigation

Multiplayer maps ship no navigation data: no `PathNode`s or `ReachSpec`s, and Kynapse pathdata exists only for the
single-player `*_Kyn` maps. So `Nav.cs` generates a graph from the level's collision.

- **When**: once the map is loaded (the SDK's `OnMapLoaded`: GameInfo found, streaming levels in place) and its
  streaming levels have then stayed in place for 5 s.
- **Area**: the bounds of the map's ground combat volumes (`GameMode_CombatVolume` with `bGroundVolumeType`,
  `BrushComponent.Bounds`). Without them, the spawn points plus 4000 units.
- **Flood fill**: starts at every spawn point (`PlayerStart`) and objective in the area, and on top of every ladder
  (past the `LadderVolume` on the side the climber faces: platforms reached only by climbing), over a 48-unit grid (about
  1 m; a soldier is a 15 x 42 cylinder, steps 26 units, walks on floors with normal Z >= 0.70). For each grid
  neighbour, a box probe finds the floor; the rise must be a step, a walkable slope or a drop of at most 250 units
  (one-way); then a soldier-sized box is swept across, lifted by a step. Traces are `World.Trace` with `TraceWorld`.
- **Cost**: fl-farm has 292k nodes and 2.2M links from 2.5M traces in about 12 s of trace time, spread over about a
  minute at 8 ms per tick, so the server keeps running. The cache loads in about a second. Delete a cache (or
  `!bots nav rebuild`) to regenerate.
- `HomefrontBots\build_navs.py [--rebuild] [--maps ...]` starts the server and sends it to every map (Ground Control
  where the map has it, otherwise TDM) with the SDK's `!sdk travel` through its bridge, waiting for each graph.
  `--rebuild` deletes only the caches of the maps in the run. It starts the server with `-MULTIHOME=<this PC's
  address>` (see the SDK README).
- **Ladders**: every `LadderVolume` of the map becomes a two-way climb link between the node on the floor under it
  (in front of the ladder) and a node level with its top (beyond it, on the side the climber faces, `LookDir`).
  They are found each time the graph loads, not cached. A* counts a climb as 1.5 x its height + 150.

The C# port was checked against the C++ build: rebuilding FL-BigBox gave the same 18,731 nodes (heights within
0.0001 units) and the same 131,727 links; only the node order differs, because the engine's object table (the order
seeds are found in) differs between server runs.

Graphs built 2026-10-27 (by the C++ version; same format), all 13 maps, about 22 minutes in total. Every one is a
single connected network apart from a few stray nodes:

| Map | Mode | Nodes | Links | Trace time | Islands |
|---|---|---|---|---|---|
| fl-angelisland | FL | 302,694 | 2,167,371 | 17.9 s | 1 |
| fl-farm | FL | 292,228 | 2,188,816 | 11.5 s | 1 |
| fl-lowlands | FL | 274,877 | 2,101,971 | 11.2 s | 1 |
| FL-Crossroads | FL | 245,447 | 1,745,911 | 14.4 s | 1 |
| fl-harbor | FL | 122,725 | 912,078 | 5.7 s | 1 |
| FL-Spillway | FL | 117,381 | 866,432 | 5.5 s | 2 (one stray node) |
| fl-bridge | FL | 71,751 | 508,726 | 4.4 s | 2 (109 nodes apart) |
| FL-Overpass | TDM | 59,361 | 425,925 | 4.4 s | 1 |
| fl-borderlands | TDM | 49,240 | 363,492 | 2.0 s | 1 |
| FL-TDMSpillway | TDM | 40,266 | 296,639 | 1.7 s | 1 |
| fl-culdesac | FL | 31,301 | 222,670 | 1.4 s | 1 |
| fl-alcatraz | TDM | 20,559 | 144,453 | 1.7 s | 1 |
| FL-BigBox | TDM | 18,731 | 131,727 | 0.9 s | 1 |

The cache is per map, not per mode: a Ground Control graph also serves TDM on that map.

File format (`nav\<map>.nav`, little-endian): header `"HFNV"`, version 3, spacing, soldier radius, half height,
step, walkable Z (floats), node count, link count; then each node's floor point (3 floats); then per node a link
count (1 byte) and that many node indices (uint32).

## Movement

- **Goal**: Ground Control: an active `Objective_GameMode` that the bot's team doesn't own (nearer is better, with
  randomness); else one it owns, to hold. At an objective, it keeps picking spots within 450 units. Team Deathmatch
  (`FL2GameTDM`), or no objective to take: hunt an enemy, a near one with randomness so the bots spread out, with a new
  route every 4 s if it moved 800 units or died. Else roam.
- **Sight**: every 0.4-0.6 s each bot checks its nearest 3 enemies within 4000 units for a clear line eye to eye
  (`World.Trace`, world geometry). One in sight is faced (`SetFocalPoint`), so the game's AI notices it (`SeePlayer`)
  and fights; otherwise the bot looks ahead along its route at eye level.
- **Path**: A* over the graph, with one path search per server tick across all bots, then pulled straight: from each
  kept node, the farthest node up to 48 ahead that is reachable in a straight line becomes the next waypoint. A
  straight line counts only if every grid cell on it holds a node linked from the one before; every link was tested
  with a body sweep, so shortcuts never cut through walls, climb ledges or skip ladders. No engine traces needed.
- **Steering**: sets `Pawn.Acceleration` and the walking `Velocity` toward the next waypoint, and `SetFocalPoint`
  ahead so it looks where it goes.
- **Ladders**: on a climb link the bot walks into the volume facing the wall, gets on (`Pawn.ClimbLadder` if the
  engine hasn't put it on already) and pushes along `ClimbDir` in `PHYS_Ladder`; off at the other end, it walks on. A
  climb not done within 12 s is given up.
- **Stuck** (moved less than 40 units in 1.5 s): it jumps; stuck again on a straightened route, it re-plans along the
  raw grid route (a straight line can clip a corner the grid goes around); stuck again, a new goal.

Known gaps: no cover (bots strafe in the open), and ladder climbing is still untested in game.

## Dummy bots

`dummy` calls the developers' own spawner, which survived in the server: `GCWeapon.ServerAddMPBot` (native; C++ body
`AGCWeapon::ServerAddMPBot`, vtable `0x644`, `0x00D7B630`), on the requesting player's current weapon. For each bot
it spawns a copy of the requester's pawn class next to them, copies their loadout, and drives it with a `GCMPBot`
controller named `Bot_%i_%i`. It requires the requester's `Controller.ReplicatedCheatInfo` to be set, which only the
native cheat setup does; the function only null-checks it, so the mod points it at the controller for the duration
of the call and then restores it. Dummies aren't players: no scoreboard entry, team or respawn, and they don't move.

## Gotchas

- After a map change, the previous map's objects linger until garbage collection: compare actors' `WorldInfo` with
  the current map's (the SDK's `Game.WorldInfo`).
- A `GameCrashException` from a call means game code crashed inside it (caught by the host); the engine may be left
  inconsistent.
