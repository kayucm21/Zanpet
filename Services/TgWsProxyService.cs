using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ZapretUI.Services;

/// <summary>
/// Local MTProto→WebSocket bridge for Telegram Desktop. Applies proxy automatically
/// (including the native confirm dialog) — no manual settings.
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
    private static readonly int[] ApplyDelaysMs = [0, 1500, 4000, 9000, 18000];

    private Process? _proc;
    private CancellationTokenSource? _watchCts;
    private CancellationTokenSource? _clickerCts;
    private DateTime _lastApplyUtc;

    public string Secret { get; set; } = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a";

    public bool IsRunning => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    private static string ExeDir => Path.Combine(AppPaths.EngineDir, "tgws");
    private static string ExePath => Path.Combine(ExeDir, "TgWsProxy.exe");
    private static string PortableDataDir => Path.Combine(ExeDir, "TgWsProxy_data");
    private static string ConfigPath => Path.Combine(PortableDataDir, "config.json");

    private string ProxyDeeplink =>
        $"tg://proxy?server={DefaultHost}&port={DefaultPort}&secret=dd{Secret}";

    private string ProxyHttpsLink =>
        $"https://t.me/proxy?server={DefaultHost}&port={DefaultPort}&secret=dd{Secret}";

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
            await ApplyProxyWithRetriesAsync(ct).ConfigureAwait(false);
            return;
        }

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
            ?? throw new InvalidOperationException("Не удалось запустить компонент Telegram Desktop.");

        if (!await WaitForListenAsync(DefaultHost, DefaultPort, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            int code = _proc is { HasExited: true } p ? p.ExitCode : -1;
            throw new InvalidOperationException(
                code >= 0
                    ? $"Компонент Telegram Desktop завершился (код {code})."
                    : "Компонент Telegram Desktop не запустился.");
        }

        await ApplyProxyWithRetriesAsync(ct).ConfigureAwait(false);
        StartDesktopWatch(ct);
    }

    private async Task ApplyProxyWithRetriesAsync(CancellationToken ct)
    {
        StartDialogAutoClicker(ct);
        foreach (int delay in ApplyDelaysMs)
        {
            if (delay > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            ApplyProxyToDesktop();
        }
    }

    private void StartDialogAutoClicker(CancellationToken outerCt)
    {
        StopDialogAutoClicker();
        _clickerCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var token = _clickerCts.Token;
        _ = Task.Run(() => TelegramProxyUiHelper.RunFor(TimeSpan.FromSeconds(60), token), token);
    }

    private void StopDialogAutoClicker()
    {
        try { _clickerCts?.Cancel(); } catch { }
        _clickerCts?.Dispose();
        _clickerCts = null;
    }

    private void StartDesktopWatch(CancellationToken outerCt)
    {
        StopDesktopWatch();
        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var token = _watchCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (IsRunning && Process.GetProcessesByName("Telegram").Length > 0)
                    {
                        // Re-apply every 20s while Telegram is open (welcome screen keeps retrying).
                        if (DateTime.UtcNow - _lastApplyUtc > TimeSpan.FromSeconds(20))
                            ApplyProxyToDesktop();
                    }
                    await Task.Delay(3000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* best-effort */ }
            }
        }, token);
    }

    private void StopDesktopWatch()
    {
        try { _watchCts?.Cancel(); } catch { }
        _watchCts?.Dispose();
        _watchCts = null;
    }

    private void ApplyProxyToDesktop()
    {
        _lastApplyUtc = DateTime.UtcNow;
        string? telegramExe = FindTelegramExe();

        try
        {
            if (telegramExe is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = telegramExe,
                    Arguments = $"-- \"{ProxyDeeplink}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
        }
        catch { /* fall through */ }

        try
        {
            Process.Start(new ProcessStartInfo(ProxyHttpsLink) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    private static string? FindTelegramExe()
    {
        foreach (var p in Process.GetProcessesByName("Telegram"))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch { /* elevated / access */ }
            finally
            {
                p.Dispose();
            }
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Telegram Desktop", "Telegram.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Telegram Desktop", "Telegram.exe"),
            @"C:\Program Files\Telegram Desktop\Telegram.exe",
            @"C:\Program Files (x86)\Telegram Desktop\Telegram.exe",
        ];

        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        return null;
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

    public void Stop()
    {
        StopDesktopWatch();
        StopDialogAutoClicker();
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
}
