using System.Net.Http;
using System.Net.Http.Headers;

namespace ZapretUI.Services;

/// <summary>
/// Shared HttpClient factory: a single HttpClient per purpose with sensible defaults.
/// Avoids socket exhaustion from per-request HttpClient instances.
/// </summary>
public static class HttpFactory
{
    private static readonly object _gate = new();
    private static HttpClient? _github;
    private static HttpClient? _general;

    /// <summary>
    /// GitHub API client: Accepts vnd.github+json, UserAgent set, 10-minute timeout.
    /// </summary>
    public static HttpClient GitHub
    {
        get
        {
            if (_github is not null) return _github;
            lock (_gate)
            {
                return _github ??= CreateGitHubClient();
            }
        }
    }

    /// <summary>
    /// General-purpose client for non-GitHub downloads (xray, app updates, etc.).
    /// </summary>
    public static HttpClient General
    {
        get
        {
            if (_general is not null) return _general;
            lock (_gate)
            {
                return _general ??= CreateGeneralClient();
            }
        }
    }

    private static HttpClient CreateGitHubClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretUI", "2.4"));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static HttpClient CreateGeneralClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretUI", "2.4"));
        return http;
    }
}
