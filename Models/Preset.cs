namespace ZapretUI.Models;

/// <summary>
/// A self-contained winws2 strategy. <see cref="Args"/> holds the command-line
/// arguments after the mandatory lua-init of the bundled libraries.
///
/// Tokens expanded by <c>EngineService</c> at launch time:
///   {FILES}    -> absolute path to the engine "files" folder (fake blobs)
///   {WF}       -> absolute path to the engine "windivert.filter" folder (raw-part filters)
///   {HOSTLIST} -> "--hostlist=&lt;path&gt;" for the active list, or removed if none
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>winws2 arguments (capture filters, --filter-*, --lua-desync=, --new, …).</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>True if this preset honours the selected hostlist via the {HOSTLIST} token.</summary>
    public bool UsesHostlist { get; set; }

    /// <summary>Built-in presets cannot be deleted (only duplicated/edited into a copy).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>The single preset the Simple mode applies with its one-click button.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>True if this preset desyncs an ee-MTProxy connection scoped to the user's proxy IP
    /// (the {IPSET:proxy} token). The engine cannot start until the user has entered their proxy host
    /// (Settings/Hostlists → list "proxy"), because without it the ipset is empty and nothing matches.</summary>
    public bool RequiresProxyHost { get; set; }

    /// <summary>True if assembled by the strategy generator (personalised). Marked ✨ in the list.</summary>
    public bool IsGenerated { get; set; }

    /// <summary>Section title for grouping in the presets list.</summary>
    public string GroupTitle => IsBuiltIn ? "Стратегии" : IsGenerated ? "✨ Генерация" : "Импорт / Личные";

    public Preset Clone() => new()
    {
        Name = Name,
        Description = Description,
        Args = new List<string>(Args),
        UsesHostlist = UsesHostlist,
        RequiresProxyHost = RequiresProxyHost,
        IsGenerated = IsGenerated,
        IsBuiltIn = false
    };

    /// <summary>Whether this preset's winws args target a service (not just the preset name).</summary>
    public bool IncludesService(string service)
    {
        if (Name.Contains(service, StringComparison.OrdinalIgnoreCase))
            return true;

        string key = service.ToLowerInvariant();
        foreach (var a in Args)
        {
            if (a.Contains($"{{HOSTLIST:{key}}}", StringComparison.OrdinalIgnoreCase)
                || a.Contains($"{{IPSET:{key}}}", StringComparison.OrdinalIgnoreCase))
                return true;

            if (key == "telegram" && a.Contains("telegram.org", StringComparison.OrdinalIgnoreCase))
                return true;
            if (key == "discord" && (a.Contains("discord.gg", StringComparison.OrdinalIgnoreCase)
                || a.Contains("discord.com", StringComparison.OrdinalIgnoreCase)
                || a.Contains("discord-shop", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (key == "youtube" && a.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
                return true;
            if (key == "whatsapp" && (a.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase)
                || a.Contains("whatsapp-web", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (key == "tiktok" && (a.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
                || a.Contains("tiktok-web", StringComparison.OrdinalIgnoreCase)
                || a.Contains("tiktok-upload", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (key == "instagram" && a.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
