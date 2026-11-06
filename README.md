# HomefrontBots - player bots

A [HomefrontSDK](https://github.com/TylerJaacks/HomefrontSDK) mod for the Homefront dedicated server: bots that join
as players, spawn and respawn by the game's rules, fight with the game's own AI, and walk to objectives over a
navigation graph the mod generates for each map. How it works is in [RESEARCH.md](RESEARCH.md).

## Install from the releases

No build needed. Stop the server, then from each project's GitHub Releases page:

| Download | Put it in `Homefront Dedicated Server\Binaries\` as |
|---|---|
| [HomefrontModLoader](https://github.com/TylerJaacks/HomefrontModLoader/releases): `msimg32.dll` | `msimg32.dll` |
| [HomefrontSDK](https://github.com/TylerJaacks/HomefrontSDK/releases): `HomefrontSDK-v<x>.zip` | extracted here, giving `HomefrontSDK\HomefrontSDK.dll` and its files |
| [HomefrontBots](https://github.com/TylerJaacks/HomefrontBots/releases): `HomefrontBots.dll` | `HomefrontSDK\Mods\HomefrontBots\HomefrontBots.dll` |

Take the HomefrontSDK release that the HomefrontBots release notes name: the mod must run with the SDK it was built
against. The server machine needs the **32-bit (x86) .NET 10 runtime**. Then generate the navigation graphs (below)
and start the server.

## Build and install

**Needs:** Windows, the .NET 10 SDK, Visual Studio (or its Build Tools) with the x86 C++ tools (for the loader),
Python 3 (for `build_navs.py`), and on the server machine the **32-bit (x86) .NET 10 runtime**.

**1. Clone the repositories side by side**, next to the dedicated server. `Build.ps1` looks for
`HomefrontModLoader`, `HomefrontBots` and `Homefront Dedicated Server` beside `HomefrontSDK`:

```
<folder>\
  Homefront Dedicated Server\     the server (Binaries\HFDedicated.exe)
  HomefrontModLoader\             git clone https://github.com/TylerJaacks/HomefrontModLoader
  HomefrontSDK\                   git clone https://github.com/TylerJaacks/HomefrontSDK
  HomefrontBots\                  git clone https://github.com/TylerJaacks/HomefrontBots
```

**2. Build and install** (stop the server first). HomefrontBots builds against the HomefrontSDK checkout beside it,
the same SDK `Build.ps1` installs:

```
dotnet build HomefrontBots\HomefrontBots.slnx -c Release   build HomefrontBots
HomefrontSDK\Build.ps1 -Install                            build the loader, the SDK and HelloMod, install them with HomefrontBots
python HomefrontBots\build_navs.py                         generate the navigation graph of every map (below)
```

For a Debug build, pass `-c Debug` to both. Installed into `Homefront Dedicated Server\Binaries`:

```
msimg32.dll                                       HomefrontModLoader
HomefrontSDK\HomefrontSDK.dll                     HomefrontSDK (+ .runtimeconfig.json, .deps.json, .pdb)
HomefrontSDK\Mods\HomefrontBots\HomefrontBots.dll this mod (+ .pdb)
```

Without `Build.ps1`: build the loader with `HomefrontModLoader\build.cmd` and copy the files above by hand.

**3. Start the server** as in the SDK README, with `-MULTIHOME=<this PC's address>`, and type `!bots add 4` in chat.

## In the server

The mod's folder, `Binaries\HomefrontSDK\Mods\HomefrontBots\`, also holds its log `HomefrontBots.log` and the
navigation graphs `nav\<map>.nav`. A rebuilt DLL can be swapped in while the server runs: copy it over the old one
and type `!sdk reload HomefrontBots`. The bots stay in the game.

Bots added with `!bots add` come back on every map until `!bots kill`. Each picks a loadout at random from the
armoury's six defaults and the loadout of the player who added it.

## Navigation graphs

Bots need a graph of each map to move. The mod builds one the first time a map loads (about a minute on the largest
maps, while the server keeps running) and caches it in `nav\<map>.nav`; later loads take about a second. To build
them all ahead of time:

```
python HomefrontBots\build_navs.py                              every map
python HomefrontBots\build_navs.py --maps fl-farm,fl-harbor     only these maps
python HomefrontBots\build_navs.py --rebuild                    delete the maps' caches first (with --maps: only theirs)
```

It starts the server itself (with `-MULTIHOME=<this PC's address>`) and sends it to each map in turn; all 13 maps
take about 22 minutes. In game, `!bots nav` shows the graph's status and `!bots nav rebuild` builds this map's again.
One graph per map serves both Ground Control and Team Deathmatch.

## Chat commands (`!bots ...`)

| Command | What |
|---|---|
| `list` (or nothing) | each bot's team and goal with progress, e.g. `take Objective_GameMode_VolumeHold_3 12/340` |
| `add [n] [team]` | add n (1-16) player bots; team 0 or 1, default balanced |
| `goto [n]` | teleport yourself next to `Bot n` (default: the first with a body) |
| `bring [n]` | teleport `Bot n` (default: all) in front of you |
| `kill` | remove every bot |
| `nav` / `nav rebuild` | the graph's status / build it again for this map |
| `imitation [...]` | behaviour learned from real players: status, `on`/`off`, `reload`, `combat\|stance\|routes\|spots on\|off`, `temp <t>` (below) |
| `dummy [n]` | spawn n stationary dummies next to you with your loadout (the developers' test spawner; not players) |
| `ladder [n] [down]` | test: puts the first bot at the foot (or top) of the map's ladder n and sends it up (or down); `!bots ladder` says how many |
| `die [n]` | test: kills bot n (default: the first with a body) |
| `loadouts` | the loadouts bots draw from, and each bot's loadout and weapon in hand |

Commands are kept out of chat and answered with a chat line to the sender. They also work from a tool, without a
player: `python HomefrontSDK\Tools\hfbridge.py command "!bots add 4"`. `goto`, `bring` and `dummy` need a player in
game. Tools can also ask the bridge for `bots.list` (each bot's position, state, goal and remaining path) and
`bots.nav` (the graph's nodes; links too with `"links": true`).

## Learning from real players

With [HomefrontImitation](https://github.com/TylerJaacks/HomefrontImitation) recording real players and its trainer
writing `Mods\HomefrontBots\imitation\policy.json` and `<map>.heat`, bots:

- **fight** like the humans did in the same spot: each 0.1 s the policy picks a move relative to the enemy (or
  holding still), never into a wall, walked along a straight line of the nav graph (`combat`);
- crouch, sprint and jump when and as often as humans do (`stance`);
- prefer the floor humans walk: A* adds up to `routeBias` (0.6) x distance on floor nobody used (`routes`);
- hold objectives from the spots humans hold and win fights from (`spots`).

Each part falls back to the behaviour above without its file. `!bots imitation` shows what's in use; settings are in
`imitation\imitation.json`. The shared code in `Imitation\Features.cs`, `Policy.cs` and `Senses.cs` is a copy of
HomefrontImitation's `Shared\` and must stay identical (its `tests/e2e.py` checks). How it works:
[RESEARCH.md](RESEARCH.md#learned-behaviour).

## Known limitations

- Bots don't take cover: they strafe in the open.
- Ladder climbing is untested in game.
