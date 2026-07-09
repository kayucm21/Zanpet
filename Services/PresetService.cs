using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Provides the built-in (code-defined) strategies plus any user-created ones
/// persisted in presets.json. Built-ins are read-only starting points; users
/// duplicate them to customise.
/// </summary>
public sealed class PresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<Preset> UserPresets { get; private set; } = new();

    /// <summary>Cached combined list (built-ins + user). Invalidated on add/remove/save.</summary>
    private IReadOnlyList<Preset>? _allCache;

    public PresetService() => Load();

    /// <summary>Built-ins first, then user presets. Cached for performance.</summary>
    public IReadOnlyList<Preset> All => _allCache ??= BuiltIns().Concat(UserPresets).ToList();

    /// <summary>Force cache invalidation (called after add/delete/import).</summary>
    private void InvalidateCache() => _allCache = null;

    public Preset? FindByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void AddUser(Preset p)
    {
        p.IsBuiltIn = false;
        string baseName = p.Name;
        int i = 2;
        while (FindByName(p.Name) is not null)
            p.Name = $"{baseName} ({i++})";
        UserPresets.Add(p);
        InvalidateCache();
        Save();
    }

    public void UpdateUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        Save();
    }

    public void DeleteUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        UserPresets.Remove(p);
        InvalidateCache();
        Save();
    }

    public void BulkImport(IEnumerable<Preset> presets)
    {
        foreach (var p in presets)
        {
            p.IsBuiltIn = false;
            string baseName = p.Name;
            int i = 2;
            while (FindByName(p.Name) is not null)
                p.Name = $"{baseName} ({i++})";
            UserPresets.Add(p);
        }
        InvalidateCache();
        Save();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            string tmp = AppPaths.PresetsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(UserPresets, JsonOpts));
            File.Move(tmp, AppPaths.PresetsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.PresetsFile))
            {
                var list = JsonSerializer.Deserialize<List<Preset>>(
                    File.ReadAllText(AppPaths.PresetsFile));
                if (list is not null)
                {
                    foreach (var p in list) p.IsBuiltIn = false;
                    UserPresets = list;
                }
            }
        }
        catch
        {
            UserPresets = new();
            try { File.Move(AppPaths.PresetsFile, AppPaths.PresetsFile + ".bak", overwrite: true); } catch { }
        }
    }

    public static List<Preset> BuiltIns() => new()
    {
        Combo(
            name: "YouTube + Discord + Telegram",
            description: "Рекомендуемая стратегия: YouTube, Discord (войс/_MEDIA), Telegram и остальной TLS/QUIC. Настройте «Область обхода» и «Игровой фильтр» в Настройках.",
            recommended: true
        ),
        Combo(
            name: "Multidisorder Advanced",
            description: "Продвинутый multidisorder с multisplit по нескольким позициям. Использует seqovl для дополнительного искажения. Для агрессивных DPI.",
            recommended: false,
            buildArgs: BuildMultidisorderArgs
        ),
        Combo(
            name: "FakeSplit Pro",
            description: "fakedsplit + fakeddisorder: отправка поддельных сегментов перед реальными. Двойное искажение для максимального обхода DPI.",
            recommended: false,
            buildArgs: BuildFakeSplitArgs
        ),
        Combo(
            name: "TCP Segmentation",
            description: "tcpseg: агрессивная сегментация TCP через seqovl. Разбивает пакет на мелкие куски с наложением. Для DPI, не разбирающих сегментацию.",
            recommended: false,
            buildArgs: BuildTcpSegArgs
        ),
        Combo(
            name: "OOB Injection",
            description: "Out-of-band injection: внедрение байта OOB в TCP поток с URG-флагом. Сбивает DPI, который не обрабатывает экстренные данные.",
            recommended: false,
            buildArgs: BuildOobArgs
        ),
    };

    private static Preset Combo(
        string name, string description, bool recommended,
        Func<List<string>>? buildArgs = null,
        string[]? proxyTls = null)
        => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsRecommended = recommended,
            RequiresProxyHost = proxyTls is not null,
            Args = buildArgs?.Invoke() ?? BuildComboArgs(proxyTls: proxyTls),
        };

    private static void AppendGlobalSetup(List<string> a)
    {
        a.AddRange(new[]
        {
            "{WF_TCP}",
            "{WF_UDP}",
            "--blob=tls_google:@{FILES}/fake/tls_clienthello_www_google_com.bin",
            "--blob=quic_google:@{FILES}/fake/quic_initial_www_google_com.bin",
            "--blob=stun_pat:@{FILES}/fake/stun.bin",
            "--blob=tls5:@{FILES}/fake/tls_clienthello_5.bin",
            "--blob=quic2:@{FILES}/fake/quic_2.bin",
            "--blob=fake_default_udp:0x00000000000000000000000000000000",
            "--wf-raw-part=@{WF}/windivert_part.discord_media.txt",
            "--wf-raw-part=@{WF}/windivert_part.discord_bidirectional.txt",
            "--wf-raw-part=@{WF}/windivert_part.stun.txt",
            "--wf-raw-part=@{WF}/windivert_part.wireguard.txt",
        });
    }

    /// <summary>Proven tls_multisplit chain from Default multisplit_sni (YouTube + Discord TCP).</summary>
    private static readonly string[] FastTls =
    [
        "--lua-desync=send:repeats=1",
        "--lua-desync=syndata:blob=tls_google",
        "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google",
    ];

    private static readonly string[] FastTlsDiscordOnly =
    [
        "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google",
    ];

    /// <summary>YouTube → Discord → Telegram (web + desktop MTProto first).</summary>
    private static void AppendServiceProfiles(List<string> a)
    {
        AppendTelegramDesktopProfiles(a, firstSegment: true);
        AppendYoutubeProfiles(a, firstSegment: false);
        AppendDiscordProfiles(a, firstSegment: false);
        AppendTelegramWebProfiles(a, firstSegment: false);
    }

    private static void AppendYoutubeProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "--hostlist-domains=googlevideo.com",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{HOSTLIST:youtube}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{IPSET:youtube}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6:payload=all",
        });
    }

    private static void AppendDiscordProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=updates.discord.com,gateway.discord.gg,remote-auth-gateway.discord.gg",
            "--out-range=-d2",
        });
        a.AddRange(FastTlsDiscordOnly);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "--hostlist-domains=discord.media,cdn.discordapp.com,media.discordapp.net",
            "--out-range=-d2",
        });
        a.AddRange(FastTlsDiscordOnly);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "{HOSTLIST:discord}",
            "--out-range=-n2",
        });
        a.AddRange(FastTlsDiscordOnly);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "{IPSET:discord}",
            "--out-range=-n2",
        });
        a.AddRange(FastTlsDiscordOnly);

        Next();
        a.AddRange(new[]
        {
            "--filter-l7=stun,discord",
            "--payload=stun,discord_ip_discovery",
            "--out-range=-n2",
            "--lua-desync=fake:blob=stun_pat:repeats=2",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{IPSET:discord}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic2:repeats=4",
            "--lua-desync=udplen:increment=5:pattern=0xDEADBEEF",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=19294-19344,50000-65535",
            "{IPSET:discord}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=stun_pat:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2",
            "--lua-desync=fake:blob=quic2:repeats=4",
        });
    }

    /// <summary>Telegram Desktop MTProto to DC IPs — highest priority, no seqovl.</summary>
    private static void AppendTelegramDesktopProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=1-65535",
            "{IPSET:telegram}",
            "{IPSET:telegram-bypass}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20",
            "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=4",
            "--lua-desync=pass",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{IPSET:telegram}",
            "{IPSET:telegram-bypass}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{IPSET:telegram}",
            "{IPSET:telegram-bypass}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=fakedsplit:pos=1:blob=tls_google:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{IPSET:telegram}",
            "{IPSET:telegram-bypass}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=fake:blob=stun_pat:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=8",
        });
    }

    /// <summary>Telegram web (hostfakesplit, no seqovl).</summary>
    private static void AppendTelegramWebProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{HOSTLIST:telegram}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=tls_google",
            "--lua-desync=hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:tcp_md5:repeats=2",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "--hostlist-domains=web.telegram.org,pluto.web.telegram.org,zws1.web.telegram.org,zws2.web.telegram.org,zws3.web.telegram.org,zws4.web.telegram.org,zws5.web.telegram.org,api.telegram.org",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=tls_google",
            "--lua-desync=hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:tcp_md5:repeats=3",
        });
    }

    private static void AppendCatchAllFallback(List<string> a, IReadOnlyList<string> tcpDesync)
    {
        a.Add("--new");
        a.AddRange(new[]
        {
            "--filter-tcp=80,443-65535",
            "{EXCLUDE:exclude}",
            "{IPSET_EXCLUDE:ru}",
            "--out-range=-d7",
        });
        a.AddRange(tcpDesync);
        a.Add("--new");
        a.AddRange(new[]
        {
            "--filter-udp=80,443-65535",
            "{IPSET_EXCLUDE:ru}",
            "--payload=all",
            "--out-range=-d8",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=10:payload=all",
        });
    }

    public static List<string> BuildComboArgs(string? discordFilter = null, string[]? proxyTls = null)
    {
        var a = new List<string>();
        AppendGlobalSetup(a);
        AppendServiceProfiles(a);
        AppendCatchAllFallback(a, FastTls);

        if (proxyTls is not null)
        {
            a.Add("--new");
            a.AddRange(new[] { "--filter-tcp=1-65535", "{IPSET:proxy}", "--out-range=-d7" });
            a.AddRange(proxyTls);
        }

        return a;
    }

    public static List<string> BuildMultidisorderArgs()
    {
        string[] youtubeTls =
        [
            "--lua-desync=send:repeats=3",
            "--lua-desync=syndata:blob=stun_pat:repeats=3",
            "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
            "--lua-desync=multidisorder:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
        ];
        var a = new List<string>();
        AppendGlobalSetup(a);
        AppendServiceProfiles(a);
        AppendCatchAllFallback(a, youtubeTls);
        return a;
    }

    public static List<string> BuildFakeSplitArgs()
    {
        string[] youtubeTls =
        [
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=stun_pat:repeats=2",
            "--lua-desync=fakedsplit:blob=tls_google:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
            "--lua-desync=fakeddisorder:blob=tls_google:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
        ];
        var a = new List<string>();
        AppendGlobalSetup(a);
        AppendServiceProfiles(a);
        AppendCatchAllFallback(a, youtubeTls);
        return a;
    }

    public static List<string> BuildTcpSegArgs()
    {
        string[] youtubeTls =
        [
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=stun_pat:repeats=2",
            "--lua-desync=tcpseg:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
        ];
        var a = new List<string>();
        AppendGlobalSetup(a);
        AppendServiceProfiles(a);
        AppendCatchAllFallback(a, youtubeTls);
        return a;
    }

    public static List<string> BuildOobArgs()
    {
        string[] youtubeTls =
        [
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=stun_pat:repeats=2",
            "--lua-desync=oob:byte=0x00:urp=1:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
            "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=stun_pat:ip_autottl=-3,3-20:ip6_autottl=-3,3-20",
        ];
        var a = new List<string>();
        AppendGlobalSetup(a);
        AppendServiceProfiles(a);
        AppendCatchAllFallback(a, youtubeTls);
        return a;
    }
}
