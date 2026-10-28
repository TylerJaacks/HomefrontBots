"""Build the HomefrontBots navigation graph for every multiplayer map.

Starts the dedicated server, then sends it to each map in turn (Ground Control where the map has it, otherwise Team
Deathmatch) with the SDK's "!sdk travel <map>" through its bridge (127.0.0.1:27040; no admin password needed), and
waits on each map until HomefrontBots logs its graph saved (or loaded from the cache). The server's playlist is left
alone; the server is stopped at the end.

usage: python HomefrontBots/build_navs.py [--rebuild] [--maps fl-farm,FL-BigBox?GameMode=TDM] [--timeout 600]
  --rebuild   delete the cached graphs of the maps in the run first, so they are generated again
  --maps      comma-separated map URLs instead of all maps (GameMode defaults to FL)
The server must not be running.
"""
import argparse
import os
import re
import socket
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "Homefront Dedicated Server", "Binaries")
MOD = os.path.join(BIN, "HomefrontSDK", "Mods", "HomefrontBots")
LOG = os.path.join(MOD, "HomefrontBots.log")
CACHE = os.path.join(MOD, "nav")
LAUNCH = ["HFDedicated.exe", "SERVER", "?maxplayers=32", "-queryport=27015", "-authport=8766",
          "-rconport1=27010", "-rconport2=27011", "-configsubdir=DedicatedProfile_DefaultProfile", "-ALLOWMULTIPLE"]

sys.path.insert(0, os.path.join(ROOT, "HomefrontSDK", "Tools"))  # python -I drops the script dir from the path
import hfbridge  # noqa: E402

# Persistent map packages (GCGame\CookedPC\Maps\MP\*). Ground Control (FL) is played on these...
FL_MAPS = ["fl-farm", "fl-lowlands", "fl-angelisland", "FL-Crossroads", "fl-culdesac", "fl-harbor", "FL-Spillway",
           "fl-bridge"]
# ...and only Team Deathmatch on these.
TDM_MAPS = ["FL-BigBox", "fl-borderlands", "fl-alcatraz", "FL-TDMSpillway", "FL-Overpass"]


def internet_address():
    """This PC's address on the network that reaches the internet (a UDP connect sends nothing). The server must be
    told it with -MULTIHOME: left alone it may pick a virtual adapter (VMware's host-only network here), and its Steam
    logon, bound to that address, never gets out."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.connect(("8.8.8.8", 53))
        return s.getsockname()[0]


def travel(url, wait_s=90):
    """Sends the server to `url` through the SDK bridge; retries while the SDK is still starting."""
    deadline = time.time() + wait_s
    while True:
        try:
            sock, reader = hfbridge.connect()
            with sock:
                msg = hfbridge.request(sock, reader, "command", line="!sdk travel " + url)
            if msg.get("ok"):
                return True
            print("  travel refused: %s" % msg.get("error"), flush=True)
            return False
        except OSError:
            if time.time() > deadline:
                print("  the SDK bridge never answered", flush=True)
                return False
            time.sleep(2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rebuild", action="store_true")
    ap.add_argument("--maps")
    ap.add_argument("--timeout", type=float, default=600, help="seconds to wait on one map")
    args = ap.parse_args()

    if args.maps:
        entries = [m if "?" in m else m + "?GameMode=FL" for m in args.maps.split(",")]
    else:
        entries = [m + "?GameMode=FL" for m in FL_MAPS] + [m + "?GameMode=TDM" for m in TDM_MAPS]
    wanted = [e.split("?")[0].lower() for e in entries]
    if subprocess.run(["tasklist", "/FI", "IMAGENAME eq HFDedicated.exe"], capture_output=True,
                      text=True).stdout.count("HFDedicated.exe"):
        raise SystemExit("stop the server first")
    if args.rebuild and os.path.isdir(CACHE):
        for f in os.listdir(CACHE):
            if f.endswith(".nav") and f[:-4].lower() in wanted:
                os.remove(os.path.join(CACHE, f))

    log_start = os.path.getsize(LOG) if os.path.exists(LOG) else 0
    server = subprocess.Popen(LAUNCH + ["-MULTIHOME=" + internet_address()], cwd=BIN,
                              executable=os.path.join(BIN, "HFDedicated.exe"))
    print("server pid %d; maps: %s" % (server.pid, ", ".join(entries)), flush=True)

    results = {}  # map (lower) -> summary line
    current, target, since, pos, buf = None, None, time.time(), log_start, ""

    def next_target():
        """Travels to the first map without a result; None when all are done."""
        nonlocal target, since
        for e, w in zip(entries, wanted):
            if w not in results:
                target, since = w, time.time()
                if current != w:
                    print("  -> %s" % e, flush=True)
                    travel(e)
                return w
        return None

    try:
        while len(results) < len(wanted):
            if server.poll() is not None:
                print("server exited (code %s)" % server.returncode, flush=True)
                break
            if os.path.exists(LOG):
                with open(LOG, encoding="latin-1") as f:
                    f.seek(pos)
                    buf += f.read()
                    pos = f.tell()
            lines = buf.split("\n")
            buf = lines.pop()
            for line in lines:
                m = re.search(r" map: (\S+)", line)
                if m:
                    current = m.group(1).lower()
                    print("map %s" % m.group(1), flush=True)
                    if target is None or (current != target and time.time() - since > 30):
                        next_target()  # the server's first map, or it wandered off: go where we want
                    continue
                if current is None or current != target or " nav: " not in line:
                    continue
                if any(k in line for k in (" nav: building", " nav: built", " nav: loaded", "no graph", "not cached")):
                    print("  " + line.strip(), flush=True)
                done = None
                if " nav: saved " in line or " nav: loaded " in line:
                    done = results.get(current, "") or line.split(" nav: ", 1)[1]
                elif " nav: built " in line:
                    results[current] = line.split(" nav: ", 1)[1]
                    continue
                elif "no graph" in line or "not cached" in line:
                    done = line.split(" nav: ", 1)[1]
                if done is not None:
                    results[current] = done
                    next_target()
            if target and time.time() - since > args.timeout:
                print("  timed out on %s; skipping" % target, flush=True)
                results.setdefault(target, "TIMED OUT")
                next_target()
            time.sleep(1)
    finally:
        if server.poll() is None:
            subprocess.run(["taskkill", "/PID", str(server.pid), "/F"], capture_output=True)
        print("server stopped", flush=True)

    print("\nresults:")
    for w in wanted:
        print("  %-16s %s" % (w, results.get(w, "not reached")))


if __name__ == "__main__":
    main()
