using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ZapretUI.Services;

/// <summary>
/// Local MTProto→WebSocket bridge (Flowseal tg-ws-proxy) for Telegram Desktop.
/// Raw MTProto to DC IPs is often blocked at ISP level; winws cannot fix that — this proxy
/// tunnels desktop traffic through TLS to web.telegram.org instead.
/// </summary>
public sealed class TgWsProxyService : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 1443;

    private const string DownloadUrl =
        "https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.8.1/TgWsProxy_windows.exe";

    private const string DownloadMirrorUrl =
        "https://sourceforge.net/projects/tg-ws-proxy.mirror/files/v1.8.1/TgWsProxy_windows.exe/download";

    private static readonly HttpClient Http = HttpFactory.General;

    private Process? _proc;

    public string Secret { get; set; } = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a";

    public bool IsRunning => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    private static string ExeDir => Path.Combine(AppPaths.EngineDir, "tgws");
    private static string ExePath => Path.Combine(ExeDir, "TgWsProxy.exe");
    private static string PortableDataDir => Path.Combine(ExeDir, "TgWsProxy_data");
    private static string ConfigPath => Path.Combine(PortableDataDir, "config.json");

    /// <summary>tg:// deeplink to auto-enable the proxy in Telegram Desktop (dd + secret).</summary>
    public string ProxyDeeplink =>
        $"tg://proxy?server={DefaultHost}&port={DefaultPort}&secret=dd{Secret}";

    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return;

        Directory.CreateDirectory(ExeDir);
        Emit("Загрузка Telegram WS Proxy…");

        Exception? last = null;
        foreach (var url in new[] { DownloadUrl, DownloadMirrorUrl })
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(ExePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                Emit("Telegram WS Proxy установлен.");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Не удалось скачать TgWsProxy: {last?.Message ?? "неизвестная ошибка"}");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        await EnsureInstalledAsync(ct).ConfigureAwait(false);
        WritePortableConfig();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "--portable",
            WorkingDirectory = ExeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить TgWsProxy.");

        if (!await WaitForListenAsync(DefaultHost, DefaultPort, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
        {
            int code = _proc.HasExited ? _proc.ExitCode : -1;
            throw new InvalidOperationException(
                code >= 0
                    ? $"TgWsProxy завершился (код {code}). Порт {DefaultPort} занят?"
                    : $"TgWsProxy не слушает {DefaultHost}:{DefaultPort} — проверьте, не запущен ли другой экземпляр.");
        }

        Emit($"Telegram Desktop: MTProto прокси {DefaultHost}:{DefaultPort}");
        Emit($"Secret (ручной ввод): dd{Secret}");
        Emit("Открываю настройку прокси в Telegram…");
        TryOpenTelegramProxy();
    }

    private void WritePortableConfig()
    {
        Directory.CreateDirectory(PortableDataDir);
        var cfg = new Dictionary<string, object?>
        {
            ["port"] = DefaultPort,
            ["host"] = DefaultHost,
            ["secret"] = Secret,
            ["dc_ip"] = new[] { "2:149.154.167.220", "4:149.154.167.220" },
            ["verbose"] = false,
            ["check_updates"] = false,
            ["log_max_mb"] = 5,
            ["buf_kb"] = 256,
            ["pool_size"] = 4,
            ["cfproxy"] = true,
            ["cfproxy_user_domain"] = Array.Empty<string>(),
            ["cfproxy_worker_domain"] = Array.Empty<string>(),
            ["force_test_dc"] = false,
            ["ws_keepalive_interval"] = 30,
            ["language"] = "ru",
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<bool> WaitForListenAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        return false;
    }

    private void TryOpenTelegramProxy()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProxyDeeplink) { UseShellExecute = true });
            Emit("Если Telegram не открылся — вставьте ссылку из лога в браузер или настройте прокси вручную.");
        }
        catch (Exception ex)
        {
            Emit($"Не удалось открыть tg:// ссылку: {ex.Message}");
            Emit($"Скопируйте в браузер: {ProxyDeeplink}");
        }
    }

    public void Stop()
    {
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* best-effort */ }
        finally
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }

    private void Emit(string line) => LogLine?.Invoke(line);

    public void Dispose() => Stop();
}
