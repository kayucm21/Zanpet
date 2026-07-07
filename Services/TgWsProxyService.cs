using System.Diagnostics;
using System.IO;
using System.Net.Http;

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

    private static readonly HttpClient Http = HttpFactory.General;

    private Process? _proc;

    public string Secret { get; set; } = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a";

    public bool IsRunning => _proc is { HasExited: false };

    public event Action<string>? LogLine;

    private static string ExePath => Path.Combine(AppPaths.EngineDir, "tgws", "TgWsProxy.exe");

    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
        Emit("Загрузка Telegram WS Proxy…");

        using var resp = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(ExePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        Emit("Telegram WS Proxy установлен.");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        await EnsureInstalledAsync(ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"--port {DefaultPort} --host {DefaultHost} --secret {Secret}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            _proc = Process.Start(psi);
            if (_proc is null) throw new InvalidOperationException("Не удалось запустить TgWsProxy.");
            await Task.Delay(800, ct).ConfigureAwait(false);
            if (_proc.HasExited)
                throw new InvalidOperationException($"TgWsProxy завершился (код {_proc.ExitCode}).");
            Emit($"Telegram Desktop: MTProto прокси {DefaultHost}:{DefaultPort} secret={Secret}");
            Emit("В Telegram: Настройки → Данные и память → Прокси → MTProto → укажите адрес выше.");
        }
        catch
        {
            // Tray build may ignore CLI args — try bare launch (defaults to 127.0.0.1:1443).
            try
            {
                psi.Arguments = "";
                _proc = Process.Start(psi);
                if (_proc is not null && !_proc.HasExited)
                {
                    Emit($"Telegram Desktop: MTProto прокси {DefaultHost}:{DefaultPort} (секрет — в окне TgWsProxy в трее).");
                    return;
                }
            }
            catch { /* fall through */ }
            throw;
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
