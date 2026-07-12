namespace ZapretUI.Services;

/// <summary>Picks an OpenCode agent from the user message and available agents list.</summary>
public static class VoiceAgentRouter
{
    private static readonly (string[] Keys, string Agent, string Hint)[] Routes =
    [
        (["обход", "dpi", "zapret", "winws", "движок", "стратег", "пресет", "блокир"],
            "build", "Ты помощник Zapret UI по обходу DPI. Отвечай кратко, простым русским языком, удобно для озвучки."),

        (["vpn", "xray", "vless", "прокси", "сервер"],
            "build", "Ты помощник Zapret UI по VPN. Объясняй подключение и диагностику простыми словами для голосового ответа."),

        (["домен", "domain", "сайт", "whatsapp", "telegram", "discord", "youtube", "tiktok", "instagram"],
            "plan", "Ты помощник Zapret UI по настройке доменов и списков. Предлагай шаги без лишней техники, для озвучки."),

        (["ip", "игр", "сервер", "исключен", "cidr", "порт"],
            "plan", "Ты помощник Zapret UI по IP-исключениям и игровому трафику. Отвечай коротко и по делу."),
    ];

    public static (string? Agent, string SystemPrompt) Route(string userText, IReadOnlyList<string> availableAgents,
        string? responseLanguageCode = "ru-RU")
    {
        string lower = userText.ToLowerInvariant();
        string langRule = LanguageRule(responseLanguageCode);

        foreach (var (keys, agent, hint) in Routes)
        {
            if (keys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                return (PickAgent(agent, availableAgents), hint + " " + langRule);
        }

        return (PickAgent("build", availableAgents),
            "Ты голосовой помощник Zapret UI. Отвечай кратко и понятно — ответ будет озвучен. " + langRule);
    }

    private static string LanguageRule(string? code)
    {
        string c = (code ?? "ru-RU").Trim().ToLowerInvariant();
        if (c.StartsWith("en", StringComparison.Ordinal))
            return "NEVER write reasoning or meta-commentary. Reply with ONLY one short final sentence in English for speech.";
        if (c.StartsWith("de", StringComparison.Ordinal))
            return "Antworte nur auf Deutsch. Kurze Sätze für Sprachausgabe.";
        if (c.StartsWith("fr", StringComparison.Ordinal))
            return "Réponds uniquement en français. Phrases courtes pour la synthèse vocale.";
        if (c.StartsWith("es", StringComparison.Ordinal))
            return "Responde solo en español. Frases cortas para voz.";
        return "НИКОГДА не пиши размышления, пояснения и текст на английском. Не пересказывай запрос пользователя. " +
               "Ответь ТОЛЬКО одной короткой финальной фразой на русском — её озвучат вслух.";
    }

    private static string? PickAgent(string preferred, IReadOnlyList<string> availableAgents)
    {
        if (availableAgents.Count == 0)
            return preferred;

        string? exact = availableAgents.FirstOrDefault(a =>
            string.Equals(a, preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        string? partial = availableAgents.FirstOrDefault(a =>
            a.Contains(preferred, StringComparison.OrdinalIgnoreCase));
        if (partial is not null)
            return partial;

        return availableAgents[0];
    }
}
