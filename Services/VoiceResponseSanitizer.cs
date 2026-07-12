using System.Text;
using System.Text.RegularExpressions;

namespace ZapretUI.Services;

/// <summary>Strips agent reasoning / English meta-text — keeps only the spoken user-facing reply.</summary>
public static class VoiceResponseSanitizer
{
    private static readonly Regex ThinkingBlock = new(
        @"<think(?:ing)?>[\s\S]*?</think(?:ing)?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ReasoningPrefixes =
    [
        "The user is ",
        "The user wants ",
        "The user asked ",
        "I should respond",
        "I should answer",
        "I need to respond",
        "I need to answer",
        "I will respond",
        "I'll respond",
        "Let me respond",
        "As instructed",
        "as instructed",
    ];

    public static string Sanitize(string raw, string? languageCode = "ru-RU")
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string s = ThinkingBlock.Replace(raw, "").Trim();
        s = Regex.Replace(s, @"^\*\*([^*]+)\*\*\s*", "", RegexOptions.Singleline).Trim();

        string lang = (languageCode ?? "ru-RU").Trim().ToLowerInvariant();
        if (lang.StartsWith("ru", StringComparison.Ordinal))
            s = ExtractRussianReply(s);
        else if (lang.StartsWith("en", StringComparison.Ordinal))
            s = ExtractEnglishReply(s);
        else
            s = StripReasoningLines(s);

        s = CollapseWhitespace(s);
        return s.Length > 0 ? s : CollapseWhitespace(raw);
    }

    public static string ForSpeech(string text, string? languageCode = "ru-RU") =>
        Sanitize(text, languageCode);

    private static string ExtractRussianReply(string text)
    {
        text = StripReasoningLines(text);

        int idx = IndexOfCyrillic(text);
        if (idx < 0)
            return StripReasoningLines(text);

        string before = text[..idx].Trim();
        string after = text[idx..].Trim();

        if (before.Length > 0 && LooksLikeReasoning(before))
            return after;

        if (HasCyrillic(text) && HasLatinWords(before) && before.Length > 20)
            return after;

        return text;
    }

    private static string ExtractEnglishReply(string text)
    {
        text = StripReasoningLines(text);
        var sentences = SplitSentences(text);
        var english = sentences.Where(IsMostlyEnglish).ToList();
        return english.Count > 0 ? string.Join(" ", english) : text;
    }

    private static string StripReasoningLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();

        foreach (string line in lines)
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (LooksLikeReasoning(t) && !HasCyrillic(t))
                continue;
            kept.Add(t);
        }

        return kept.Count > 0 ? string.Join(" ", kept) : text;
    }

    private static bool LooksLikeReasoning(string text)
    {
        if (text.Length == 0) return false;
        foreach (string p in ReasoningPrefixes)
        {
            if (text.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return text.Contains("the user", StringComparison.OrdinalIgnoreCase)
            && text.Contains("russian", StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfCyrillic(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsCyrillic(text[i]))
                return i;
        }
        return -1;
    }

    private static bool IsCyrillic(char c) =>
        (c >= '\u0400' && c <= '\u04FF') || (c >= '\u0500' && c <= '\u052F');

    private static bool HasCyrillic(string text) => IndexOfCyrillic(text) >= 0;

    private static bool HasLatinWords(string text) =>
        Regex.IsMatch(text, @"\b[A-Za-z]{3,}\b");

    private static bool IsMostlyEnglish(string sentence)
    {
        int latin = 0;
        int cyr = 0;
        foreach (char c in sentence)
        {
            if (IsCyrillic(c)) cyr++;
            else if (char.IsLetter(c) && c < 128) latin++;
        }
        return latin > cyr;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        foreach (string part in Regex.Split(text, @"(?<=[.!?])\s+"))
        {
            string t = part.Trim();
            if (t.Length > 0)
                yield return t;
        }
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) { sb.Append(' '); space = true; }
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }
}
