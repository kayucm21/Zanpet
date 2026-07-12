using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretUI.Services;

public sealed class AppSettings
{
    /// <summary>Schema version for future migrations. Bump when adding/removing fields.</summary>
    public int SettingsVersion { get; set; } = 17;

    public string? ActivePresetName { get; set; }
    public string? ActiveHostlist { get; set; }
    public bool AutoUpdateEngine { get; set; } = true;
    public bool Autostart { get; set; }
    public bool AutostartEngine { get; set; } = true;
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

    /// <summary>Auto-start tg-ws-proxy MTProto bridge with bypass (does not launch Telegram.exe).</summary>
    public bool TelegramWsProxy { get; set; } = true;

    /// <summary>Write Windows hosts entries for discord.com (Cloudflare edge pin).</summary>
    public bool DiscordWebHosts { get; set; } = true;

    /// <summary>Auto-find and launch Discord Desktop after bypass (hosts + winws). Manual only by default.</summary>
    public bool DiscordDesktopAutoLaunch { get; set; } = false;

    /// <summary>Geo bridge for Discord shop/Nitro — part of bypass strategy (SOCKS for Discord only).</summary>
    public bool DiscordShopBridge { get; set; } = true;

    /// <summary>Only when user manually connects full VPN tab.</summary>
    public bool DiscordShopVpnBridge { get; set; } = false;

    /// <summary>Deprecated — never auto-start full VPN with bypass.</summary>
    public bool DiscordShopAutoVpn { get; set; } = false;

    /// <summary>Write Windows hosts for Telegram web + desktop (DC pin, like Discord hosts).</summary>
    public bool TelegramWebHosts { get; set; } = true;

    /// <summary>Hosts pin for TikTok CDN (DPI bridge, no app auto-launch).</summary>
    public bool TikTokWebHosts { get; set; } = true;

    /// <summary>Hosts pin for Instagram/Meta CDN.</summary>
    public bool InstagramWebHosts { get; set; } = true;

    /// <summary>Hosts pin for WhatsApp web/API.</summary>
    public bool WhatsAppWebHosts { get; set; } = true;

    /// <summary>Use FTP server for app updates (primary source).</summary>
    public bool FtpUpdateEnabled { get; set; } = true;
    public string FtpUpdateHost { get; set; } = "";
    public int FtpUpdatePort { get; set; } = 21;
    public bool FtpUpdateUseSsl { get; set; }
    public string FtpUpdateUser { get; set; } = "";
    public string FtpUpdatePassword { get; set; } = "";
    public string FtpUpdatePath { get; set; } = "/updates";

    /// <summary>Fixed MTProto secret for tg-ws-proxy (exactly 32 hex chars).</summary>
    public string TelegramWsProxySecret { get; set; } = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a0";

    /// <summary>OpenCode HTTP server URL (opencode serve). Default local port 4096.</summary>
    public string OpenCodeUrl { get; set; } = "http://127.0.0.1:4096";

    /// <summary>Basic auth user for OpenCode server (if OPENCODE_SERVER_PASSWORD is set).</summary>
    public string OpenCodeUsername { get; set; } = "opencode";

    /// <summary>Basic auth password for OpenCode server.</summary>
    public string OpenCodePassword { get; set; } = "";

    /// <summary>OpenCode Zen / provider API key (sk-…). Sent to local opencode serve via PUT /auth.</summary>
    public string OpenCodeApiKey { get; set; } = "";

    /// <summary>Let the assistant auto-pick an OpenCode agent from the user request.</summary>
    public bool VoiceAutoAgent { get; set; } = true;

    /// <summary>Speak assistant replies aloud (TTS).</summary>
    public bool VoiceSpeakResponses { get; set; } = true;

    /// <summary>TTS voice culture: ru-RU, en-US, de-DE, etc.</summary>
    public string VoiceTtsLanguage { get; set; } = "ru-RU";

    /// <summary>Last app version the user saw the changelog for. Empty = never shown.</summary>
    public string LastSeenVersion { get; set; } = "";

    /// <summary>Normalize values after deserialization to guard against corrupt/invalid JSON.</summary>
    public AppSettings Normalize()
    {
        // Clamp version to known range for forward-compatible migrations.
        if (SettingsVersion < 1) SettingsVersion = 1;
        if (SettingsVersion < 2)
            SettingsVersion = 2;
        if (SettingsVersion < 3)
        {
            TelegramWsProxy = true;
            SettingsVersion = 3;
        }
        if (SettingsVersion < 4)
        {
            // v2.7.4: MTProto secret must be 32 hex chars (was 31 — bridge crashed).
            if (string.IsNullOrWhiteSpace(TelegramWsProxySecret)
                || TelegramWsProxySecret.Trim().Length != 32
                || !TelegramWsProxySecret.Trim().All(Uri.IsHexDigit))
                TelegramWsProxySecret = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a0";
            SettingsVersion = 4;
        }
        if (SettingsVersion < 5)
        {
            DiscordDesktopAutoLaunch = true;
            SettingsVersion = 5;
        }
        if (SettingsVersion < 6)
        {
            DiscordShopVpnBridge = false;
            DiscordShopAutoVpn = false;
            SettingsVersion = 6;
        }
        if (SettingsVersion < 7)
        {
            DiscordShopVpnBridge = false;
            DiscordShopAutoVpn = false;
            SettingsVersion = 7;
        }
        if (SettingsVersion < 8)
        {
            DiscordShopBridge = true;
            SettingsVersion = 8;
        }
        if (SettingsVersion < 9)
        {
            TikTokWebHosts = true;
            InstagramWebHosts = true;
            WhatsAppWebHosts = true;
            SettingsVersion = 9;
        }
        if (SettingsVersion < 10)
        {
            DiscordDesktopAutoLaunch = false;
            TelegramWsProxy = false;
            SettingsVersion = 10;
        }
        if (SettingsVersion < 11)
        {
            TelegramWsProxy = true;
            DiscordDesktopAutoLaunch = false;
            SettingsVersion = 11;
        }
        if (SettingsVersion < 12)
        {
            FtpUpdateEnabled = true;
            SettingsVersion = 12;
        }
        if (SettingsVersion < 13)
        {
            if (string.IsNullOrWhiteSpace(OpenCodeUrl))
                OpenCodeUrl = "http://127.0.0.1:4096";
            if (string.IsNullOrWhiteSpace(OpenCodeUsername))
                OpenCodeUsername = "opencode";
            VoiceAutoAgent = true;
            VoiceSpeakResponses = true;
            SettingsVersion = 13;
        }
        if (SettingsVersion < 14)
        {
            OpenCodeApiKey ??= "";
            SettingsVersion = 14;
        }
        if (SettingsVersion < 15)
        {
            VoiceAutoAgent = true;
            VoiceSpeakResponses = true;
            SettingsVersion = 15;
        }
        if (SettingsVersion < 16)
        {
            if (string.IsNullOrWhiteSpace(VoiceTtsLanguage))
                VoiceTtsLanguage = "ru-RU";
            SettingsVersion = 16;
        }
        if (SettingsVersion < 17)
        {
            AutostartEngine = true;
            SettingsVersion = 17;
        }
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
