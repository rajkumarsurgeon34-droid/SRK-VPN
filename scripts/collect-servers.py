#!/usr/bin/env python3
"""Collect VPN Gate servers every hour and keep every server seen in the last 48 hours.

VPN Gate's public API returns only about 100 servers per request, and a different
set each time. Merging the hourly snapshots gives SRK VPN many more servers and
countries than a single download. Output keeps VPN Gate's own CSV format, so the
app parses it exactly like the live list.

Usage: collect-servers.py <out-dir>   (reads/writes <out-dir>/vpngate.csv and seen.json)
"""
import base64, json, os, sys, time, urllib.request

OUT = sys.argv[1]
KEEP_SECONDS = 48 * 3600
SOURCES = os.environ.get("SRK_VG_SOURCES", "https://www.vpngate.net/api/iphone/ http://www.vpngate.net/api/iphone/").split()
HEADER = ("#HostName,IP,Score,Ping,Speed,CountryLong,CountryShort,NumVpnSessions,Uptime,"
          "TotalUsers,TotalTraffic,LogType,Operator,Message,OpenVPN_ConfigData_Base64")

def download():
    for src in SOURCES:
        for attempt in range(3):
            try:
                req = urllib.request.Request(src, headers={"User-Agent": "SRK-VPN-collector/1.0"})
                text = urllib.request.urlopen(req, timeout=60).read().decode("utf-8", "replace")
                if "OpenVPN_ConfigData_Base64" in text:
                    return text
            except Exception as e:
                print(f"  {src} attempt {attempt + 1}: {e}")
                time.sleep(5)
    return None

def key_of(fields):
    """Same identity the app uses: host-ip-port."""
    try:
        cfg = base64.b64decode(fields[-1]).decode("utf-8", "replace")
    except Exception:
        return None, None
    if "<ca>" not in cfg:
        return None, None
    port = ""
    for line in cfg.splitlines():
        p = line.split()
        if len(p) >= 3 and p[0] == "remote":
            port = p[2]
            break
    return f"{fields[0]}-{fields[1]}-{port}", cfg

os.makedirs(OUT, exist_ok=True)
seen_path = os.path.join(OUT, "seen.json")
try:
    seen = json.load(open(seen_path))       # id -> {"t": last_seen, "row": csv line}
except Exception:
    seen = {}

now = int(time.time())
text = download()
fresh = 0
if text:
    for line in text.splitlines():
        line = line.strip()
        if not line or line[0] in "*#":
            continue
        f = line.split(",")
        if len(f) < 15:
            continue
        k, _ = key_of(f)
        if not k:
            continue
        seen[k] = {"t": now, "row": line}
        fresh += 1
else:
    print("VPN Gate list could not be downloaded this hour; keeping previous servers.")

seen = {k: v for k, v in seen.items() if now - v["t"] <= KEEP_SECONDS}
# newest first, then by VPN Gate score
def score(v):
    f = v["row"].split(",")
    return int(f[2]) if f[2].isdigit() else 0
rows = sorted(seen.values(), key=lambda v: (-v["t"], -score(v)))
with open(os.path.join(OUT, "vpngate.csv"), "w") as fh:
    fh.write(f"*vpn_servers SRK collected {time.strftime('%Y-%m-%d %H:%M UTC', time.gmtime(now))}\n")
    fh.write(HEADER + "\n")
    for v in rows:
        fh.write(v["row"] + "\n")
    fh.write("*\n")
json.dump(seen, open(seen_path, "w"))
countries = {v["row"].split(",")[6] for v in rows}
print(f"fresh this hour: {fresh} · kept (48 h): {len(rows)} servers in {len(countries)} countries")
if not rows:
    sys.exit(1)
