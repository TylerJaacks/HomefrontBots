using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HomefrontSDK;

namespace HomefrontBots;

/// <summary>HomefrontBots: player bots for the dedicated server. Bots join as players, spawn and respawn by the game's
/// rules, fight with the game's own AI, and walk to objectives over a navigation graph generated per map.
/// Chat: "!bots ...".</summary>
[Script(Name = "HomefrontBots", Description = "player bots with generated navigation (!bots in chat)")]
public sealed class BotsScript : Script
{
    // The nav graph starts once the map's streaming levels have been in place this long: a graph traced against a
    // half-streamed map would be wrong, and cached.
    const long SettleMs = 5000, GiveUpMs = 180000;
    const double NavBudgetMs = 8.0;  // graph building per tick

    internal Nav Nav = null!;
    internal readonly Random Random = new();
    Bots bots = null!;

    GameInfo? game;
    string map = "";
    bool navPending;
    long navNextTry, mapSince, settledSince;

    // This map's capture points (Objective_GameMode), found once per map.
    List<Objective_GameMode>? objectives;

    public override void OnStart()
    {
        Nav = new Nav(Log, Directory);
        bots = new Bots(this);
        Bridge.Handle("bots.list", _ => BotsForEditor());
        Bridge.Handle("bots.nav", r => NavForEditor(r["links"]?.GetValue<bool>() ?? false));
        Log.Write("HomefrontBots started");
    }

    // ---- HomefrontEditor: the bots with their paths, and the nav graph (packed, base64)
    JsonArray BotsForEditor()
    {
        var list = new JsonArray();
        foreach (Bot b in bots.All)
        {
            var o = new JsonObject { ["name"] = b.Name, ["goal"] = b.Brain.Describe() };
            if (b.Controller.IsValid)
            {
                o["controller"] = (long)b.Controller.Ptr;
                o["team"] = b.Controller.TeamIndex;
                o["state"] = b.Controller.StateName;
                if (b.Controller.Pawn is Pawn p && p.Alive)
                {
                    FVector at = p.Location;
                    o["pawn"] = (long)p.Ptr;
                    (o["x"], o["y"], o["z"]) = (at.X, at.Y, at.Z);
                    if (Nav.Ready)
                    {
                        var path = new JsonArray();
                        for (int i = b.Brain.Next; i < b.Brain.Path.Count && path.Count < 600; ++i)
                        {
                            FVector s = Nav.Stand(b.Brain.Path[i]);
                            path.Add(s.X);
                            path.Add(s.Y);
                        }
                        o["path"] = path;
                    }
                }
            }
            list.Add(o);
        }
        return list;
    }

    JsonObject NavForEditor(bool withLinks)
    {
        var result = new JsonObject { ["map"] = Game.MapName, ["status"] = Nav.Status, ["nodes"] = Nav.Count };
        if (!Nav.Ready)
            return result;
        var (floor, linkStart, links) = Nav.Graph;
        var points = new float[floor.Count * 3];
        for (int i = 0; i < floor.Count; ++i)
            (points[i * 3], points[i * 3 + 1], points[i * 3 + 2]) = (floor[i].X, floor[i].Y, floor[i].Z);
        result["floor"] = Convert.ToBase64String(MemoryMarshal.AsBytes(points.AsSpan()));
        if (withLinks)
        {
            result["linkStart"] = Convert.ToBase64String(MemoryMarshal.AsBytes(linkStart.AsSpan()));
            result["links"] = Convert.ToBase64String(MemoryMarshal.AsBytes(links.AsSpan()));
        }
        return result;
    }

    public override void OnStop()
    {
        // The bots stay in the game without their brains; take them out so a reloaded mod starts clean.
        if (bots.All.Count > 0 && Game.Info is GCGameBase g)
            g.KillBots();
    }

    public override void OnMapLoaded(GameInfo gameInfo)
    {
        game = gameInfo;
        map = Game.MapName;
        Log.Write($"map: {map}");
        Nav.Reset();
        objectives = null;
        navPending = true;
        mapSince = Environment.TickCount64;
        settledSince = 0;
    }

    public override void OnMapUnloading(string oldMap)
    {
        bots.Clear();
        Nav.Reset();
        navPending = false;
        game = null;
        objectives = null;
    }

    public override void OnTick(float deltaSeconds)
    {
        long now = Environment.TickCount64;
        if (navPending && now >= navNextTry && game != null)
        {
            navNextTry = now + 1000;
            int streaming = 0;
            if (game.IsValid && game.WorldInfo is WorldInfo info && info.StreamingSettled(out streaming))
            {
                if (settledSince == 0)
                    settledSince = now;
                if (now - settledSince >= SettleMs && Nav.Begin(game, map))
                    navPending = false;
            }
            else
            {
                settledSince = 0;
            }
            if (navPending && now - mapSince > GiveUpMs)
            {
                Log.Write($"nav: {map} never settled ({streaming} streaming level(s) pending) or has no spawn points; no graph");
                navPending = false;
            }
        }
        Nav.Tick(NavBudgetMs);
        bots.Tick(now);
    }

    internal List<Objective_GameMode> Objectives(WorldInfo? worldInfo)
    {
        if (objectives == null)
        {
            objectives = new List<Objective_GameMode>();
            foreach (Objective_GameMode o in Objects.All<Objective_GameMode>())
                if (o.WorldInfo == worldInfo)
                    objectives.Add(o);
            Log.Write($"brain: {objectives.Count} objectives on this map");
        }
        return objectives;
    }

    /// <summary>The bots, for the editor bridge.</summary>
    internal IReadOnlyList<Bot> AllBots => bots.All;

    [Command("bots", Help = "!bots add [n] [team] | list | goto [n] | bring [n] | kill | nav [rebuild] | dummy [n]")]
    void BotsCommand(CommandContext ctx)
    {
        static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);
        switch (ctx.Arg(0, "list"))
        {
            case "add":
            {
                int team = ctx.Int(2, -1);
                bots.Add(ctx, Clamp(ctx.Int(1, 1), 1, 16), team is 0 or 1 ? team : -1);
                break;
            }
            case "list":
                bots.List(ctx);
                break;
            case "goto":
                bots.GoTo(ctx, ctx.Int(1, 0));
                break;
            case "bring":
                bots.Bring(ctx, ctx.Int(1, 0));
                break;
            case "kill":
                bots.RemoveAll(ctx);
                break;
            case "dummy":
                bots.AddDummies(ctx, Clamp(ctx.Int(1, 1), 1, 16));
                break;
            case "nav":
                if (ctx.Arg(1) == "rebuild" && Game.Info is GameInfo g)
                {
                    navPending = false;
                    Nav.Begin(g, Game.MapName, rebuild: true);
                    ctx.Reply("[bots] rebuilding the nav graph: " + Nav.Status);
                }
                else
                {
                    ctx.Reply("[bots] nav " + Nav.Status);
                }
                break;
            default:
                ctx.Reply("[bots] " + "add [n] [team] | list | goto [n] | bring [n] | kill | nav [rebuild] | dummy [n]");
                break;
        }
    }
}
