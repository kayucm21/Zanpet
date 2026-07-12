using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace ZapretUI.Services;

/// <summary>Detects OS and auto-selected speech engines — no manual setup instructions.</summary>
public static class SpeechPlatformInfo
{
    public static string OsLabel { get; } = DetectOsLabel();

    public static IReadOnlyList<string> RecognizerLanguages { get; } = ListRecognizerLanguages();

    public static IReadOnlyList<string> VoiceLanguages { get; } = ListVoiceLanguages();

    public static string BuildCapabilityMessage(bool recognitionOk, bool synthesisOk, string activeRecognizer = "",
        string engine = "", string ttsLabel = "")
    {
        string rec = recognitionOk
            ? $"Микрофон: {activeRecognizer}" + (engine.Length > 0 ? $" ({engine})" : "")
            : "Микрофон: определяется…";

        string tts = synthesisOk
            ? (string.IsNullOrWhiteSpace(ttsLabel) ? $"Озвучка: {DescribeVoices()}" : $"Озвучка: {ttsLabel}")
            : "Озвучка: стандартный голос Windows";

        return $"{OsLabel} · {rec} · {tts}";
    }

    private static string DetectOsLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";

        return RuntimeInformation.OSDescription;
    }

    private static IReadOnlyList<string> ListRecognizerLanguages()
    {
        var list = new List<string>();
        try
        {
            list.AddRange(SpeechRecognitionEngine.InstalledRecognizers()
                .Select(r => r.Culture.DisplayName));
        }
        catch { /* ignore */ }

        return list.Distinct().ToList();
    }

    private static IReadOnlyList<string> ListVoiceLanguages()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Culture.DisplayName)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string DescribeVoices()
    {
        if (VoiceLanguages.Count == 0)
            return AutoPickVoiceLabel();

        var ru = VoiceLanguages.FirstOrDefault(v =>
            v.Contains("рус", StringComparison.OrdinalIgnoreCase) ||
            v.Contains("Russian", StringComparison.OrdinalIgnoreCase));
        if (ru is not null)
            return ru;

        return VoiceLanguages[0];
    }

    private static string AutoPickVoiceLabel()
    {
        string ui = CultureInfo.CurrentUICulture.DisplayName;
        return ui.Length > 0 ? ui : "системный";
    }
}

/// <summary>Tries to install Windows speech language packs silently in background.</summary>
public static class SpeechLanguageInstaller
{
    public static void TryInstallSpeechPacksInBackground()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                // Russian + UI language speech packs via DISM (silent, needs admin for full install)
                string ui = CultureInfo.CurrentUICulture.Name;
                string[] caps =
                [
                    "Language.Speech~~~ru-RU~0.0.1.0",
                    $"Language.Speech~~~{ui}~0.0.1.0",
                    "Language.Speech~~~en-US~0.0.1.0"
                ];

                foreach (string cap in caps.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism",
                        Arguments = $"/online /add-capability /capabilityname:{cap} /quiet /norestart",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    try { Process.Start(psi)?.WaitForExit(8000); } catch { }
                }

                TryInstallTtsPack("ru-RU");
            }
            catch { /* skip */ }
        });
    }

    public static void TryInstallTtsPack(string cultureCode)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string cap = $"Language.TextToSpeech~~~{cultureCode}~0.0.1.0";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dism",
                Arguments = $"/online /add-capability /capabilityname:{cap} /quiet /norestart",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit(8000);
        }
        catch { /* skip */ }
    }
}
