using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Discord shop/Nitro geo bridge — part of the bypass strategy (not the VPN tab).
/// Starts local xray SOCKS only (no system proxy/DNS), then Discord Desktop uses it.
/// </summary>
public sealed class DiscordShopBridgeService
{
    private readonly VpnService _vpn;

    public DiscordShopBridgeService(VpnService vpn) => _vpn = vpn;

    public event Action<string>? LogLine;

    public bool IsActive => _vpn.IsShopBridgeActive;

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_vpn.IsFullVpnActive)
        {
            if (VpnService.IsLocalSocksListening())
            {
                Emit("Discord магазин: мост стратегии (SOCKS через ваш VPN)");
                return true;
            }
            Emit("Discord магазин: VPN вкладки активен, но SOCKS недоступен");
            return false;
        }

        if (_vpn.IsShopBridgeActive && VpnService.IsLocalSocksListening())
            return true;

        if (!_vpn.IsXrayInstalled)
        {
            Emit("Discord магазин: подготовка моста (xray, один раз)…");
            try { await _vpn.DownloadXrayAsync(ct: ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Emit($"Discord магазин: xray — {ex.Message}");
                return false;
            }
            if (!_vpn.IsXrayInstalled)
            {
                Emit("Discord магазин: xray не установлен");
                return false;
            }
        }

        var servers = _vpn.GetDefaultServers();
        if (servers.Count == 0)
        {
            Emit("Discord магазин: нет серверов моста");
            return false;
        }

        Emit("Discord магазин: мост стратегии (зарубежный IP только для Discord)…");

        foreach (var srv in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _vpn.StartShopBridge(srv);
            }
            catch (Exception ex)
            {
                Emit($"Discord магазин: {srv.Name} — {ex.Message}");
                continue;
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);

            if (!_vpn.IsShopBridgeActive)
            {
                Emit($"Discord магазин: {srv.Name} — xray не запустился");
                continue;
            }

            if (await _vpn.TestSocksAsync(ct).ConfigureAwait(false))
            {
                Emit($"Discord магазин: мост активен ({srv.Name}) — откройте Nitro/Магазин");
                return true;
            }

            Emit($"Discord магазин: {srv.Name} — SOCKS не отвечает, пробую другой…");
            _vpn.StopShopBridge();
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        Emit("Discord магазин: мост не поднялся — чат/voice работают через DPI");
        return false;
    }

    public void Stop() => _vpn.StopShopBridge();

    private void Emit(string line) => LogLine?.Invoke(line);
}
