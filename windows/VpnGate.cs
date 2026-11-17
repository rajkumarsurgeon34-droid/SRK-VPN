using System.Text;
using System.Text.Json;

namespace SrkVpn;

/// <summary>Loads the public VPN Gate server list and keeps each server's OpenVPN config in memory.</summary>
sealed class VpnGate
{
    static readonly string[] Sources =
    {
        "https://www.vpngate.net/api/iphone/",
        "http://www.vpngate.net/api/iphone/",
    };
    // Every server VPN Gate listed in the last 48 hours, collected hourly by the "Collect free servers" workflow.
    const string Collected = "https://raw.githubusercontent.com/rajkumarsurgeon34-droid/SRK-VPN/server-list/vpngate.csv";

    public sealed class Server
    {
        public string Id, Host, Ip, Country, Cc, Proto = "udp", Port = "", Config;
        public long Score, Speed, Sessions, Uptime;
        public int Ping;
    }

    readonly Dictionary<string, Server> servers = new();
    readonly object gate = new();
    readonly HttpClient http;

    public VpnGate(HttpClient http) { this.http = http; }

    public Server Get(string id)
    {
        lock (gate) return servers.TryGetValue(id ?? "", out var s) ? s : null;
    }

    public async Task<string> LoadAsync(bool forceNetwork)
    {
        string cache = Path.Combine(App.DataDir, "vpngate-cache.csv");
        string csv = null;
        bool fromCache = false;
        DateTime fetched = DateTime.UtcNow;
        Exception last = null;

        if (!forceNetwork && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromMinutes(20))
        {
            csv = await File.ReadAllTextAsync(cache);
            fetched = File.GetLastWriteTimeUtc(cache);
        }
        if (csv == null)
        {
            // Live list and the collected 48-hour list, downloaded together; live entries win duplicates.
            var collectedTask = DownloadAsync(Collected);
            string live = null;
            foreach (var src in Sources)
            {
                try
                {
                    var text = await DownloadAsync(src);
                    if (text.Contains("OpenVPN_ConfigData_Base64")) { live = text; break; }
                }
                catch (Exception e) { last = e; }
            }
            string collected = null;
            try
            {
                var text = await collectedTask;
                if (text.Contains("OpenVPN_ConfigData_Base64")) collected = text;
            }
            catch (Exception e) { last ??= e; }
            csv = live != null && collected != null ? live + "\n" + collected : live ?? collected;
            if (csv != null)
            {
                await File.WriteAllTextAsync(cache, csv);
                fetched = DateTime.UtcNow;
            }
            else if (File.Exists(cache))
            {
                csv = await File.ReadAllTextAsync(cache);
                fromCache = true;
                fetched = File.GetLastWriteTimeUtc(cache);
            }
            else throw last ?? new Exception("Server list unavailable");
        }
        return Parse(csv, fetched, fromCache);
    }

    async Task<string> DownloadAsync(string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await http.GetStringAsync(url, cts.Token);
    }

    string Parse(string csv, DateTime fetched, bool fromCache)
    {
        var list = new List<Server>();
        lock (gate)
        {
            servers.Clear();
            foreach (var raw in csv.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '*' || line[0] == '#') continue;
                var f = line.Split(',');
                if (f.Length < 15) continue;
                var s = new Server
                {
                    Host = f[0], Ip = f[1], Score = Num(f[2]), Ping = (int)Num(f[3]), Speed = Num(f[4]),
                    Country = f[5], Cc = f[6].ToUpperInvariant(), Sessions = Num(f[7]), Uptime = Num(f[8]),
                };
                try { s.Config = Encoding.UTF8.GetString(Convert.FromBase64String(f[^1])); }
                catch { continue; }
                if (!s.Config.Contains("<ca>")) continue;
                foreach (var cl in s.Config.Split('\n'))
                {
                    var t = cl.Trim();
                    if (t.StartsWith("proto ")) s.Proto = t[6..].Trim();
                    else if (t.StartsWith("remote "))
                    {
                        var p = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3) s.Port = p[2];
                    }
                }
                s.Id = $"{s.Host}-{s.Ip}-{s.Port}";
                if (servers.ContainsKey(s.Id)) continue;
                servers[s.Id] = s;
                list.Add(s);
            }
        }
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        var payload = new
        {
            servers = list.Select(s => new
            {
                id = s.Id, host = s.Host, ip = s.Ip, country = s.Country, cc = s.Cc, score = s.Score, ping = s.Ping,
                speed = s.Speed, sessions = s.Sessions, uptime = s.Uptime, proto = s.Proto, port = s.Port,
            }),
            fetchedAt = new DateTimeOffset(fetched).ToUnixTimeMilliseconds(),
            fromCache,
        };
        return JsonSerializer.Serialize(payload);
    }

    static long Num(string s) => long.TryParse(s.Trim(), out var v) ? v : 0;
}
