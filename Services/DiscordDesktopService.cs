using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZapretUI.Services;

/// <summary>
/// Discord Desktop auto-connect. DPI bypass via hosts + winws; shop/Nitro via optional VPN SOCKS proxy
/// (Chromium --proxy-server) when <see cref="UseShopVpnProxy"/> is set.
/// </summary>
public sealed class DiscordDesktopService
{
    private static readonly int[] ApplyDelaysMs = [0, 800];
    private static readonly string[] ProcessNames = ["Discord", "DiscordCanary", "DiscordPTB"];

    private bool _appliedThisSession;
    private bool _relaunchedWithProxyThisSession;
    private string? _loggedPath;

    /// <summary>Launch/relaunch Discord through local xray SOCKS (127.0.0.1:10808) for shop geo bypass.</summary>
    public bool UseShopVpnProxy { get; set; }

    public event Action<string>? LogLine;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_appliedThisSession) return Task.CompletedTask;
        return ApplyOnceAsync(ct);
    }

    private async Task ApplyOnceAsync(CancellationToken ct)
    {
        Emit(UseShopVpnProxy && VpnService.IsLocalSocksListening()
            ? "Discord Desktop: мост стратегии + магазин (hosts + winws + SOCKS)"
            : "Discord Desktop: мост стратегии (gateway, магазин DPI — hosts + winws)");

        foreach (int delay in ApplyDelaysMs)
        {
            if (delay > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            if (await TryConnectDesktopAsync(ct).ConfigureAwait(false))
            {
                _appliedThisSession = true;
                return;
            }
        }

        _appliedThisSession = true;
    }

    private async Task<bool> TryConnectDesktopAsync(CancellationToken ct)
    {
        var located = DiscordLocator.Locate();

        if (!string.IsNullOrEmpty(located.AppDataDir))
            TryPatchDiscordSettings(located.AppDataDir);

        if (located.UpdateExe is not null && File.Exists(located.UpdateExe))
        {
            if (_loggedPath != located.UpdateExe)
            {
                _loggedPath = located.UpdateExe;
                Emit($"Discord Desktop: найден — {located.UpdateExe}");
            }
        }
        else if (located.DiscordExe is not null && File.Exists(located.DiscordExe))
        {
            if (_loggedPath != located.DiscordExe)
            {
                _loggedPath = located.DiscordExe;
                string tag = located.Flavor switch
                {
                    DiscordLocator.Flavor.Canary => "Canary",
                    DiscordLocator.Flavor.Ptb => "PTB",
                    _ => "Stable",
                };
                Emit($"Discord Desktop: найден ({tag}) — {located.DiscordExe}");
            }
        }
        else if (located.ProcessRunning)
        {
            if (ShouldRelaunchWithShopProxy())
            {
                await RelaunchWithShopProxyAsync(located, ct).ConfigureAwait(false);
                return true;
            }
            Emit($"Discord Desktop: процесс запущен ({located.ProcessCount} шт.), обход активен");
            return true;
        }
        else
        {
            Emit("Discord Desktop: не найден — установите Discord или запустите вручную");
            return false;
        }

        if (located.ProcessRunning && ShouldRelaunchWithShopProxy())
        {
            await RelaunchWithShopProxyAsync(located, ct).ConfigureAwait(false);
            return true;
        }

        if (located.ProcessRunning)
        {
            Emit("Discord Desktop: уже запущен, обход подхвачен");
            return true;
        }

        return LaunchDiscord(located, withShopProxy: UseShopVpnProxy && VpnService.IsLocalSocksListening());
    }

    private bool ShouldRelaunchWithShopProxy() =>
        UseShopVpnProxy && VpnService.IsLocalSocksListening() && !_relaunchedWithProxyThisSession;

    private async Task RelaunchWithShopProxyAsync(DiscordLocator.LocateResult located, CancellationToken ct)
    {
        Emit("Discord Desktop: перезапуск через VPN для магазина/Nitro…");
        KillDiscordProcesses();
        await Task.Delay(1200, ct).ConfigureAwait(false);
        if (LaunchDiscord(located, withShopProxy: true))
            _relaunchedWithProxyThisSession = true;
    }

    private static void KillDiscordProcesses()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { /* best-effort */ }
                finally { p.Dispose(); }
            }
        }
    }

    private bool LaunchDiscord(DiscordLocator.LocateResult located, bool withShopProxy)
    {
        try
        {
            string? proxyArg = withShopProxy ? VpnService.ShopSocksProxyArg : null;

            if (located.UpdateExe is not null && File.Exists(located.UpdateExe))
            {
                string args = proxyArg is null
                    ? "--processStart Discord.exe"
                    : $"--processStart Discord.exe --process-start-args \"{proxyArg}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = located.UpdateExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            else if (located.DiscordExe is not null && File.Exists(located.DiscordExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = located.DiscordExe,
                    Arguments = proxyArg ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                });
            }
            else return false;

            Emit(withShopProxy
                ? "Discord Desktop: запуск с VPN-прокси (магазин/Nitro)"
                : "Discord Desktop: запуск с автоподключением");
            return true;
        }
        catch (Exception ex)
        {
            Emit($"Discord Desktop: {ex.Message}");
            return false;
        }
    }

    /// <summary>Skip Discord updater network checks (helps when gateway is reached via hosts+winws).</summary>
    private static void TryPatchDiscordSettings(string appDataDir)
    {
        try
        {
            string path = Path.Combine(appDataDir, "settings.json");
            JsonObject root;
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            }
            else
            {
                Directory.CreateDirectory(appDataDir);
                root = new JsonObject();
            }

            bool changed = false;
            if (root["SKIP_HOST_UPDATE"]?.GetValue<bool>() != true)
            {
                root["SKIP_HOST_UPDATE"] = true;
                changed = true;
            }
            if (root["SKIP_MODULE_UPDATE"]?.GetValue<bool>() != true)
            {
                root["SKIP_MODULE_UPDATE"] = true;
                changed = true;
            }

            if (!changed) return;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    public void Reset()
    {
        _appliedThisSession = false;
        _relaunchedWithProxyThisSession = false;
    }

    private void Emit(string line) => LogLine?.Invoke(line);
}
