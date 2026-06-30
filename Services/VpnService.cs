using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

public sealed class VpnService : IDisposable
{
    private Process? _proc;
    private readonly HttpClient _http;
    private string? _savedDns;

    public bool IsConnected => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    private static string XrayDir => Path.Combine(AppPaths.EngineDir, "xray");
    private static string XrayExe => Path.Combine(XrayDir, "xray.exe");
    private static string XrayConfigPath => Path.Combine(XrayDir, "config.json");
    private static string SubCachePath => Path.Combine(AppPaths.Root, "vpn_subscription.txt");

    private const string XrayReleaseApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
    public const string VpnSubscriptionUrl = "https://tepaqq.mooo.com/s/V9UygbKuEvfjSy0KYPIgH3sLSQbXo6l-6_LCrTAjwrm208Cy/VPN/b64";

    public VpnService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretUI", "2.2"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // ---- subscription parsing ----

    public static List<VpnServer> ParseSubscription(string raw)
    {
        var result = new List<VpnServer>();
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim())); }
        catch { return result; }

        foreach (var line in decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) continue;
            var server = ParseVlessUri(trimmed);
            if (server is not null) result.Add(server);
        }
        return result;
    }

    private static VpnServer? ParseVlessUri(string uri)
    {
        try
        {
            var rest = uri["vless://".Length..];
            int hashIdx = rest.IndexOf('#');
            string name = hashIdx >= 0 ? Uri.UnescapeDataString(rest[(hashIdx + 1)..]) : "Server";
            if (hashIdx >= 0) rest = rest[..hashIdx];

            int qIdx = rest.IndexOf('?');
            string authority = qIdx >= 0 ? rest[..qIdx] : rest;
            string query = qIdx >= 0 ? rest[(qIdx + 1)..] : "";

            var parts = authority.Split('@');
            if (parts.Length != 2) return null;

            string uuid = parts[0];
            var hostPort = parts[1].Split(':');
            if (hostPort.Length != 2) return null;

            string address = hostPort[0];
            if (!int.TryParse(hostPort[1], out int port)) return null;

            var ps = System.Web.HttpUtility.ParseQueryString(query);

            return new VpnServer
            {
                Name = name,
                Address = address,
                Port = port,
                Uuid = uuid,
                Security = ps["security"] ?? "reality",
                Sni = ps["sni"] ?? "",
                Fingerprint = ps["fp"] ?? "chrome",
                PublicKey = ps["pbk"] ?? "",
                ShortId = ps["sid"] ?? "",
                Network = ps["type"] ?? "tcp",
                Host = ps["host"] ?? ps["ahost"] ?? "",
                Spx = Uri.UnescapeDataString(ps["spx"] ?? ""),
                RawUri = uri,
            };
        }
        catch { return null; }
    }

    // ---- subscription fetch ----

    public async Task<List<VpnServer>> FetchSubscriptionAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        File.WriteAllText(SubCachePath, raw, Encoding.UTF8);
        return ParseSubscription(raw);
    }

    public List<VpnServer> LoadCachedSubscription()
    {
        if (!File.Exists(SubCachePath)) return new();
        return ParseSubscription(File.ReadAllText(SubCachePath, Encoding.UTF8));
    }

    public List<VpnServer> GetDefaultServers()
    {
        var servers = new List<VpnServer>();
        string[] uris = [
            "vless://52729ad8-eb3f-4ab8-89b7-f6715c81623f@31.76.14.166:9443?encryption=none&security=reality&sni=www.cloudflare.com&fp=safari&pbk=tr7AGu2HJSs2PMWJWVJu5Wb_j4m30D5XydUB5mJZAlE&sid=ec58f673d73750cb&type=tcp&spx=%2F#Moscow",
            "vless://3e1dad94-2e0f-4ad7-8a53-8b304397bd65@31.76.14.166:444?encryption=none&security=reality&sni=n.sni-347-default.ssl.fastly.net&fp=firefox&pbk=tr7AGu2HJSs2PMWJWVJu5Wb_j4m30D5XydUB5mJZAlE&sid=8bf978f2a63c420a&type=xhttp&spx=%2F&path=%2Fxhttp&mode=auto#Saint-Petersburg"
        ];
        foreach (var uri in uris)
        {
            var server = ParseVlessUri(uri);
            if (server is not null) servers.Add(server);
        }
        return servers;
    }

    // ---- xray download ----

    public bool IsXrayInstalled => File.Exists(XrayExe);

    public async Task DownloadXrayAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(XrayDir);

        using var resp = await _http.GetAsync(XrayReleaseApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        string? zipUrl = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("64", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (zipUrl is null) throw new InvalidOperationException("Не найден xray-core для Windows x64.");

        string zipPath = Path.Combine(Path.GetTempPath(), "xray-core.zip");
        using (var dl = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            dl.EnsureSuccessStatusCode();
            long total = dl.Content.Headers.ContentLength ?? 0;
            await using var stream = await dl.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                string name = entry.Name;
                if (string.IsNullOrEmpty(name)) continue;
                string dest = Path.Combine(XrayDir, name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        try { File.Delete(zipPath); } catch { }

        if (!File.Exists(XrayExe))
            throw new FileNotFoundException($"xray.exe не найден в {XrayDir} после распаковки.");
    }

    // ---- ping ----

    public async Task<int> PingAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    // ---- test connectivity through proxy ----

    public async Task<bool> TestProxyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 10809, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 8000;
            stream.WriteTimeout = 5000;

            // HTTP proxy requires ABSOLUTE URL in request line
            string request = "GET http://www.gstatic.com/generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nProxy-Connection: close\r\nConnection: close\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(reqBytes, cts.Token).ConfigureAwait(false);

            var buf = new byte[512];
            int totalRead = 0;
            while (totalRead < buf.Length)
            {
                int n = await stream.ReadAsync(buf, totalRead, buf.Length - totalRead, cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                totalRead += n;
                string partial = Encoding.ASCII.GetString(buf, 0, totalRead);
                if (partial.Contains("\r\n\r\n")) break;
            }

            string response = Encoding.ASCII.GetString(buf, 0, totalRead);
            return response.Contains("204") || response.Contains("200") || response.Contains("301") || response.Contains("302") || response.Contains("204");
        }
        catch { return false; }
    }

    /// <summary>Quick SOCKS5 test — used internally for fast connectivity check.</summary>
    public async Task<bool> TestSocksAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 10808, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            byte[] greeting = [0x05, 0x01, 0x00];
            await stream.WriteAsync(greeting, cts.Token).ConfigureAwait(false);
            var buf = new byte[2];
            await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
            if (buf[0] != 0x05) return false;
            byte[] connect = [0x05, 0x01, 0x00, 0x01, 1, 1, 1, 1, 0x00, 0x50];
            await stream.WriteAsync(connect, cts.Token).ConfigureAwait(false);
            var resp = new byte[10];
            await stream.ReadAsync(resp, cts.Token).ConfigureAwait(false);
            return resp[1] == 0x00;
        }
        catch { return false; }
    }

    // ---- xray config generation ----

    private static string GenerateConfig(VpnServer server)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"log\": { \"loglevel\": \"warning\" },");

        // Minimal DNS — just like v2rayN
        sb.AppendLine("  \"dns\": {");
        sb.AppendLine("    \"servers\": [\"1.1.1.1\", \"8.8.8.8\"]");
        sb.AppendLine("  },");

        sb.AppendLine("  \"inbounds\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"port\": 10808,");
        sb.AppendLine("      \"protocol\": \"socks\",");
        sb.AppendLine("      \"settings\": { \"udp\": true },");
        sb.AppendLine("      \"tag\": \"socks-in\"");
        sb.AppendLine("    },");
        sb.AppendLine("    {");
        sb.AppendLine("      \"port\": 10809,");
        sb.AppendLine("      \"protocol\": \"http\",");
        sb.AppendLine("      \"tag\": \"http-in\"");
        sb.AppendLine("    }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"outbounds\": [");

        // main proxy outbound
        sb.AppendLine("    {");
        sb.AppendLine("      \"protocol\": \"vless\",");
        sb.AppendLine("      \"settings\": {");
        sb.AppendLine("        \"vnext\": [{");
        sb.AppendLine($"          \"address\": \"{Esc(server.Address)}\",");
        sb.AppendLine($"          \"port\": {server.Port},");
        sb.AppendLine("          \"users\": [{");
        sb.AppendLine($"            \"id\": \"{Esc(server.Uuid)}\",");
        sb.AppendLine("            \"encryption\": \"none\"");
        sb.AppendLine("          }]");
        sb.AppendLine("        }]");
        sb.AppendLine("      },");

        sb.AppendLine("      \"streamSettings\": {");
        sb.AppendLine($"        \"network\": \"{server.Network}\",");
        sb.AppendLine($"        \"security\": \"{server.Security}\"");

        if (server.Security == "reality")
        {
            sb.AppendLine("        ,\"realitySettings\": {");
            sb.AppendLine($"          \"serverName\": \"{Esc(server.Sni)}\",");
            sb.AppendLine($"          \"fingerprint\": \"{Esc(server.Fingerprint)}\",");
            sb.AppendLine($"          \"publicKey\": \"{Esc(server.PublicKey)}\",");
            sb.AppendLine($"          \"shortId\": \"{Esc(server.ShortId)}\"");
            sb.AppendLine("        }");
        }

        if (server.Network == "tcp" || server.Network == "grpc")
        {
            // nothing extra needed
        }
        else if (server.Network == "xhttp")
        {
            sb.AppendLine("        ,\"xhttpSettings\": {");
            sb.AppendLine("          \"mode\": \"auto\"");
            if (!string.IsNullOrEmpty(server.Spx))
                sb.AppendLine($"          ,\"path\": \"{Esc(server.Spx)}\"");
            string host = !string.IsNullOrEmpty(server.Host) ? server.Host : server.Sni;
            if (!string.IsNullOrEmpty(host))
                sb.AppendLine($"          ,\"host\": \"{Esc(host)}\"");
            sb.AppendLine("        }");
        }
        else if (server.Network == "ws")
        {
            sb.AppendLine("        ,\"wsSettings\": {");
            if (!string.IsNullOrEmpty(server.Spx))
                sb.AppendLine($"          \"path\": \"{Esc(server.Spx)}\"");
            string host = !string.IsNullOrEmpty(server.Host) ? server.Host : server.Sni;
            if (!string.IsNullOrEmpty(host))
                sb.AppendLine($"          ,\"headers\": {{ \"Host\": \"{Esc(host)}\" }}");
            sb.AppendLine("        }");
        }

        sb.AppendLine("      },");
        sb.AppendLine("      \"tag\": \"proxy\"");
        sb.AppendLine("    },");

        sb.AppendLine("    { \"protocol\": \"freedom\", \"tag\": \"direct\" }");

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---- start / stop ----

    public void Start(VpnServer server)
    {
        if (IsConnected) Stop();
        if (!IsXrayInstalled) throw new FileNotFoundException("xray.exe не найден. Скачайте xray-core.");

        string configJson = GenerateConfig(server);
        File.WriteAllText(XrayConfigPath, configJson, new UTF8Encoding(false));
        LogLine?.Invoke($"[vpn] Config:\n{configJson}");

        var psi = new ProcessStartInfo
        {
            FileName = XrayExe,
            Arguments = $"run -c \"{XrayConfigPath}\"",
            WorkingDirectory = XrayDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) LogLine?.Invoke($"[xray] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogLine?.Invoke($"[xray] {e.Data}"); };
        proc.Exited += (_, _) =>
        {
            int exitCode = 0;
            try { exitCode = proc.ExitCode; } catch { }
            LogLine?.Invoke($"[xray] Процесс завершён (код: {exitCode}).");
            ClearSystemProxy();
            RestoreDns(_savedDns);
            _savedDns = null;
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _proc = proc;

        // Give xray a moment to bind port 53 before we switch DNS to it
        Thread.Sleep(500);
        _savedDns = SaveAndSetDns();
        SetSystemProxy("127.0.0.1", 10809, 10808, server.Address);
    }

    public void Stop()
    {
        ClearSystemProxy();
        RestoreDns(_savedDns);
        _savedDns = null;
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
    }

    // ---- system proxy (Windows) ----
    //
    // HAPP-style: simple HTTP proxy via registry + WinHTTP + InternetSetOption broadcast.
    // No PAC file (blocked by browsers), no SOCKS in ProxyServer (not supported).

    private static void SetSystemProxy(string host, int httpPort, int socksPort, string? vpnServerIp = null)
    {
        try
        {
            string proxyOverride = BuildProxyOverride(vpnServerIp);

            // 1) Registry — Internet Settings (per-user)
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
            {
                if (key is not null)
                {
                    key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", $"http={host}:{httpPort}", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("ProxyOverride", proxyOverride, Microsoft.Win32.RegistryValueKind.String);
                }
            }

            // 2) WinHTTP (for apps that use WinHTTP instead of WinINet)
            RunCmd("netsh", $"winhttp set proxy proxy-server=\"http={host}:{httpPort}\" bypass-list=\"{proxyOverride.Replace(";", " ")}\"");

            // 3) Notify all running apps (INTERNET_OPTION_SETTINGS_CHANGED + INTERNET_OPTION_REFRESH)
            InternetSetOption(0, 39, IntPtr.Zero, 0);
            InternetSetOption(0, 37, IntPtr.Zero, 0);
        }
        catch { }
    }

    private static void ClearSystemProxy()
    {
        try
        {
            // 1) Registry
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
            {
                if (key is not null)
                {
                    key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }

            // 2) WinHTTP
            RunCmd("netsh", "winhttp reset proxy");

            // 3) Notify all running apps
            InternetSetOption(0, 39, IntPtr.Zero, 0);
            InternetSetOption(0, 37, IntPtr.Zero, 0);
        }
        catch { }
    }

    // ---- system DNS (route DNS through VPN tunnel) ----

    private string? SaveAndSetDns()
    {
        try
        {
            var adapter = GetActiveNetworkInterface();
            if (adapter is null)
            {
                LogLine?.Invoke("[vpn] Не удалось найти активный сетевой адаптер для DNS.");
                return null;
            }

            string adapterId = adapter.Id;
            string adapterName = adapter.Name;

            // Save current DNS
            string? savedDns = GetCurrentDns(adapterId);

            // Set DNS to 1.1.1.1 (Cloudflare, fast and reliable)
            RunCmd("netsh", $"interface ip set dns name=\"{adapterName}\" static 1.1.1.1 primary");
            LogLine?.Invoke($"[vpn] DNS → 1.1.1.1 (адаптер: {adapterName})");
            return savedDns;
        }
        catch (Exception ex)
        {
            LogLine?.Invoke($"[vpn] DNS ошибка: {ex.Message}");
            return null;
        }
    }

    private void RestoreDns(string? savedDns)
    {
        try
        {
            var adapter = GetActiveNetworkInterface();
            if (adapter is null) return;

            if (string.IsNullOrEmpty(savedDns))
            {
                RunCmd("netsh", $"interface ip set dns name=\"{adapter.Name}\" dhcp");
                LogLine?.Invoke($"[vpn] DNS → DHCP (адаптер: {adapter.Name})");
            }
            else
            {
                RunCmd("netsh", $"interface ip set dns name=\"{adapter.Name}\" static {savedDns} primary");
                LogLine?.Invoke($"[vpn] DNS → {savedDns} (адаптер: {adapter.Name})");
            }
        }
        catch (Exception ex)
        {
            LogLine?.Invoke($"[vpn] DNS восстановление ошибки: {ex.Message}");
        }
    }

    private static NetworkInterface? GetActiveNetworkInterface()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        NetworkInterface? best = null;

        foreach (var iface in interfaces)
        {
            if (iface.OperationalStatus != OperationalStatus.Up)
                continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var props = iface.GetIPProperties();
            if (props.GatewayAddresses.Count == 0)
                continue;

            best = iface;
            break;
        }

        return best;
    }

    private static string? GetCurrentDns(string adapterId)
    {
        return null;
    }

    private static string BuildProxyOverride(string? vpnServerIp = null)
    {
        string list = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;" +
                      "172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;" +
                      "172.30.*;172.31.*;192.168.*;<local>";
        if (!string.IsNullOrEmpty(vpnServerIp))
            list += $";{vpnServerIp}";
        return list;
    }

    private static void RunCmd(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(int dwOption, int dwBuffer, IntPtr lpBuffer, int dwBufferLength);

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
