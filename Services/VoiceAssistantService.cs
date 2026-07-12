using ZapretUI.Models;

namespace ZapretUI.Services;

public sealed class VoiceAssistantService : IDisposable
{
    private readonly OpenCodeApiService _api = new();
    private readonly SpeechService _speech = new();
    private IReadOnlyList<string> _agents = [];

    public bool CanListen => _speech.RecognitionAvailable;
    public bool CanSpeak => _speech.SynthesisAvailable;

    public async Task<(bool Ok, string Message)> RefreshConnectionAsync(AppSettings settings, CancellationToken ct = default)
    {
        string url = VoiceBackendDefaults.ResolveOpenCodeUrl(settings);
        string user = VoiceBackendDefaults.OpenCodeUsername(settings);
        string pass = VoiceBackendDefaults.OpenCodePassword(settings);
        string apiKey = VoiceBackendDefaults.OpenCodeApiKey(settings);

        var (ok, msg) = await _api.CheckHealthAsync(url, user, pass, ct).ConfigureAwait(false);
        if (!ok)
            return (ok, msg);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var (authOk, authMsg) = await _api.RegisterApiKeyAsync(url, user, pass, apiKey, ct).ConfigureAwait(false);
            if (!authOk)
                return (false, $"{msg}. Ключ: {authMsg}");
            msg = $"{msg}. {authMsg}";
        }

        _agents = await _api.ListAgentsAsync(url, user, pass, ct).ConfigureAwait(false);
        return (true, msg);
    }

    public IReadOnlyList<string> KnownAgents => _agents;

    public string? RecognitionHint => _speech.RecognitionError;
    public string RecognitionLanguage => _speech.RecognitionLanguage;
    public string SpeechCapability => _speech.CapabilitySummary;
    public string TtsLanguageLabel => _speech.TtsLanguageLabel;

    public void ConfigureFromSettings(AppSettings settings) =>
        _speech.ApplyTtsLanguage(settings.VoiceTtsLanguage);

    public static IReadOnlyList<VoiceTtsOption> GetTtsOptions() =>
        SpeechService.GetAvailableTtsOptions();

    public async Task<string?> ListenAsync(CancellationToken ct, Action<string>? onPartial = null) =>
        await _speech.ListenAsync(30_000, ct, onPartial).ConfigureAwait(false);

    public void StopListening() => _speech.StopListening();

    public async Task<(string Reply, string? Agent)> AskAsync(AppSettings settings, string userText, CancellationToken ct)
    {
        string url = VoiceBackendDefaults.ResolveOpenCodeUrl(settings);
        string user = VoiceBackendDefaults.OpenCodeUsername(settings);
        string pass = VoiceBackendDefaults.OpenCodePassword(settings);

        var (agent, system) = VoiceAgentRouter.Route(userText, _agents, settings.VoiceTtsLanguage);
        string? picked = VoiceBackendDefaults.AutoAgent ? agent : null;
        string reply = await _api.PromptAsync(url, user, pass, userText, picked, system, ct)
            .ConfigureAwait(false);
        reply = VoiceResponseSanitizer.Sanitize(reply, settings.VoiceTtsLanguage);
        return (reply, agent);
    }

    public Task SpeakAsync(string text, CancellationToken ct) =>
        _speech.SpeakAsync(text, ct);

    public void StopSpeaking() => _speech.StopSpeaking();

    public void ResetSession() => _api.ResetSession();

    public void Dispose() => _speech.Dispose();
}
