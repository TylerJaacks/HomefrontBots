"""Build the HomefrontBots navigation graph for every multiplayer map.

Starts the dedicated server with a playlist of every map (Ground Control where the map has it, otherwise Team
Deathmatch), waits on each map until the HomefrontBots mod logs its graph saved (or loaded from the cache), then
moves on with the RCON command "admin NextMap". The playlist's Map= lines are replaced for the run and put
back afterwards; the server is stopped at the end.

usage: python HomefrontBots/build_navs.py [--rebuild] [--maps fl-farm,FL-BigBox?GameMode=TDM] [--timeout 900]
  --rebuild   delete the cached graphs of the maps in the run first, so they are generated again
  --maps      comma-separated playlist entries instead of all maps (GameMode defaults to FL)
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
PLAYLIST = os.path.join(ROOT, "Homefront Dedicated Server", "GCGame", "Config", "DedicatedProfile_DefaultProfile", "DedicatedPlaylist")
MOD = os.path.join(BIN, "HomefrontSDK", "Mods", "HomefrontBots")
LOG = os.path.join(MOD, "HomefrontBots.log")
CACHE = os.path.join(MOD, "nav")
LAUNCH = ["HFDedicated.exe", "SERVER", "?maxplayers=32", "-queryport=27015", "-authport=8766",
          "-rconport1=27010", "-rconport2=27011", "-configsubdir=DedicatedProfile_DefaultProfile", "-ALLOWMULTIPLE"]


def internet_address():
    """This PC's address on the network that reaches the internet (a UDP connect sends nothing). The server must be
    told it with -MULTIHOME: left alone it may pick a virtual adapter (VMware's host-only network here), and its Steam
    logon, bound to that address, never gets out."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.connect(("8.8.8.8", 53))
        return s.getsockname()[0]

# Persistent map packages (GCGame\CookedPC\Maps\MP\*). The playlist offers Ground Control (FL) on these...
FL_MAPS = ["fl-farm", "fl-lowlands", "fl-angelisland", "FL-Crossroads", "fl-culdesac", "fl-harbor", "FL-Spillway",
           "fl-bridge"]
# ...and only Team Deathmatch on these.
TDM_MAPS = ["FL-BigBox", "fl-borderlands", "fl-alcatraz", "FL-TDMSpillway", "FL-Overpass"]


def admin_password():
    with open(PLAYLIST, encoding="latin-1") as f:
        for line in f:
            if line.startswith("AdminPassword="):
                return line.split("=", 1)[1].strip()
    raise SystemExit("no AdminPassword in " + PLAYLIST)


def set_rotation(entries):
    """Replace the playlist's Map= lines; returns the original text."""
    with open(PLAYLIST, encoding="latin-1", newline="") as f:
        text = f.read()
    nl = "\r\n" if "\r\n" in text else "\n"
    lines = [l for l in text.split(nl) if not l.startswith("Map=")]
    while lines and lines[-1] == "":
        lines.pop()
    lines += ["Map=" + e for e in entries] + [""]
    with open(PLAYLIST, "w", encoding="latin-1", newline="") as f:
        f.write(nl.join(lines))
    return text


def restore(text):
    with open(PLAYLIST, "w", encoding="latin-1", newline="") as f:
        f.write(text)


def next_map(password):
    for _ in range(5):
        r = subprocess.run([sys.executable, os.path.join(ROOT, "HomefrontSDK", "Tools", "hfrcon.py"), "--password", password,
                            "--wait", "3", "admin NextMap"], capture_output=True, text=True)
        if r.returncode == 0:
            return True
        time.sleep(3)
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rebuild", action="store_true")
    ap.add_argument("--maps")
    ap.add_argument("--timeout", type=float, default=900, help="seconds to wait on one map")
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

    password = admin_password()
    original = set_rotation(entries)
    log_start = os.path.getsize(LOG) if os.path.exists(LOG) else 0
    server = subprocess.Popen(LAUNCH + ["-MULTIHOME=" + internet_address()], cwd=BIN,
                              executable=os.path.join(BIN, "HFDedicated.exe"))
    print("server pid %d; playlist: %s" % (server.pid, ", ".join(entries)), flush=True)

    results = {}  # map (lower) -> summary line
    current, since, pos, buf = None, time.time(), log_start, ""
    try:
        while len(results) < len(wanted):
            if server.poll() is not None:
                print("server exited (code %s)" % server.returncode, flush=True)
                break
            with open(LOG, encoding="latin-1") as f:
                f.seek(pos)
                buf += f.read()
                pos = f.tell()
            lines = buf.split("\n")
            buf = lines.pop()
            for line in lines:
                m = re.search(r" map: (\S+)", line)
                if m:
                    current, since = m.group(1).lower(), time.time()
                    print("map %s" % m.group(1), flush=True)
                    continue
                if current is None or " nav: " not in line:
                    continue
                if any(k in line for k in (" nav: building", " nav: built", " nav: loaded", "no graph", "not cached")):
                    print("  " + line.strip(), flush=True)
                done = None
                if " nav: saved " in line or " nav: loaded " in line:
                    done = results.get(current, "") or line.split(" nav: ", 1)[1]
                elif " nav: built " in line:
                    if current in wanted:  # not the intermission lobby between maps
                        results[current] = line.split(" nav: ", 1)[1]
                    continue
                elif "no graph" in line or "not cached" in line:
                    done = line.split(" nav: ", 1)[1]
                if done is not None and current in wanted:
                    results[current] = done
                    if len(results) < len(wanted):
                        print("  -> next map", flush=True)
                        if not next_map(password):
                            print("  RCON NextMap failed", flush=True)
            if current and time.time() - since > args.timeout:
                print("  timed out on %s; skipping" % current, flush=True)
                results.setdefault(current, "TIMED OUT")
                since = time.time()
                next_map(password)
            time.sleep(1)
    finally:
        restore(original)
        if server.poll() is None:
            subprocess.run(["taskkill", "/PID", str(server.pid), "/F"], capture_output=True)
        print("playlist restored; server stopped", flush=True)

    print("\nresults:")
    for w in wanted:
        print("  %-16s %s" % (w, results.get(w, "not reached")))


if __name__ == "__main__":
    main()
