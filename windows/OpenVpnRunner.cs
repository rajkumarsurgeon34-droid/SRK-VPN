using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace SrkVpn;

/// <summary>Runs the official OpenVPN engine (openvpn.exe) and reports its state.</summary>
sealed class OpenVpnRunner
{
    /// <summary>(state, level, message) — level names match the Android engine so the same UI works.</summary>
    public event Action<string, string, string> StateChanged;
    /// <summary>(totalIn, totalOut, diffIn, diffOut) every 2 seconds.</summary>
    public event Action<long, long, long, long> Bytes;

    readonly object sync = new();
    Process proc;
    int generation;
    int mgmtPort;
    TcpClient mgmt;
    long lastIn, lastOut;
    bool connected;
    int failedGen = -1;

    /// <summary>Reports a failed connection attempt once per attempt.</summary>
    void Fail(int gen, string why)
    {
        lock (sync) { if (gen != generation || failedGen == gen || connected) return; failedGen = gen; }
        StateChanged?.Invoke("FAILED", "LEVEL_NOTCONNECTED", why);
    }

    public bool Running { get { lock (sync) return proc != null && !proc.HasExited; } }

    public static string FindOpenVpnDir()
    {
        var candidates = new List<string>();
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\OpenVPN");
            if (k?.GetValue("exe_path") is string exe) candidates.Add(Path.GetDirectoryName(exe));
            if (k?.GetValue("bin_dir") is string bin) candidates.Add(bin);
            if (k?.GetValue("") is string dir) candidates.Add(Path.Combine(dir, "bin"));
        }
        catch { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin"));
        candidates.Add(@"C:\Program Files\OpenVPN\bin");
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "openvpn.exe"))) return c;
        return null;
    }

    /// <summary>Makes sure a TAP-Windows adapter exists (the installer normally creates one).</summary>
    static void EnsureAdapter(string bin)
    {
        var tapctl = Path.Combine(bin, "tapctl.exe");
        if (!File.Exists(tapctl)) return;
        try
        {
            var list = RunTool(tapctl, "list --hwid root\\tap0901");
            if (string.IsNullOrWhiteSpace(list))
                RunTool(tapctl, "create --hwid root\\tap0901 --name \"SRK VPN\"");
        }
        catch { }
    }

    static string RunTool(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        });
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return o;
    }

    public void Start(string serverConfig, string label)
    {
        Stop(wait: true);
        var bin = FindOpenVpnDir() ?? throw new Exception("OpenVPN engine not found. Please reinstall SRK VPN.");
        EnsureAdapter(bin);

        mgmtPort = FreePort();
        string cipher = null;
        var sb = new StringBuilder();
        foreach (var raw in serverConfig.Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("cipher ")) cipher = t[7..].Trim();
            // options we set ourselves below
            if (t.StartsWith("management") || t.StartsWith("windows-driver") || t.StartsWith("data-ciphers")
                || t.StartsWith("connect-retry") || t.StartsWith("resolv-retry") || t.StartsWith("server-poll-timeout")
                || t.StartsWith("hand-window") || t.StartsWith("ip-win32") || t.StartsWith("connect-timeout")) continue;
            sb.Append(raw.TrimEnd()).Append('\n');
        }
        // VPN Gate relays run SoftEther with older OpenVPN settings: use the TAP driver (no DCO for CBC),
        // keep their cipher as a fallback, and relax the TLS profile for their certificates.
        sb.Append("\n# --- added by SRK VPN ---\n");
        sb.Append("disable-dco\n");
        sb.Append("windows-driver tap-windows6\n");
        string ciphers = "AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305";
        if (!string.IsNullOrEmpty(cipher))
        {
            if (!ciphers.Contains(cipher)) ciphers += ":" + cipher;
            sb.Append($"data-ciphers-fallback {cipher}\n");
        }
        sb.Append($"data-ciphers {ciphers}\n");
        sb.Append("tls-cert-profile insecure\n");
        sb.Append("block-outside-dns\n");
        // Fail fast on dead relays so the app can move to the next server instead of waiting minutes.
        sb.Append("server-poll-timeout 8\n");
        sb.Append("connect-retry 1\n");
        sb.Append("connect-retry-max 1\n");
        sb.Append("resolv-retry 5\n");
        sb.Append("hand-window 15\n");
        // Set the adapter address directly instead of waiting for Windows DHCP on the TAP adapter.
        sb.Append("ip-win32 netsh\n");
        sb.Append($"management 127.0.0.1 {mgmtPort}\n");
        sb.Append("verb 3\n");

        var cfg = Path.Combine(App.DataDir, "current.ovpn");
        File.WriteAllText(cfg, sb.ToString());

        var psi = new ProcessStartInfo(Path.Combine(bin, "openvpn.exe"), $"--config \"{cfg}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = App.DataDir,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        int gen;
        lock (sync) { gen = ++generation; proc = p; connected = false; }
        p.OutputDataReceived += (_, e) => OnLine(gen, e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(gen, e.Data);
        p.Exited += (_, _) =>
        {
            if (!Current(gen)) return;
            CloseMgmt();
            bool wasUp;
            lock (sync) wasUp = connected;
            if (wasUp) StateChanged?.Invoke("NOPROCESS", "LEVEL_NOTCONNECTED", "OpenVPN stopped");
            else Fail(gen, "server did not answer");
        };
        File.WriteAllText(Path.Combine(App.DataDir, "openvpn.log"), $"SRK VPN — {label}\n");
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        StateChanged?.Invoke("CONNECTING", "LEVEL_CONNECTING_NO_SERVER_REPLY_YET", label);
        _ = Task.Run(() => ManagementLoop(gen));
    }

    bool Current(int gen) { lock (sync) return gen == generation; }

    void OnLine(int gen, string line)
    {
        if (line == null) return;
        try { File.AppendAllText(Path.Combine(App.DataDir, "openvpn.log"), line + "\n"); } catch { }
        if (!Current(gen)) return;
        if (line.Contains("Initialization Sequence Completed"))
        {
            lock (sync) connected = true;
            StateChanged?.Invoke("CONNECTED", "LEVEL_CONNECTED", "");
        }
        else if (line.Contains("TLS key negotiation failed") || line.Contains("TLS Error")
                 || line.Contains("Connection refused") || line.Contains("Connection reset")
                 || line.Contains("Exiting due to fatal error") || line.Contains("Cannot load"))
            Fail(gen, "server did not answer");
        else if (line.Contains("AUTH_FAILED"))
            StateChanged?.Invoke("AUTH_FAILED", "LEVEL_AUTH_FAILED", line);
        else if (line.Contains("TLS: Initial packet"))
            StateChanged?.Invoke("AUTH", "LEVEL_CONNECTING_SERVER_REPLIED", "");
        else if (line.Contains("PUSH: Received control message"))
            StateChanged?.Invoke("GET_CONFIG", "LEVEL_CONNECTING_SERVER_REPLIED", "");
        else if (line.Contains("SIGUSR1") || line.Contains("Restart pause"))
            StateChanged?.Invoke("RECONNECTING", "LEVEL_CONNECTING_NO_SERVER_REPLY_YET", "");
        else if (line.Contains("All TAP-Windows adapters on this system are currently in use") || line.Contains("There are no TAP-Windows"))
            StateChanged?.Invoke("NOPROCESS", "LEVEL_NOTCONNECTED", "No free VPN network adapter");
    }

    async Task ManagementLoop(int gen)
    {
        // The engine opens its management port shortly after starting.
        for (int i = 0; i < 40 && Current(gen); i++)
        {
            try
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, mgmtPort);
                lock (sync) { if (gen != generation) { c.Dispose(); return; } mgmt = c; }
                var stream = c.GetStream();
                var w = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
                await w.WriteLineAsync("bytecount 2");
                lastIn = lastOut = 0;
                using var r = new StreamReader(stream);
                string line;
                while ((line = await r.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith(">BYTECOUNT:")) continue;
                    var parts = line[11..].Split(',');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var bin) && long.TryParse(parts[1], out var bout))
                    {
                        Bytes?.Invoke(bin, bout, Math.Max(0, bin - lastIn), Math.Max(0, bout - lastOut));
                        lastIn = bin; lastOut = bout;
                    }
                }
                return;
            }
            catch { await Task.Delay(500); }
        }
    }

    public void Stop(bool wait)
    {
        Process p;
        TcpClient m;
        lock (sync) { p = proc; m = mgmt; proc = null; mgmt = null; generation++; }
        if (p == null) return;
        try
        {
            if (m != null && m.Connected)
            {
                var w = new StreamWriter(m.GetStream()) { AutoFlush = true, NewLine = "\n" };
                w.WriteLine("signal SIGTERM");
            }
        }
        catch { }
        try
        {
            if (!p.HasExited && !p.WaitForExit(wait ? 5000 : 1500)) p.Kill(true);
        }
        catch { }
        try { m?.Dispose(); } catch { }
        StateChanged?.Invoke("NOPROCESS", "LEVEL_NOTCONNECTED", "");
    }

    void CloseMgmt()
    {
        TcpClient m;
        lock (sync) { m = mgmt; mgmt = null; }
        try { m?.Dispose(); } catch { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
