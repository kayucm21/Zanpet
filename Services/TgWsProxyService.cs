using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ZapretUI.Services;

/// <summary>
/// Silent MTProto→WebSocket bridge for Telegram Desktop. Web Telegram works because traffic
/// goes through web.telegram.org; desktop uses raw DC IPs which ISPs often block. This tunnel
/// routes desktop through the same web path — proxy is applied automatically (no browser).
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
    private static readonly int[] ApplyDelaysMs = [0, 2000];

    private Process? _proc;
    private CancellationTokenSource? _clickerCts;
    private bool _proxyAppliedThisSession;
    private bool _deeplinkSent;
    private string? _loggedTelegramPath;

    private const string DefaultSecret = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a0";

    public string Secret { get; set; } = DefaultSecret;

    public bool IsRunning => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    private static string ExeDir => Path.Combine(AppPaths.EngineDir, "tgws");
    private static string ExePath => Path.Combine(ExeDir, "TgWsProxy.exe");
    private static string PortableDataDir => Path.Combine(ExeDir, "TgWsProxy_data");
    private static string ConfigPath => Path.Combine(PortableDataDir, "config.json");

    private string ProxyDeeplink =>
        $"tg://proxy?server={DefaultHost}&port={DefaultPort}&secret=dd{Secret}";

    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return;

        Directory.CreateDirectory(ExeDir);

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
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Не удалось скачать компонент Telegram Desktop: {last?.Message ?? "неизвестная ошибка"}");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            if (!_proxyAppliedThisSession)
                await ApplyProxyOnceAsync(ct).ConfigureAwait(false);
            return;
        }

        _proxyAppliedThisSession = false;
        _deeplinkSent = false;
        await EnsureInstalledAsync(ct).ConfigureAwait(false);
        Secret = NormalizeSecret(Secret);
        KillStaleProcesses();
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
            ?? throw new InvalidOperationException("Не удалось запустить компонент Telegram Desktop.");

        if (!await WaitForListenAsync(DefaultHost, DefaultPort, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false))
        {
            int code = _proc is { HasExited: true } p ? p.ExitCode : -1;
            string logHint = ReadProxyLogTail();
            throw new InvalidOperationException(
                code >= 0
                    ? $"Мост Telegram завершился (код {code}).{logHint}"
                    : $"Мост Telegram не запустился.{logHint}");
        }

        Emit($"Telegram Desktop: мост {DefaultHost}:{DefaultPort}");
        await ApplyProxyOnceAsync(ct).ConfigureAwait(false);
    }

    private async Task ApplyProxyOnceAsync(CancellationToken ct)
    {
        if (_proxyAppliedThisSession) return;

        StartDialogAutoClicker(ct);
        foreach (int delay in ApplyDelaysMs)
        {
            if (delay > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            if (TryApplyProxyToDesktop())
                break;
        }

        _proxyAppliedThisSession = true;
        StopDialogAutoClicker();
    }

    private void StartDialogAutoClicker(CancellationToken outerCt)
    {
        StopDialogAutoClicker();
        _clickerCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var token = _clickerCts.Token;
        _ = Task.Run(() => TelegramProxyUiHelper.RunFor(TimeSpan.FromSeconds(12), token), token);
    }

    /// <returns>True when proxy deeplink was sent or Telegram is already running (clicker handles dialog).</returns>
    private bool TryApplyProxyToDesktop()
    {
        var located = TelegramLocator.Locate();

        if (located.ExePath is not null)
        {
            if (_loggedTelegramPath != located.ExePath)
            {
                _loggedTelegramPath = located.ExePath;
                Emit($"Telegram Desktop: найден — {located.ExePath}");
            }
        }
        else if (located.ProcessRunning)
        {
            Emit($"Telegram Desktop: процесс запущен ({located.ProcessCount} шт.), автоподключение прокси…");
            TelegramProxyUiHelper.TryClickOnce();
            return true;
        }
        else
        {
            Emit("Telegram Desktop: не найден — запустите Telegram вручную");
            return false;
        }

        bool wasRunning = located.ProcessRunning;
        if (_deeplinkSent)
        {
            TelegramProxyUiHelper.TryClickOnce();
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = located.ExePath,
                Arguments = $"-- \"{ProxyDeeplink}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            _deeplinkSent = true;

            if (!wasRunning)
                Emit("Telegram Desktop: запуск с автопрокси");
            else
                Emit("Telegram Desktop: прокси применён (один раз за сессию)");

            return true;
        }
        catch (Exception ex)
        {
            Emit($"Telegram Desktop: {ex.Message}");
            return false;
        }
    }

    private void StopDialogAutoClicker()
    {
        try { _clickerCts?.Cancel(); } catch { }
        _clickerCts?.Dispose();
        _clickerCts = null;
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
        // Skip first-run / IPv6 GUI dialogs that block headless start.
        File.WriteAllText(Path.Combine(PortableDataDir, ".first_run_done_mtproto"), "");
        File.WriteAllText(Path.Combine(PortableDataDir, ".ipv6_warned"), "");
    }

    private static void KillStaleProcesses()
    {
        foreach (var p in Process.GetProcessesByName("TgWsProxy"))
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
            finally { p.Dispose(); }
        }
    }

    private static string NormalizeSecret(string? secret)
    {
        string s = (secret ?? "").Trim().ToLowerInvariant();
        if (s.Length == 32 && s.All(static c => Uri.IsHexDigit(c)))
            return s;
        return DefaultSecret;
    }

    private static string ReadProxyLogTail()
    {
        try
        {
            string logPath = Path.Combine(PortableDataDir, "proxy.log");
            if (!File.Exists(logPath)) return "";
            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0) return "";
            string last = lines[^1];
            return string.IsNullOrWhiteSpace(last) ? "" : $" ({last})";
        }
        catch { return ""; }
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

    public void Stop()
    {
        StopDialogAutoClicker();
        _proxyAppliedThisSession = false;
        _deeplinkSent = false;
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

    public void Dispose() => Stop();

    private void Emit(string line) => LogLine?.Invoke(line);
}
