using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Manages user-defined bypass targets. Each target is a root domain whose related
/// domains (found via crt.sh Certificate Transparency, or entered by hand) are stored
/// as a <c>target-&lt;name&gt;.txt</c> hostlist under the lists folder. The union of all
/// target domains is mirrored to <c>targets.txt</c>, which:
///   • feeds the diagnostics matrix + auto-select/generation goal hosts, and
///   • is subtracted from the catch-all exclude by <see cref="EngineService"/> so the
///     active strategy actually desyncs these domains (even sensitive ones like yandex.ru
///     that the default exclude protects).
/// </summary>
public sealed class TargetService
{
    private const string Prefix = "target-";
    public const string AggregateName = "targets";

    private static HttpClient Http => HttpFactory.General;

    public TargetService()
    {
        AppPaths.EnsureCreated();
    }

    private static string PathFor(string name) => Path.Combine(AppPaths.ListsDir, Prefix + name + ".txt");

    /// <summary>All saved targets with their domain counts.</summary>
    public List<CustomTarget> GetTargets()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, Prefix + "*.txt")
                .Select(f => Path.GetFileNameWithoutExtension(f)!.Substring(Prefix.Length))
                .Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new CustomTarget { Name = n, DomainCount = ReadDomains(n).Count })
                .ToList();
        }
        catch { return new(); }
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public List<string> ReadDomains(string name)
    {
        try
        {
            string p = PathFor(name);
            if (!File.Exists(p)) return new();
            return File.ReadAllLines(p)
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Union of every target's domains (what gets probed / bypassed).</summary>
    public List<string> AllDomains()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, Prefix + "*.txt")
                .SelectMany(f => File.ReadAllLines(f))
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    public void Save(string name, IEnumerable<string> domains, IEnumerable<int>? openPorts = null)
    {
        name = Normalize(name);
        if (name.Length == 0) return;
        var clean = domains
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>();
        if (openPorts is not null)
        {
            var ports = openPorts.Distinct().OrderBy(p => p).ToList();
            if (ports.Count > 0)
                lines.Add("# ports=" + string.Join(',', ports));
        }
        lines.AddRange(clean);
        File.WriteAllText(PathFor(name), string.Join('\n', lines));
        WriteAggregate();
    }

    public void Delete(string name)
    {
        try { var p = PathFor(name); if (File.Exists(p)) File.Delete(p); } catch { }
        WriteAggregate();
    }

    /// <summary>Mirror the union of all target domains to targets.txt (engine + diagnostics read this).</summary>
    private void WriteAggregate()
    {
        try { File.WriteAllText(Path.Combine(AppPaths.ListsDir, AggregateName + ".txt"), string.Join('\n', AllDomains())); }
        catch { /* non-fatal */ }
    }

    /// <summary>Strip scheme/path/port from user input, leaving a bare host. Returns "" for anything
    /// that can't be a safe file-name component (the host becomes target-&lt;name&gt;.txt).</summary>
    public static string Normalize(string input)
    {
        string s = (input ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        s = s.Split('/', '\\', '?', '#')[0];     // also split on '\' so it can't escape the lists folder
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        s = s.Trim('.');
        // The result is used to build a file path — reject path traversal / invalid file-name chars.
        if (s.Length == 0 || s.Contains("..") || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "";
        return s;
    }

    /// <summary>Registrable root: web.whatsapp.com → whatsapp.com.</summary>
    public static string RegistrableRoot(string host)
    {
        host = Normalize(host);
        var p = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return host;
        bool multi = p.Length >= 3 && p[^1].Length == 2 &&
                     p[^2] is "co" or "com" or "ne" or "or" or "go" or "ac";
        return multi ? string.Join('.', p[^3..]) : string.Join('.', p[^2], p[^1]);
    }

    /// <summary>Parallel crt.sh on several roots (fast, best-effort).</summary>
    public async Task<List<string>> DiscoverCrtAsync(IEnumerable<string> roots, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = roots
            .Select(Normalize)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        if (unique.Count == 0) return found.ToList();

        var tasks = unique.Select(r => CrtShSubdomainsAsync(r, ct)).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* partial ok */ }

        foreach (var t in tasks)
        {
            try
            {
                if (t.IsCompletedSuccessfully)
                    foreach (var d in t.Result) found.Add(d);
            }
            catch { }
        }
        return found.ToList();
    }

    /// <summary>
    /// Expand a root domain: crt.sh subdomains + a few cross-TLD mirrors.
    /// Hard time budget (~5 s) so «Принять» does not hang on slow DNS/crt.sh.
    /// </summary>
    public async Task<List<string>> ExpandAsync(string rootDomain, int cap, CancellationToken ct)
    {
        string root = Normalize(rootDomain);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.Length == 0) return result.ToList();

        result.Add(root);
        if (!root.StartsWith("www.", StringComparison.Ordinal))
            result.Add("www." + root);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            var crt = CrtShSubdomainsAsync(root, budget.Token);
            var tld = CrossTldVariantsAsync(root, budget.Token);
            await Task.WhenAll(crt, tld).ConfigureAwait(false);
            foreach (var d in await crt.ConfigureAwait(false)) result.Add(d);
            foreach (var d in await tld.ConfigureAwait(false)) result.Add(d);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget expired — return root (+ anything collected so far is already in result).
        }

        return result
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, cap))
            .ToList();
    }

    /// <summary>Minimal list for instant save before background refinement.</summary>
    public static List<string> QuickSeed(string rootDomain)
    {
        string root = Normalize(rootDomain);
        if (root.Length == 0) return new();
        var list = new List<string> { root };
        if (!root.StartsWith("www.", StringComparison.Ordinal))
            list.Add("www." + root);
        return list;
    }

    private async Task<List<string>> CrtShSubdomainsAsync(string root, CancellationToken ct)
    {
        var found = new List<string>();
        const int maxHosts = 40;
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(2.5));
            string url = $"https://crt.sh/?q=%25.{Uri.EscapeDataString(root)}&output=json";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: reqCts.Token).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (found.Count >= maxHosts) break;
                if (!el.TryGetProperty("name_value", out var nv)) continue;
                foreach (var raw in (nv.GetString() ?? "").Split('\n'))
                {
                    if (found.Count >= maxHosts) break;
                    string d = raw.Trim().ToLowerInvariant();
                    if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];
                    if (d.Length == 0 || d.Contains(' ') || d.Contains('@') || !d.Contains('.')) continue;
                    if (d == root || d.EndsWith("." + root, StringComparison.Ordinal))
                    {
                        if (seen.Add(d)) found.Add(d);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* crt.sh slow/offline — root+www already enough */ }
        return found;
    }

    /// <summary>Small TLD set for a fast DNS probe (not the full world list).</summary>
    private static readonly string[] FastBrandTlds = { "ru", "com", "net", "org", "io", "by", "kz", "ua" };

    private static async Task<List<string>> CrossTldVariantsAsync(string root, CancellationToken ct)
    {
        string brand = BrandLabel(root);
        if (brand.Length < 2 || brand.Equals(root, StringComparison.OrdinalIgnoreCase))
            return new();

        var candidates = FastBrandTlds
            .Select(t => brand + "." + t)
            .Where(h => !h.Equals(root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return new();

        var alive = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(8);
        var tasks = candidates.Select(async host =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { if (await ResolvesAsync(host, ct).ConfigureAwait(false)) alive.Add(host); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return alive.ToList();
    }

    /// <summary>The registrable brand label (yandex.ru → "yandex", a.yandex.com.tr → "yandex").</summary>
    private static string BrandLabel(string host)
    {
        var p = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return p.Length == 1 ? p[0] : "";
        // Multi-label suffix (com.tr, co.uk): brand sits one label further left.
        bool multi = p.Length >= 3 && p[^1].Length == 2 &&
                     p[^2] is "com" or "co" or "net" or "org" or "edu" or "gov" or "ac";
        return multi ? p[^3] : p[^2];
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1.2));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addrs.Length > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SocketException) { return false; }
        catch { return false; }
    }
}
