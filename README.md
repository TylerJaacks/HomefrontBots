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
one and type `!sdk reload HomefrontBots` (the bots of the old copy are removed).

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

Commands are kept out of chat and answered with a chat line to the sender. They also work from a tool, without a
player: `python HomefrontSDK\Tools\hfbridge.py command "!bots add 4"` (bots added that way copy the first player's loadout;
with nobody on the server they spawn without weapons). `goto`, `bring` and `dummy` need a player in game.

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
     A bot that joined without a body sits in `Dead`, and possessing doesn't leave that state. Then copy the
     requester's loadout into `PRI.CurrentPackedLoadout`, and call the manager's `SetPlayerDefaults` and
     `AddDefaultInventory` (weapons from the loadout).
4. **Respawn**: the tick sees a bot without a pawn and respawns it 5 s later, retrying every second.
5. **Teleports** use `Actor.Teleport` (the SDK's wrapper of `UWorld::FarMoveActor`, `0x0070D920`, with `bNoCheck`).
   `Actor.SetLocation` refuses every spot for soldier pawns here, even open ground next to a player.

Bots are dropped on map change; add them again.

## Fighting

This is the game's own `GCWeaponAIController`. In `Searching`, it sweeps its view and goes to `Attacking` when
`SeePlayer` reports an enemy in range; in `Attacking`, it aims, locks on and fires. While a bot is in `Attacking`,
the brain stops moving it.

## Navigation

Multiplayer maps ship no navigation data: no `PathNode`s or `ReachSpec`s, and Kynapse pathdata exists only for the
single-player `*_Kyn` maps. So `Nav.cs` generates a graph from the level's collision.

- **When**: once the map is loaded (the SDK's `OnMapLoaded`: GameInfo found, streaming levels in place) and its
  streaming levels have then stayed in place for 5 s.
- **Area**: the bounds of the map's ground combat volumes (`GameMode_CombatVolume` with `bGroundVolumeType`,
  `BrushComponent.Bounds`). Without them, the spawn points plus 4000 units.
- **Flood fill**: starts at every spawn point (`PlayerStart`) and objective in the area, over a 48-unit grid (about
  1 m; a soldier is a 15 x 42 cylinder, steps 26 units, walks on floors with normal Z >= 0.70). For each grid
  neighbour, a box probe finds the floor; the rise must be a step, a walkable slope or a drop of at most 250 units
  (one-way); then a soldier-sized box is swept across, lifted by a step. Traces are `World.Trace` with `TraceWorld`.
- **Cost**: fl-farm has 292k nodes and 2.2M links from 2.5M traces in about 12 s of trace time, spread over about a
  minute at 8 ms per tick, so the server keeps running. The cache loads in about a second. Delete a cache (or
  `!bots nav rebuild`) to regenerate.
- `HomefrontBots\build_navs.py [--rebuild] [--maps ...]` rotates the server through every map (Ground Control where the map
  has it, otherwise TDM) with RCON `admin NextMap` and waits for each graph. `--rebuild` deletes only the caches of
  the maps in the run.

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

File format (`nav\<map>.nav`, little-endian): header `"HFNV"`, version 2, spacing, soldier radius, half height,
step, walkable Z (floats), node count, link count; then each node's floor point (3 floats); then per node a link
count (1 byte) and that many node indices (uint32).

## Movement

- **Goal**: an active `Objective_GameMode` that the bot's team doesn't own (nearer is better, with randomness);
  else one it owns, to hold; else roam. At an objective, it keeps picking spots within 450 units.
- **Path**: A* over the graph, with one path search per server tick across all bots.
- **Steering**: sets `Pawn.Acceleration` and the walking `Velocity` toward the next node, and `SetFocalPoint` ahead
  so it looks where it goes.
- **Stuck** (moved less than 40 units in 1.5 s): it jumps, then picks a new goal.

Known gaps: bots stand still while fighting (no cover or strafing), there are no ladders, and paths zig-zag along
the grid.

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
