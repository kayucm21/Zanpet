using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Manages VPN via xray-core: subscription parsing, download, config generation,
/// start/stop of the xray process, and system proxy toggle.
/// </summary>
public sealed class VpnService : IDisposable
{
    private Process? _proc;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public bool IsConnected => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    // ---- paths ----

    private static string XrayDir => Path.Combine(AppPaths.EngineDir, "xray");
    private static string XrayExe => Path.Combine(XrayDir, "xray.exe");
    private static string XrayConfigPath => Path.Combine(XrayDir, "config.json");
    private static string SubCachePath => Path.Combine(AppPaths.Root, "vpn_subscription.txt");

    private const string XrayReleaseApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
    public const string VpnSubscriptionUrl = "https://tepaqq.mooo.com/s/V9UygbKuEvfjSy0KYPIgH3sLSQbXo6l-6_LCrTAjwrm208Cy/VPN/b64";

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
            // vless://uuid@address:port?params#name
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
            string topFolder = zip.Entries.First().FullName.Split('/')[0] + "/";
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length <= topFolder.Length) continue;
                string rel = entry.FullName[topFolder.Length..];
                if (rel.Length == 0 || rel.Contains('/')) continue;
                string dest = Path.Combine(XrayDir, rel);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        try { File.Delete(zipPath); } catch { }
    }

    // ---- xray config generation ----

    private static string GenerateConfig(VpnServer server)
    {
        var streamSettings = new Dictionary<string, object>
        {
            ["network"] = server.Network,
            ["security"] = server.Security,
        };

        if (server.Security == "reality")
        {
            var reality = new Dictionary<string, object>
            {
                ["serverName"] = server.Sni,
                ["fingerprint"] = server.Fingerprint,
                ["publicKey"] = server.PublicKey,
                ["shortId"] = server.ShortId,
            };
            if (server.Network == "xhttp" && !string.IsNullOrEmpty(server.Spx))
                reality["path"] = server.Spx;
            streamSettings["realitySettings"] = reality;
        }

        if (server.Network == "xhttp" && !string.IsNullOrEmpty(server.Spx))
        {
            streamSettings["xhttpSettings"] = new Dictionary<string, object>
            {
                ["path"] = server.Spx,
                ["mode"] = "auto",
            };
        }

        var config = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object> { ["loglevel"] = "warning" },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["port"] = 10808,
                    ["protocol"] = "socks",
                    ["settings"] = new Dictionary<string, object> { ["udp"] = true },
                    ["tag"] = "socks-in",
                },
                new Dictionary<string, object>
                {
                    ["port"] = 10809,
                    ["protocol"] = "http",
                    ["tag"] = "http-in",
                },
            },
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["protocol"] = "vless",
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["vnext"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["address"] = server.Address,
                                ["port"] = server.Port,
                                ["users"] = new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["id"] = server.Uuid,
                                        ["encryption"] = "none",
                                    },
                                },
                            },
                        },
                    },
                    ["streamSettings"] = streamSettings,
                    ["tag"] = "proxy",
                },
                new Dictionary<string, object>
                {
                    ["protocol"] = "freedom",
                    ["tag"] = "direct",
                },
            },
            ["routing"] = new Dictionary<string, object>
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["ip"] = new[] { "geoip:private" },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- start / stop ----

    public void Start(VpnServer server)
    {
        if (IsConnected) Stop();
        if (!IsXrayInstalled) throw new FileNotFoundException("xray.exe не найден. Скачайте xray-core.");

        File.WriteAllText(XrayConfigPath, GenerateConfig(server));

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
        proc.Exited += (_, _) => { LogLine?.Invoke("[xray] Процесс завершён."); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _proc = proc;

        SetSystemProxy("127.0.0.1", 10809);
    }

    public void Stop()
    {
        ClearSystemProxy();
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
    }

    // ---- system proxy (Windows) ----

    private static void SetSystemProxy(string host, int port)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key is null) return;
            key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"socks={host}:{port};http={host}:{port}");
            key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>");
            InternetSetOption(0, 39, IntPtr.Zero, 0);
            InternetSetOption(0, 37, IntPtr.Zero, 0);
        }
        catch { }
    }

    private static void ClearSystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key is null) return;
            key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
            InternetSetOption(0, 39, IntPtr.Zero, 0);
            InternetSetOption(0, 37, IntPtr.Zero, 0);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll")]
    private static extern bool InternetSetOption(int dwOption, int dwBuffer, IntPtr lpBuffer, int dwBufferLength);

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
