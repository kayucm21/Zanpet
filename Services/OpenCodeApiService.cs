using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZapretUI.Services;

/// <summary>HTTP client for a local or remote OpenCode server (opencode serve).</summary>
public sealed class OpenCodeApiService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private string? _sessionId;

    public async Task<(bool Ok, string Message)> RegisterApiKeyAsync(string baseUrl, string? username, string? password,
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (true, "API-ключ не задан");

        string root = TrimUrl(baseUrl);
        string[] providers = ["opencode", "opencode-zen", "zen"];
        string? lastErr = null;

        foreach (string provider in providers)
        {
            var payload = new JsonObject
            {
                ["type"] = "api",
                ["key"] = apiKey.Trim()
            };

            using var req = new HttpRequestMessage(HttpMethod.Put, $"{root}/auth/{provider}")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            ApplyAuth(req, username, password);

            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return (true, $"API-ключ привязан ({provider})");

                lastErr = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
        }

        return (false, lastErr ?? "Не удалось привязать API-ключ");
    }

    public async Task<(bool Ok, string Message)> CheckHealthAsync(string baseUrl, string? username, string? password,
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, TrimUrl(baseUrl) + "/global/health");
            ApplyAuth(req, username, password);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"OpenCode: HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string version = TryReadString(body, "version") ?? "?";
            return (true, $"Подключено (v{version})");
        }
        catch (Exception ex)
        {
            return (false, $"OpenCode недоступен: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListAgentsAsync(string baseUrl, string? username, string? password,
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, TrimUrl(baseUrl) + "/agent");
            ApplyAuth(req, username, password);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return [];

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseAgentNames(body);
        }
        catch
        {
            return [];
        }
    }

    public async Task<string> PromptAsync(string baseUrl, string? username, string? password, string userText,
        string? agent, string systemPrompt, CancellationToken ct = default)
    {
        string root = TrimUrl(baseUrl);
        string sessionId = await EnsureSessionAsync(root, username, password, ct).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["parts"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = userText
                }
            },
            ["system"] = systemPrompt
        };
        if (!string.IsNullOrWhiteSpace(agent))
            payload["agent"] = agent;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/session/{sessionId}/message")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        ApplyAuth(req, username, password);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenCode HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        string? reply = ExtractAssistantText(body);
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("OpenCode вернул пустой ответ.");

        return reply.Trim();
    }

    /// <summary>Extract only user-facing text parts (skip reasoning/thinking/tools).</summary>
    public static string CleanRawReply(string raw, string? languageCode = "ru-RU") =>
        VoiceResponseSanitizer.Sanitize(raw, languageCode);

    public void ResetSession() => _sessionId = null;

    private async Task<string> EnsureSessionAsync(string root, string? username, string? password, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
            return _sessionId!;

        var payload = new JsonObject { ["title"] = "Zapret UI — голосовой помощник" };
        using var req = new HttpRequestMessage(HttpMethod.Post, root + "/session")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        ApplyAuth(req, username, password);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Не удалось создать сессию OpenCode: {TrimError(body)}");

        string? id = TryReadString(body, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("OpenCode не вернул ID сессии.");

        _sessionId = id;
        return id;
    }

    private static void ApplyAuth(HttpRequestMessage req, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return;

        string user = string.IsNullOrWhiteSpace(username) ? "opencode" : username.Trim();
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password.Trim()}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static string TrimUrl(string url) => url.Trim().TrimEnd('/');

    private static string? TryReadString(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static IReadOnlyList<string> ParseAgentNames(string json)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                root = data;

            if (root.ValueKind != JsonValueKind.Array)
                return names;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                    continue;
                }

                if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    string? s = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                }
                else if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    string? s = idProp.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                }
            }
        }
        catch { /* ignore */ }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ExtractAssistantText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            CollectText(doc.RootElement, sb);
            if (sb.Length > 0)
                return sb.ToString().Trim();

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                sb.Clear();
                CollectText(data, sb);
                if (sb.Length > 0)
                    return sb.ToString().Trim();
            }

            if (doc.RootElement.TryGetProperty("parts", out var parts))
            {
                sb.Clear();
                CollectText(parts, sb);
                if (sb.Length > 0)
                    return sb.ToString().Trim();
            }
        }
        catch { /* fall through */ }

        return TrimError(json);
    }

    private static bool IsSpeakablePartType(string? type) =>
        type is null or "text" or "assistant" or "";

    private static bool IsSkippedPartType(string? type) =>
        type is "reasoning" or "thinking" or "tool" or "tool-invocation" or "step-start" or "step-finish"
            or "patch" or "snapshot" or "subtask" or "compaction" or "retry" or "agent";

    private static void CollectText(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                string? partType = null;
                if (el.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                    partType = typeProp.GetString();

                if (IsSkippedPartType(partType))
                    return;

                if (el.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    if (IsSpeakablePartType(partType))
                    {
                        string? t = text.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(t.Trim());
                        }
                    }
                }

                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("parts") || prop.NameEquals("content") || prop.NameEquals("data") ||
                        prop.NameEquals("message") || prop.NameEquals("messages"))
                        CollectText(prop.Value, sb);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectText(item, sb);
                break;
        }
    }

    private static string TrimError(string body)
    {
        if (body.Length > 240)
            return body[..240] + "…";
        return body;
    }
}
