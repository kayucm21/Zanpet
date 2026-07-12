using System.Net.Sockets;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// After the user pastes a URL/host: pick bundled domains, quick crt.sh, probe ports, tune settings.
/// </summary>
public sealed class DomainAutoConfigService
{
    private static readonly int[] DefaultPorts = [443, 80, 5222, 8443, 8080];

    private readonly HostlistService _hostlists;
    private readonly TargetService _targets;

    public DomainAutoConfigService(HostlistService hostlists, TargetService targets)
    {
        _hostlists = hostlists;
        _targets = targets;
    }

    public sealed class Plan
    {
        public required string Host { get; init; }
        public required string Root { get; init; }
        public required string Label { get; init; }
        public string ServiceKey { get; init; } = "";
        public required string[] HostlistNames { get; init; }
        public required string[] CrtRoots { get; init; }
        public required int[] Ports { get; init; }
        public int DomainCap { get; init; } = 100;
        public Action<AppSettings>? Tune { get; init; }
    }

    public sealed class Result
    {
        public required Plan Plan { get; init; }
        public required List<string> Domains { get; init; }
        public required List<int> OpenPorts { get; init; }
    }

    public Plan Detect(string input)
    {
        string host = TargetService.Normalize(input);
        string root = TargetService.RegistrableRoot(host);

        if (Contains(host, "whatsapp", "wa.me", "whatsappbrand"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "WhatsApp",
                ServiceKey = "whatsapp",
                HostlistNames = ["whatsapp", "whatsapp-web", "facebook"],
                CrtRoots = [root, "whatsapp.com", "whatsapp.net", "facebook.com"],
                Ports = DefaultPorts,
                DomainCap = 120,
                Tune = s => s.WhatsAppWebHosts = true,
            };

        if (Contains(host, "telegram", "t.me", "telegra.ph"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "Telegram",
                ServiceKey = "telegram",
                HostlistNames = ["telegram"],
                CrtRoots = [root, "telegram.org", "t.me"],
                Ports = [443, 80, 5222, 8443],
                DomainCap = 80,
                Tune = s =>
                {
                    s.TelegramWebHosts = true;
                    s.TelegramWsProxy = true;
                },
            };

        if (Contains(host, "tiktok"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "TikTok",
                ServiceKey = "tiktok",
                HostlistNames = ["tiktok", "tiktok-web", "tiktok-upload"],
                CrtRoots = [root, "tiktok.com", "tiktokv.com", "tiktokcdn.com"],
                Ports = DefaultPorts,
                DomainCap = 100,
                Tune = s => s.TikTokWebHosts = true,
            };

        if (Contains(host, "instagram", "cdninstagram"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "Instagram",
                ServiceKey = "instagram",
                HostlistNames = ["instagram", "facebook"],
                CrtRoots = [root, "instagram.com", "cdninstagram.com"],
                Ports = DefaultPorts,
                DomainCap = 80,
                Tune = s => s.InstagramWebHosts = true,
            };

        if (Contains(host, "discord"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "Discord",
                ServiceKey = "discord",
                HostlistNames = ["discord", "discord-shop"],
                CrtRoots = [root, "discord.com", "discord.gg"],
                Ports = [443, 80, 8443],
                DomainCap = 80,
                Tune = s => s.DiscordWebHosts = true,
            };

        if (Contains(host, "youtube", "googlevideo", "ytimg", "youtu.be"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "YouTube",
                ServiceKey = "youtube",
                HostlistNames = ["youtube"],
                CrtRoots = [root, "youtube.com", "googlevideo.com"],
                Ports = [443, 80],
                DomainCap = 60,
                Tune = s => s.YoutubeWebHosts = true,
            };

        if (Contains(host, "facebook", "fbcdn", "fb.com"))
            return new Plan
            {
                Host = host,
                Root = root,
                Label = "Facebook / Meta",
                HostlistNames = ["facebook", "instagram"],
                CrtRoots = [root, "facebook.com", "fbcdn.net"],
                Ports = DefaultPorts,
                DomainCap = 80,
                Tune = s => s.InstagramWebHosts = true,
            };

        return new Plan
        {
            Host = host,
            Root = root,
            Label = "Сайт",
            HostlistNames = [],
            CrtRoots = [root],
            Ports = [443, 80],
            DomainCap = 50,
        };
    }

    public async Task<Result> AnalyzeAsync(Plan plan, CancellationToken ct)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            plan.Host,
            plan.Root,
        };
        foreach (var d in TargetService.QuickSeed(plan.Host))
            domains.Add(d);

        foreach (string listName in plan.HostlistNames)
        {
            foreach (var d in _hostlists.ReadDomains(listName))
                domains.Add(d);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));

        var crtTask = _targets.DiscoverCrtAsync(plan.CrtRoots, budget.Token);
        var portTask = ProbePortsAsync(plan.Host, plan.Ports, budget.Token);

        try
        {
            await Task.WhenAll(crtTask, portTask).ConfigureAwait(false);
            foreach (var d in await crtTask.ConfigureAwait(false))
                domains.Add(d);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            try
            {
                if (crtTask.IsCompletedSuccessfully)
                    foreach (var d in crtTask.Result) domains.Add(d);
            }
            catch { }
        }

        List<int> openPorts;
        try { openPorts = await portTask.ConfigureAwait(false); }
        catch { openPorts = new(); }

        var domainList = domains
            .OrderBy(d => d.Length)
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(plan.DomainCap)
            .ToList();

        return new Result
        {
            Plan = plan,
            Domains = domainList,
            OpenPorts = openPorts,
        };
    }

    private static bool Contains(string host, params string[] parts) =>
        parts.Any(p => host.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static async Task<List<int>> ProbePortsAsync(string host, int[] ports, CancellationToken ct)
    {
        var open = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var gate = new SemaphoreSlim(6);
        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(700));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                open.Add(port);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* closed / filtered */ }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return open.OrderBy(p => p).ToList();
    }
}
