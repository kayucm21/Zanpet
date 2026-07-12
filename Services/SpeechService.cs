using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>Auto-picks speech recognition; TTS uses Windows OneCore voices when available.</summary>
public sealed class SpeechService : IDisposable
{
    private static readonly (string Code, string Label)[] KnownTtsLanguages =
    [
        ("ru-RU", "Русский"),
        ("en-US", "English (US)"),
        ("en-GB", "English (UK)"),
        ("de-DE", "Deutsch"),
        ("fr-FR", "Français"),
        ("es-ES", "Español"),
        ("uk-UA", "Українська"),
        ("kk-KZ", "Қазақша"),
    ];

    private SpeechRecognitionEngine? _recognizer;
    private SpeechSynthesizer? _synth;
    private readonly object _gate = new();
    private string _ttsCultureCode = "ru-RU";
    private bool _useWinRtTts;

    public bool RecognitionAvailable { get; private set; }
    public bool SynthesisAvailable { get; private set; }
    public string RecognitionLanguage { get; private set; } = "";
    public string TtsLanguageLabel { get; private set; } = "";
    public string CapabilitySummary { get; private set; } = "";
    public string? RecognitionError { get; private set; }

    public SpeechService()
    {
        InitSynthesis();
        InitRecognition();
    }

    public static IReadOnlyList<VoiceTtsOption> GetAvailableTtsOptions()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (WinRtSpeechBackend.IsAvailable)
        {
            foreach (var v in WinRtSpeechBackend.ListVoices())
                installed.Add(v.Code);
        }

        try
        {
            using var synth = new SpeechSynthesizer();
            foreach (var v in synth.GetInstalledVoices().Where(x => x.Enabled))
                installed.Add(v.VoiceInfo.Culture.Name);
        }
        catch { /* ignore */ }

