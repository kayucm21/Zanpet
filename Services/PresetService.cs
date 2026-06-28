using System.IO;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Provides the built-in (code-defined) strategies plus any user-created ones
/// persisted in presets.json. Built-ins are read-only starting points; users
/// duplicate them to customise.
/// </summary>
public sealed class PresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<Preset> UserPresets { get; private set; } = new();

    public PresetService() => Load();

    /// <summary>Built-ins first, then user presets.</summary>
    public IReadOnlyList<Preset> All => BuiltIns().Concat(UserPresets).ToList();

    public Preset? FindByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void AddUser(Preset p)
    {
        p.IsBuiltIn = false;
        // Ensure a unique name.
        string baseName = p.Name;
        int i = 2;
        while (FindByName(p.Name) is not null)
            p.Name = $"{baseName} ({i++})";
        UserPresets.Add(p);
        Save();
    }

    public void UpdateUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        Save();
    }

    public void DeleteUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        UserPresets.Remove(p);
        Save();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            // Write to a temp file then atomically replace: a crash mid-write can't truncate the real
            // presets.json (which Load would then reject, wiping every user preset).
            string tmp = AppPaths.PresetsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(UserPresets, JsonOpts));
            File.Move(tmp, AppPaths.PresetsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.PresetsFile))
            {
                var list = JsonSerializer.Deserialize<List<Preset>>(
                    File.ReadAllText(AppPaths.PresetsFile));
                if (list is not null)
                {
                    foreach (var p in list) p.IsBuiltIn = false;
                    UserPresets = list;
                }
            }
        }
        catch
        {
            UserPresets = new();
            // Keep the unreadable file aside instead of letting the next Save overwrite it with an
            // empty list — the user can recover their presets from the .bak.
            try { File.Move(AppPaths.PresetsFile, AppPaths.PresetsFile + ".bak", overwrite: true); } catch { }
        }
    }

    public static List<Preset> BuiltIns() => new()
    {
        Combo(
            name: "YouTube + Discord + Telegram",
            description: "Рекомендуемая стратегия: YouTube, Discord (войс/медиа), Telegram и остальной TLS/QUIC. Настройте «Область обхода» и «Игровой фильтр» в Настройках.",
            recommended: true
        ),
    };

    /// <summary>
    /// Builds a full per-service combo preset: Discord / YouTube TLS + QUIC + Discord voice.
    /// Uses only arguments confirmed working with the bundled Flowseal winws2 — no --filter-l7.
    /// </summary>
    private static Preset Combo(
        string name, string description, bool recommended,
        string[]? proxyTls = null)
        => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsRecommended = recommended,
            RequiresProxyHost = proxyTls is not null,
            Args = BuildComboArgs(proxyTls: proxyTls),
        };

    /// <summary>
    /// Build the shared combo argument list. Format matches the working imported classic presets:
    /// --filter-tcp/--filter-udp with port ranges (no --filter-l7), --lua-desync, --blob, --wf-raw-part.
    /// </summary>
    public static List<string> BuildComboArgs(string? discordFilter = null, string[]? proxyTls = null)
    {
        var a = new List<string>
        {
            // --- Глобальные настройки ---
            "{WF_TCP}",
            "{WF_UDP}",
            "--blob=tls_google:@{FILES}/fake/tls_clienthello_www_google_com.bin",
            "--blob=quic_google:@{FILES}/fake/quic_initial_www_google_com.bin",
            "--blob=stun_pat:@{FILES}/fake/stun.bin",
            "--wf-raw-part=@{WF}/windivert_part.discord_media.txt",
            "--wf-raw-part=@{WF}/windivert_part.stun.txt",
            "--wf-raw-part=@{WF}/windivert_part.wireguard.txt",
            // --- 1) TCP catch-all: YouTube + Discord + Telegram + весь TLS ---
            "--filter-tcp=80,443-65535",
            "{IPSET_EXCLUDE:ru}",
            "--out-range=-d7",
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=stun_pat:repeats=2",
            "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20",
        };
        if (proxyTls is not null)
        {
            a.Add("--new");
            a.AddRange(new[] { "--filter-tcp=1-65535", "{IPSET:proxy}", "--out-range=-d7" });
            a.AddRange(proxyTls);
        }
        // --- 2) UDP: QUIC YouTube + Discord + Telegram ---
        a.Add("--new");
        a.AddRange(new[] {
            "--filter-udp=80,443-65535",
            "{IPSET_EXCLUDE:ru}",
            "--payload=all",
            "--out-range=-d8",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=10:payload=all",
        });
        // --- 3) Discord voice (STUN + IP-discovery) ---
        a.Add("--new");
        a.AddRange(new[] {
            "--filter-udp=19294-19344,50000-65535",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2",
        });

        return a;
    }

}
