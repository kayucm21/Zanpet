using System.Globalization;
using System.Speech.Recognition;

namespace ZapretUI.Services;

/// <summary>Probes installed + direct-culture speech engines and picks the best match.</summary>
internal static class SpeechEngineProbe
{
    public static string EngineKind { get; private set; } = "System.Speech";

    public static SpeechRecognitionEngine? CreateBestRecognizer(out string language)
    {
        language = "";

        // 1) Installed recognizers list (official)
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count > 0)
        {
            var pick = PickFromInstalled(installed);
            if (TryBuild(pick.Culture, pick.Description, out var engine, out language))
                return engine;
        }

        // 2) Direct culture probe — works on many Windows PCs even when list is empty
        foreach (string cultureName in BuildCulturePriority())
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                if (TryBuild(culture, "auto", out var engine, out language))
                {
                    EngineKind = "Windows Speech (auto)";
                    return engine;
                }
            }
            catch { /* try next */ }
        }

        return null;
    }

    private static RecognizerInfo PickFromInstalled(IReadOnlyList<RecognizerInfo> installed)
    {
        string uiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return installed.FirstOrDefault(i => i.Culture.Name.Equals("ru-RU", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(i => i.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(i => i.Culture.TwoLetterISOLanguageName.Equals(uiLang, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(i => i.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? installed[0];
    }

    private static IEnumerable<string> BuildCulturePriority()
    {
        var list = new List<string>();
        void Add(string? c)
        {
            if (string.IsNullOrWhiteSpace(c)) return;
            if (!list.Contains(c, StringComparer.OrdinalIgnoreCase))
                list.Add(c);
        }

        Add(CultureInfo.CurrentUICulture.Name);
        Add(CultureInfo.CurrentCulture.Name);
        Add(CultureInfo.InstalledUICulture.Name);
        Add("ru-RU");
        Add("ru");
        Add("en-US");
        Add("en-GB");
        Add("de-DE");
        Add("fr-FR");
        Add("es-ES");
        Add("uk-UA");
        Add("kk-KZ");

        return list;
    }

    private static bool TryBuild(CultureInfo culture, string desc, out SpeechRecognitionEngine? engine, out string language)
    {
        engine = null;
        language = "";
        try
        {
            var rec = new SpeechRecognitionEngine(culture);
            rec.SetInputToDefaultAudioDevice();
            rec.LoadGrammar(new DictationGrammar { Name = "dictation" });
            rec.BabbleTimeout = TimeSpan.FromSeconds(0);
            rec.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
            rec.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
            rec.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.8);

            engine = rec;
            language = desc == "auto"
                ? culture.DisplayName
                : $"{culture.DisplayName}";
            return true;
        }
        catch
        {
            return false;
        }
    }
}
