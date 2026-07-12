namespace ZapretUI.Services;

/// <summary>Built-in voice backend endpoints. Switch VdsOpenCodeUrl when VDS is ready — users never configure this.</summary>
public static class VoiceBackendDefaults
{
    /// <summary>Future VDS endpoint. Empty = use local/auto-start.</summary>
    public const string VdsOpenCodeUrl = "";

    public const string LocalOpenCodeUrl = "http://127.0.0.1:4096";

    public static string ResolveOpenCodeUrl(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(VdsOpenCodeUrl))
            return VdsOpenCodeUrl.Trim().TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(settings.OpenCodeUrl))
            return settings.OpenCodeUrl.Trim().TrimEnd('/');

        return LocalOpenCodeUrl;
    }

    public static string OpenCodeUsername(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.OpenCodeUsername) ? "opencode" : settings.OpenCodeUsername.Trim();

    public static string OpenCodePassword(AppSettings settings) => settings.OpenCodePassword ?? "";

    public static string OpenCodeApiKey(AppSettings settings) => settings.OpenCodeApiKey ?? "";

    /// <summary>Auto agent + TTS are always on — not user-configurable.</summary>
    public const bool AutoAgent = true;
    public const bool SpeakResponses = true;
}
