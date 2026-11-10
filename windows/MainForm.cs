using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SrkVpn;

static class App
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRK VPN");
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Directory.CreateDirectory(App.DataDir);
        using var single = new Mutex(true, "SRKVPN-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("SRK VPN is already open.", "SRK VPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

/// <summary>SRK VPN window: the shared HTML screen in WebView2, wired to OpenVPN and VPN Gate.</summary>
sealed class MainForm : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 16, 32) };
    readonly HttpClient http;
    readonly VpnGate gate;
    readonly OpenVpnRunner vpn = new();
    string lastState = "{\"state\":\"NOPROCESS\",\"level\":\"LEVEL_NOTCONNECTED\",\"msg\":\"\"}";

    const string Shim = @"
(() => {
  let st = '{""state"":""NOPROCESS"",""level"":""LEVEL_NOTCONNECTED"",""msg"":""""}';
  const post = (cmd, arg) => window.chrome.webview.postMessage(JSON.stringify({ cmd, arg: arg == null ? '' : String(arg) }));
  window.__srkSetState = s => { st = s; };
  window.SRK = {
    loadServers: f => post('loadServers', f ? '1' : '0'),
    connect: id => post('connect', id),
    disconnect: () => post('disconnect'),
    state: () => st,
    checkIp: () => post('checkIp'),
    probe: ids => post('probe', ids),
    toast: m => post('toast', m),
  };
})();";

    public MainForm()
    {
        Text = "SRK VPN";
        ClientSize = new System.Drawing.Size(460, 820);
        MinimumSize = new System.Drawing.Size(380, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(11, 16, 32);
        try { Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "srkvpn.ico")); } catch { }
        Controls.Add(web);

        http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(30) });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SRK-VPN/1.0 (Windows)");
        http.Timeout = TimeSpan.FromSeconds(30);
        gate = new VpnGate(http);

        vpn.StateChanged += (state, level, msg) =>
        {
            lastState = JsonSerializer.Serialize(new { state, level, msg = msg ?? "" });
            Emit("state", lastState);
        };
        vpn.Bytes += (i, o, di, dout) => Emit("bytes", JsonSerializer.Serialize(new { @in = i, @out = o, diffIn = di, diffOut = dout }));

        Load += async (_, _) => await InitWebAsync();
        FormClosing += (_, _) => { try { vpn.Stop(wait: true); } catch { } };
    }

    async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            MessageBox.Show("SRK VPN needs Microsoft Edge WebView2 Runtime (built into Windows 11).\n\n" + e.Message,
                "SRK VPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping("srkvpn.app", Path.Combine(AppContext.BaseDirectory, "www"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.Navigate("https://srkvpn.app/index.html");
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd, arg;
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            cmd = doc.RootElement.GetProperty("cmd").GetString();
            arg = doc.RootElement.TryGetProperty("arg", out var a) ? a.GetString() : "";
        }
        catch { return; }

        switch (cmd)
        {
            case "loadServers":
                _ = Task.Run(async () =>
                {
                    try { Emit("servers", await gate.LoadAsync(arg == "1")); }
                    catch (Exception ex) { Emit("serversError", Err(ex)); }
                });
                break;
            case "connect":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var s = gate.Get(arg) ?? throw new Exception("Server no longer in list — refresh");
                        vpn.Start(s.Config, $"{s.Country} · {s.Host}");
                        Emit("connecting", JsonSerializer.Serialize(new { id = arg }));
                    }
                    catch (Exception ex) { Emit("connectError", Err(ex)); }
                });
                break;
            case "disconnect":
                _ = Task.Run(() => vpn.Stop(wait: true));
                break;
            case "probe":
                _ = Task.Run(() => ProbeAsync(arg));
                break;
            case "checkIp":
                _ = Task.Run(CheckIpAsync);
                break;
            case "toast":
                break;
        }
    }

    /// <summary>Measures how quickly each server answers from this PC (TCP connect time), in parallel.</summary>
    async Task ProbeAsync(string idsJson)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(idsJson) ?? Array.Empty<string>();
            await Task.WhenAll(ids.Select(async id =>
            {
                var s = gate.Get(id);
                if (s == null) return;
                // TCP servers: their OpenVPN port. UDP servers: SoftEther also listens on TCP 443/992.
                var ports = new List<int>();
                if (s.Proto.StartsWith("tcp") && int.TryParse(s.Port, out var tp)) ports.Add(tp);
                ports.Add(443); ports.Add(992);
                int best = -1;
                foreach (var port in ports.Distinct())
                {
                    int ms = await TcpPingAsync(s.Ip, port, 2500);
                    if (ms > 0) { best = ms; break; }
                }
                results[id] = best;
            }));
        }
        catch { }
        Emit("probe", JsonSerializer.Serialize(new { results }));
    }

    static async Task<int> TcpPingAsync(string ip, int port, int timeoutMs)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await c.ConnectAsync(ip, port, cts.Token);
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch { return -1; }
    }

    async Task CheckIpAsync()
    {
        try
        {
            string json;
            try
            {
                using var j = JsonDocument.Parse(await http.GetStringAsync("https://ipwho.is/"));
                var r = j.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                string isp = r.TryGetProperty("connection", out var c) && c.TryGetProperty("isp", out var i) ? i.GetString() : "";
                if (string.IsNullOrEmpty(S("ip"))) throw new Exception("no ip");
                json = JsonSerializer.Serialize(new { ip = S("ip"), country = S("country"), cc = S("country_code"), city = S("city"), isp });
            }
            catch
            {
                using var j = JsonDocument.Parse(await http.GetStringAsync("https://ipinfo.io/json"));
                var r = j.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                json = JsonSerializer.Serialize(new { ip = S("ip"), country = "", cc = S("country"), city = S("city"), isp = S("org") });
            }
            Emit("ip", json);
        }
        catch (Exception ex) { Emit("ipError", Err(ex)); }
    }

    static string Err(Exception e) => JsonSerializer.Serialize(new { error = e.Message });

    void Emit(string ev, string json)
    {
        if (IsDisposed) return;
        string js = (ev == "state" ? $"window.__srkSetState && window.__srkSetState({JsonSerializer.Serialize(json)});" : "")
                    + $"window.__vpn && window.__vpn({JsonSerializer.Serialize(ev)}, {json ?? "null"});";
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (web.CoreWebView2 != null) _ = web.CoreWebView2.ExecuteScriptAsync(js);
            }));
        }
        catch { }
    }
}
