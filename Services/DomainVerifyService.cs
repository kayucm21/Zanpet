using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>Pre-accept checks for custom domains: reachability + IP whitelist seed.</summary>
public static class DomainVerifyService
{
    public sealed class VerifyResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public long PingMs { get; init; }
        public List<string> ResolvedIps { get; init; } = new();
        public List<string> WhitelistDomains { get; init; } = new();
    }

    public static async Task<VerifyResult> VerifyAsync(
        DomainAutoConfigService.Plan plan,
        IReadOnlyList<string> domains,
        HostlistService hostlists,
        CancellationToken ct)
    {
        long pingMs = 0;
        bool reachable = false;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            var ping = await NetProbe.PingAsync(plan.Host, budget.Token).ConfigureAwait(false);
            pingMs = ping.ms;
            reachable = ping.ok;
            if (!reachable)
            {
                try
                {
                    var https = await NetProbe.HttpsAsync(plan.Host, budget.Token).ConfigureAwait(false);
                    reachable = https == DiagStatus.Ok;
                }
                catch { /* timeout ok */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* partial ok */ }

        List<string> ips;
        try
        {
            ips = await IpRuleService.ResolveDomainsIpsAsync(domains.Take(12), ct, cap: 24)
                .ConfigureAwait(false);
        }
        catch { ips = new(); }

        if (ips.Count == 0)
        {
            try
            {
                ips = await IpRuleService.ResolveHostIpsAsync(plan.Host, ct, cap: 8)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string listName in plan.HostlistNames)
        {
            foreach (var d in hostlists.ReadDomains(listName))
                whitelist.Add(d);
        }

        bool hasBundled = plan.HostlistNames.Length > 0 && domains.Count >= 3;

        if (!reachable && ips.Count == 0 && !hasBundled)
        {
            return new VerifyResult
            {
                Ok = false,
                Message = $"Домен {plan.Host} не отвечает. Проверьте адрес или интернет.",
                PingMs = pingMs,
            };
        }

        string note = reachable
            ? $"ping {pingMs} ms"
            : hasBundled ? "списки сервиса (без ping)" : "DNS только";

        return new VerifyResult
        {
            Ok = true,
            Message = $"Проверка OK: {note}, IP: {ips.Count}, списки: {whitelist.Count}",
            PingMs = pingMs,
            ResolvedIps = ips,
            WhitelistDomains = whitelist.Take(40).ToList(),
        };
    }
}