        return KnownTtsLanguages
            .Select(k => new VoiceTtsOption
            {
                Code = k.Code,
                Label = installed.Contains(k.Code) ? k.Label : $"{k.Label} (установка…)",
                IsInstalled = installed.Contains(k.Code)
            })
            .ToList();
    }

    public bool ApplyTtsLanguage(string cultureCode)
    {
        _ttsCultureCode = string.IsNullOrWhiteSpace(cultureCode) ? "ru-RU" : cultureCode.Trim();

        if (WinRtSpeechBackend.IsAvailable && HasWinRtVoice(_ttsCultureCode))
        {
            _useWinRtTts = true;
            TtsLanguageLabel = WinRtSpeechBackend.ListVoices()
                .FirstOrDefault(v => v.Code.Equals(_ttsCultureCode, StringComparison.OrdinalIgnoreCase)
                    || v.Code.StartsWith(_ttsCultureCode[..2], StringComparison.OrdinalIgnoreCase)).Label
                ?? KnownTtsLanguages.FirstOrDefault(k => k.Code.Equals(_ttsCultureCode, StringComparison.OrdinalIgnoreCase)).Label
                ?? _ttsCultureCode;
            RefreshCapabilitySummary();
            return true;
        }

        _useWinRtTts = false;
        if (_synth is not null && TrySelectVoice(_synth, _ttsCultureCode, out string label))
        {
            TtsLanguageLabel = label;
            RefreshCapabilitySummary();
            return true;
        }

        SpeechLanguageInstaller.TryInstallTtsPack(_ttsCultureCode);
        TtsLanguageLabel = KnownTtsLanguages.FirstOrDefault(k => k.Code.Equals(_ttsCultureCode, StringComparison.OrdinalIgnoreCase)).Label
            ?? _ttsCultureCode;
        RefreshCapabilitySummary();
        return false;
    }

    private static bool HasWinRtVoice(string cultureCode)
    {
        if (!WinRtSpeechBackend.IsAvailable) return false;
        string two = cultureCode.Length >= 2 ? cultureCode[..2] : cultureCode;
        return WinRtSpeechBackend.ListVoices().Any(v =>
            v.Code.Equals(cultureCode, StringComparison.OrdinalIgnoreCase)
            || v.Code.StartsWith(two, StringComparison.OrdinalIgnoreCase));
    }

    private void InitSynthesis()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            ApplyTtsLanguage("ru-RU");
            SynthesisAvailable = WinRtSpeechBackend.IsAvailable || _synth is not null;
        }
        catch
        {
            SynthesisAvailable = WinRtSpeechBackend.IsAvailable;
        }
    }

    private void InitRecognition()
    {
        try
        {
            _recognizer = SpeechEngineProbe.CreateBestRecognizer(out string lang);
            if (_recognizer is not null)
            {
                RecognitionLanguage = lang;
                RecognitionAvailable = true;
                RefreshCapabilitySummary();
                return;
            }
        }
        catch (Exception ex)
        {
            RecognitionError = ex.Message;
        }

        SpeechLanguageInstaller.TryInstallSpeechPacksInBackground();
        RecognitionAvailable = false;
        RefreshCapabilitySummary();
        RecognitionError = "Микрофон подключается автоматически — перезапустите через минуту или пишите текстом.";
    }

    private void RefreshCapabilitySummary()
    {
        string tts = SynthesisAvailable && TtsLanguageLabel.Length > 0
            ? TtsLanguageLabel + (_useWinRtTts ? " (Windows)" : "")
            : "стандартный";
        CapabilitySummary = SpeechPlatformInfo.BuildCapabilityMessage(
            RecognitionAvailable, SynthesisAvailable,
            RecognitionAvailable ? RecognitionLanguage : "",
            SpeechEngineProbe.EngineKind,
            tts);
    }

    public Task<string?> ListenAsync(int timeoutMs, CancellationToken ct, Action<string>? onPartial = null)
    {
        if (!RecognitionAvailable || _recognizer is null)
            return Task.FromResult<string?>(null);

        return Task.Run(() => ListenCore(timeoutMs, ct, onPartial), ct);
    }

    public void StopListening()
    {
        lock (_gate)
        {
            try { _recognizer?.RecognizeAsyncStop(); } catch { }
            try { _recognizer?.RecognizeAsyncCancel(); } catch { }
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (!SynthesisAvailable || string.IsNullOrWhiteSpace(text))
            return;

        if (_useWinRtTts && WinRtSpeechBackend.IsAvailable)
        {
            if (await WinRtSpeechBackend.SpeakAsync(text, _ttsCultureCode, ct).ConfigureAwait(false))
                return;
        }

        if (_synth is null) return;

        await Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    TrySelectVoice(_synth, _ttsCultureCode, out _);
                    _synth.SpeakAsyncCancelAll();
                    string ssml =
                        $"<speak version='1.0' xml:lang='{XmlEscape(_ttsCultureCode)}'>" +
                        $"<prosody rate='0.95'>{XmlEscape(text)}</prosody></speak>";
                    try { _synth.SpeakSsml(ssml); }
                    catch { _synth.Speak(text); }
                }
                catch { /* non-fatal */ }
            }
        }, ct).ConfigureAwait(false);
    }

    public void StopSpeaking()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
    }

    public string TtsCultureCode => _ttsCultureCode;

    public void Dispose()
    {
        try { _recognizer?.Dispose(); } catch { }
        try { _synth?.Dispose(); } catch { }
    }

    private string? ListenCore(int timeoutMs, CancellationToken ct, Action<string>? onPartial)
    {
        lock (_gate)
        {
            if (_recognizer is null) return null;

            string? result = null;
            using var done = new ManualResetEventSlim(false);

            void OnHypothesis(object? s, SpeechHypothesizedEventArgs e)
            {
                string? t = e.Result?.Text;
                if (!string.IsNullOrWhiteSpace(t))
                    onPartial?.Invoke(t);
            }

            void OnRecognized(object? s, SpeechRecognizedEventArgs e)
            {
                string? t = e.Result?.Text;
                if (!string.IsNullOrWhiteSpace(t))
                    result = t.Trim();
                done.Set();
            }

            void OnCompleted(object? s, RecognizeCompletedEventArgs e) => done.Set();

            _recognizer.SpeechHypothesized += OnHypothesis;
            _recognizer.SpeechRecognized += OnRecognized;
            _recognizer.RecognizeCompleted += OnCompleted;

            try
            {
                _recognizer.RecognizeAsync(RecognizeMode.Single);
                done.Wait(timeoutMs, ct);
            }
            catch (OperationCanceledException)
            {
                StopListening();
                return null;
            }
            finally
            {
                _recognizer.SpeechHypothesized -= OnHypothesis;
                _recognizer.SpeechRecognized -= OnRecognized;
                _recognizer.RecognizeCompleted -= OnCompleted;
                try { _recognizer.RecognizeAsyncStop(); } catch { }
            }

            return result;
        }
    }

    private static bool TrySelectVoice(SpeechSynthesizer synth, string cultureCode, out string label)
    {
        label = "";
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureCode);
            var hinted = synth.GetInstalledVoices(culture)
                .FirstOrDefault(v => v.Enabled);
            if (hinted is not null)
            {
                synth.SelectVoice(hinted.VoiceInfo.Name);
                label = hinted.VoiceInfo.Culture.DisplayName;
                return true;
            }

            var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            string two = cultureCode.Length >= 2 ? cultureCode[..2] : cultureCode;

            var pick =
                voices.FirstOrDefault(v => v.Culture.Name.Equals(cultureCode, StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName.Equals(two, StringComparison.OrdinalIgnoreCase));

            if (pick is null)
                return false;

            synth.SelectVoice(pick.Name);
            label = pick.Culture.DisplayName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string XmlEscape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
