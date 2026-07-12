using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// User IP rules: exclude (games/direct) and bypass (resolved from domains).
/// Writes ipset files consumed by <see cref="EngineService"/>.
/// </summary>
public sealed class IpRuleService
{
    public const string ExcludeIpsetName = "exclude-custom";
    public const string BypassIpsetName = "bypass-custom";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly int[] ProbePorts = [443, 80, 3074];

    private readonly string _indexPath = Path.Combine(AppPaths.ListsDir, "custom-ip-rules.json");

    public IpRuleService() => AppPaths.EnsureCreated();

    public List<CustomIpRule> GetRules()
    {
        try
        {
            if (!File.Exists(_indexPath)) return new();
            var items = JsonSerializer.Deserialize<List<StoredRule>>(File.ReadAllText(_indexPath), JsonOpts);
            return items?.Select(s => new CustomIpRule
            {
                Cidr = s.Cidr,
                Kind = s.Kind,
                Label = s.Label,
                PingMs = s.PingMs,
            }).OrderBy(r => r.Cidr, StringComparer.Ordinal).ToList() ?? new();
        }
        catch { return new(); }
    }

    public bool Exists(string cidr) =>
        GetRules().Any(r => r.Cidr.Equals(cidr, StringComparison.OrdinalIgnoreCase));

    public void SaveRule(string cidr, IpRuleKind kind, long pingMs, string? label = null)
    {
        cidr = NormalizeCidr(cidr);
        if (cidr.Length == 0) return;

        var rules = GetRules().Where(r => !r.Cidr.Equals(cidr, StringComparison.OrdinalIgnoreCase)).ToList();
        rules.Add(new CustomIpRule { Cidr = cidr, Kind = kind, PingMs = pingMs, Label = label });
        Persist(rules);
    }

    public void Delete(string cidr)
    {
        cidr = NormalizeCidr(cidr);
        var rules = GetRules().Where(r => !r.Cidr.Equals(cidr, StringComparison.OrdinalIgnoreCase)).ToList();
        Persist(rules);
    }

    public void AddBypassIps(IEnumerable<string> ips, long pingMs = 0, string? label = null)
    {
        foreach (string ip in ips.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string cidr = NormalizeCidr(ip);
            if (cidr.Length == 0) continue;
            if (!Exists(cidr))
                SaveRule(cidr, IpRuleKind.Bypass, pingMs, label);
        }
    }

    public void WriteAggregates()
    {
        var rules = GetRules();
        WriteIpset(ExcludeIpsetName, rules.Where(r => r.Kind == IpRuleKind.Exclude).Select(r => r.Cidr));
        WriteIpset(BypassIpsetName, rules.Where(r => r.Kind == IpRuleKind.Bypass).Select(r => r.Cidr));
    }

    private void Persist(List<CustomIpRule> rules)
    {
        var stored = rules.Select(r => new StoredRule
        {
            Cidr = r.Cidr,
            Kind = r.Kind,
            Label = r.Label,
            PingMs = r.PingMs,
        }).ToList();
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(stored, JsonOpts));
        WriteAggregates();
    }

    private static void WriteIpset(string name, IEnumerable<string> cidrs)
    {
        var lines = cidrs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        string path = AppPaths.IpsetFile(name);
        if (lines.Count == 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }
        File.WriteAllText(path, string.Join('\n', lines));
    }

    public static string NormalizeCidr(string input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.Contains('/'))
        {
            var parts = s.Split('/', 2);
            if (IPAddress.TryParse(parts[0], out _))
                return parts[0] + "/" + parts[1].Trim();
            return "";
        }
        if (IPAddress.TryParse(s, out var addr))
            return addr.AddressFamily == AddressFamily.InterNetworkV6
                ? addr.ToString() + "/128"
                : addr.ToString() + "/32";
        return "";
    }

    public sealed class StabilityResult
    {
        public bool Ok { get; init; }
        public long AvgPingMs { get; init; }
        public List<int> OpenTcpPorts { get; init; } = new();
        public string Message { get; init; } = "";
    }

    /// <summary>TCP-only probe (no ICMP — it hangs on some PCs). Valid IP always accepted.</summary>
    public static async Task<StabilityResult> ProbeStabilityAsync(string cidrOrIp, CancellationToken ct)
    {
        string cidr = NormalizeCidr(cidrOrIp);
        if (cidr.Length == 0)
            return new StabilityResult { Ok = false, Message = "Некорректный IP." };

        string host = cidr.Split('/')[0];
        if (!IPAddress.TryParse(host, out var addr) || IsUnsafeProbeTarget(addr))
            return new StabilityResult { Ok = false, Message = "Нельзя проверять этот адрес." };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));

        var hits = new System.Collections.Concurrent.ConcurrentBag<(int port, long ms)>();
        var probes = ProbePorts.Select(async port =>
        {
            var (open, ms) = await TcpProbeAsync(host, port, budget.Token).ConfigureAwait(false);
            if (open) hits.Add((port, ms));
        });
        try { await Task.WhenAll(probes).ConfigureAwait(false); }
        catch { /* timeout ok */ }

        var ports = hits.Select(h => h.port).Distinct().OrderBy(p => p).ToList();
        long bestMs = hits.Count > 0 ? hits.Min(h => h.ms) : 0;

        if (ports.Count > 0)
        {
            return new StabilityResult
            {
                Ok = true,
                AvgPingMs = bestMs,
                OpenTcpPorts = ports.OrderBy(p => p).ToList(),
                Message = $"TCP OK: {string.Join(",", ports)} ({bestMs} ms)",
            };
        }

        return new StabilityResult
        {
            Ok = true,
            AvgPingMs = 0,
            OpenTcpPorts = ports,
            Message = "Сохранено (сервер не ответил на TCP — нормально для игр).",
        };
    }

    private static bool IsUnsafeProbeTarget(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;
        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)) return true;
        byte[] b = addr.GetAddressBytes();
        if (b.Length == 4 && b[0] >= 224) return true; // multicast
        return false;
    }

    public static async Task<List<string>> ResolveHostIpsAsync(string host, CancellationToken ct, int cap = 16)
    {
        host = TargetService.Normalize(host);
        if (host.Length == 0) return new();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addrs
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(cap)
                .ToList();
        }
        catch { return new(); }
    }

    public static async Task<List<string>> ResolveDomainsIpsAsync(IEnumerable<string> domains, CancellationToken ct, int cap = 32)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(4);
        var tasks = domains.Take(12).Select(async d =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                foreach (var ip in await ResolveHostIpsAsync(d, ct).ConfigureAwait(false))
                    ips.Add(ip);
            }
            catch { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { }
        return ips.Take(cap).ToList();
    }

    private static async Task<(bool open, long ms)> TcpProbeAsync(string host, int port, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(600));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch { return (false, 0); }
    }

    private sealed class StoredRule
    {
        public string Cidr { get; set; } = "";
        public IpRuleKind Kind { get; set; }
        public string? Label { get; set; }
        public long PingMs { get; set; }
    }
}
