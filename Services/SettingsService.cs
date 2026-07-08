using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretUI.Services;

public sealed class AppSettings
{
    /// <summary>Schema version for future migrations. Bump when adding/removing fields.</summary>
    public int SettingsVersion { get; set; } = 1;

    public string? ActivePresetName { get; set; }
    public string? ActiveHostlist { get; set; }
    public bool AutoUpdateEngine { get; set; } = true;
    public bool Autostart { get; set; }
    public bool AutostartEngine { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    /// <summary>Simple (one-click) vs Advanced (full tabs) interface. Simple by default.</summary>
    public bool SimpleMode { get; set; } = true;

    /// <summary>Background watchdog: silently re-pick a strategy if the bypass stops working.</summary>
    public bool AutoHeal { get; set; }

    /// <summary>Game filter (Flowseal-style): when true, the bypass capture is widened to all high
    /// ports (>1023) so throttled games get desynced too. When false (default), capture stays narrow
    /// (80,443 + Discord voice) so game traffic is left untouched and games run natively.</summary>
    public bool GameFilter { get; set; }

    /// <summary>Bypass EVERY site (catch-all) vs allow-list. When false (default), only the explicit
    /// lists (YouTube/Discord/Telegram) + your custom targets/hostlists are desynced — like Flowseal,
    /// so games/apps not in any list never break. When true, all other TLS/QUIC is desynced too
    /// (kept safe by the exclude list); convenient but may break a game/app that isn't excluded.</summary>
    public bool BypassAllSites { get; set; }

    /// <summary>Theme mode: "dark" (default) or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Auto-start Flowseal tg-ws-proxy for Telegram Desktop (MTProto→WebSocket).</summary>
    public bool TelegramWsProxy { get; set; } = true;

    /// <summary>Write Windows hosts entries for web.telegram.org (Flowseal community fix).</summary>
    public bool TelegramWebHosts { get; set; } = true;

    /// <summary>Fixed MTProto secret for tg-ws-proxy (32 hex chars).</summary>
    public string TelegramWsProxySecret { get; set; } = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a";

    /// <summary>Last app version the user saw the changelog for. Empty = never shown.</summary>
    public string LastSeenVersion { get; set; } = "";

    /// <summary>Normalize values after deserialization to guard against corrupt/invalid JSON.</summary>
    public AppSettings Normalize()
    {
        // Clamp version to known range for forward-compatible migrations.
        if (SettingsVersion < 1) SettingsVersion = 1;
        return this;
    }
}

/// <summary>Loads/saves <see cref="AppSettings"/> as settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService() => Load();

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile));
                if (loaded is not null)
                {
                    Settings = loaded.Normalize();
                    return;
                }
            }
        }
        catch
        {
            try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", overwrite: true); } catch { }
        }
        Settings = new AppSettings();
    }
}
