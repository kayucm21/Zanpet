using System.Security.Authentication;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Lightweight background watchdog for the auto-orchestrator. While the engine
/// runs it periodically TLS-probes a couple of key endpoints; after a sustained
/// failure it raises <see cref="ConnectivityLost"/> so the app can silently
/// re-pick a strategy and self-heal. Cheap and quiet — one probe per target per tick.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private static readonly string[] Watch = { "gateway.discord.gg", "www.youtube.com" };
    private const int TickSeconds = 45;
    private const int FailsToHeal = 2;     // ~90s of failure before acting
    private const int BackoffSeconds = 120; // pause after a heal request

    /// <summary>Raised (once per episode) when watched endpoints stay unreachable.</summary>
    public event Action? ConnectivityLost;
    /// <summary>Raised every tick with the current health (for a subtle indicator).</summary>
    public event Action<bool>? Tick;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = LoopAsync(token);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int fails = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct).ConfigureAwait(false);
                bool ok = await HealthyAsync(ct).ConfigureAwait(false);
                Tick?.Invoke(ok);
                if (ok) { fails = 0; continue; }

                if (++fails >= FailsToHeal)
                {
                    fails = 0;
                    ConnectivityLost?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (ObjectDisposedException) { /* CTS torn down during stop */ }
    }

    /// <summary>Healthy when every watched endpoint completes a TLS handshake.</summary>
    private static async Task<bool> HealthyAsync(CancellationToken ct)
    {
        foreach (var host in Watch)
        {
            if (await NetProbe.TlsAsync(host, SslProtocols.Tls12, ct).ConfigureAwait(false) != DiagStatus.Ok)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
