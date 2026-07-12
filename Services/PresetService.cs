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
            description: "Рекомендуемая стратегия: YouTube, Discord, Telegram, TikTok, Instagram, WhatsApp и остальной TLS/QUIC. Настройте «Область обхода» и «Игровой фильтр» в Настройках.",
            recommended: true
        ),
        Combo(
            name: "Multidisorder Advanced",
            description: "YouTube, Discord, Telegram, TikTok, Instagram, WhatsApp + продвинутый multidisorder/multisplit для остального TLS. Для агрессивных DPI.",
            recommended: false,
            buildArgs: BuildMultidisorderArgs
        ),
        Combo(
            name: "FakeSplit Pro",
            description: "YouTube, Discord, Telegram, TikTok, Instagram, WhatsApp + fakedsplit/fakeddisorder для остального TLS. Двойное искажение пакетов.",
            recommended: false,
            buildArgs: BuildFakeSplitArgs
        ),
        Combo(
            name: "TCP Segmentation",
            description: "YouTube, Discord, Telegram, TikTok, Instagram, WhatsApp + tcpseg для остального TLS. Сегментация с seqovl.",
            recommended: false,
            buildArgs: BuildTcpSegArgs
        ),
        Combo(
            name: "OOB Injection",
            description: "YouTube, Discord, Telegram, TikTok, Instagram, WhatsApp + OOB injection для остального TLS. URG-флаг в потоке.",
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

    /// <summary>Flowseal hostfakesplit — works well for Meta/WhatsApp/TikTok web.</summary>
    private static readonly string[] HostFakeSplit =
    [
        "--lua-desync=send:repeats=2",
        "--lua-desync=syndata:blob=tls_google",
        "--lua-desync=hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:tcp_md5:repeats=3",
    ];

    /// <summary>Flowseal fake+fakedsplit chain for Meta ipsets (WhatsApp desktop/web).</summary>
    private static readonly string[] FlowsealMetaFake =
    [
        "--lua-desync=fake:blob=tls_google:repeats=6:tcp_ts=-600000",
        "--lua-desync=fakedsplit:pattern=0x00:repeats=6:tcp_ts=-600000",
    ];

    private const string TikTokUploadDomains =
        "v16-up.tiktokv.com,api16-va.tiktokv.com,api19-va.tiktokv.com,api.tiktokv.com,api-h2.tiktokv.com,api-core-va.tiktokv.com," +
        "v16.tiktokcdn.com,v19.tiktokcdn.com,sf16-upload.tiktokcdn.com,open-upload.tiktokapis.com,open.tiktokapis.com," +
        "p16-tiktokcdn-com.akamaized.net,v16-up.amemv.com,gecko-va.tiktokv.com,dm16.tiktokv.com,log.tiktokv.com";

    private const string WhatsAppWebDomains =
        "web.whatsapp.com,www.web.whatsapp.com,static.whatsapp.net,mmg.whatsapp.net,g.whatsapp.net,v.whatsapp.net," +
        "dyn.web.whatsapp.com,graph.whatsapp.com,pps.whatsapp.net,edge-chat.facebook.com,star.fallback.c10r.facebook.com";

    /// <summary>YouTube first — video CDN (googlevideo QUIC) must match before other services.</summary>
    private static void AppendServiceProfiles(List<string> a)
    {
        AppendYoutubeProfiles(a, firstSegment: true);
        AppendSocialWebBridgeProfiles(a, firstSegment: false);
        AppendDiscordProfiles(a, firstSegment: false);
        AppendWhatsappProfiles(a, firstSegment: false);
        AppendTiktokProfiles(a, firstSegment: false);
        AppendInstagramProfiles(a, firstSegment: false);
        AppendTelegramDesktopProfiles(a, firstSegment: false);
        AppendTelegramWebProfiles(a, firstSegment: false);
    }

    /// <summary>WhatsApp Web + TikTok Web — highest priority (before Discord).</summary>
    private static void AppendSocialWebBridgeProfiles(List<string> a, bool firstSegment)
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
            $"--hostlist-domains={WhatsAppWebDomains}",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{HOSTLIST:whatsapp-web}",
            "--payload=all",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{HOSTLIST:whatsapp-web}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            $"--hostlist-domains={TikTokUploadDomains}",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            $"--hostlist-domains={TikTokUploadDomains}",
            "--payload=all",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(FlowsealMetaFake);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{IPSET:tiktok}",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(FlowsealMetaFake);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "--hostlist-domains=www.tiktok.com,tiktok.com,m.tiktok.com,libraweb.tiktok.com,ttwstatic.com,sf16-website-login.neutral.ttwstatic.com",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{HOSTLIST:tiktok-web}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "{HOSTLIST:whatsapp}",
            "{HOSTLIST:facebook}",
            "{HOSTLIST:instagram}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{IPSET:facebook}",
            "{IPSET:whatsapp}",
            "{IPSET:instagram}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(FlowsealMetaFake);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{IPSET:cloudflare}",
            "{HOSTLIST:tiktok}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443,3478,5222,5349,59234-59242",
            "{IPSET:whatsapp}",
            "{IPSET:facebook}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=12:payload=all",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=1024-65535",
            "{IPSET:whatsapp}",
            "{IPSET:facebook}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=stun_pat:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=8",
            "--lua-desync=fake:blob=quic2:repeats=8",
        });
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
            "--hostlist-domains=googlevideo.com",
            "--out-range=-d8",
            "--lua-desync=send:repeats=1",
            "--lua-desync=syndata:blob=tls_google",
            "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1:seqovl_pattern=tls_google",
        });

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

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{IPSET:googlevideo}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=8:payload=all",
        });
    }

    private static void AppendDiscordProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        // Gateway + CDN — мгновенная загрузка клиента (полная цепочка FastTls, 4 первых пакета).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=gateway.discord.gg,remote-auth-gateway.discord.gg,updates.discord.com,cdn.discordapp.com,media.discordapp.net,images-ext-1.discordapp.net,discordapp.com,discordapp.net,discordcdn.com,discord.media,latency.discord.media",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        // Shop / Nitro / billing — Stripe checkout iframe.
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=discord.com,www.discord.com,discord.store,discord.gift,discord.gifts,discordmerch.com",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{HOSTLIST:discord-shop}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "--hostlist-domains=discord.media,cdn.discordapp.com,media.discordapp.net",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "{HOSTLIST:discord}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,1080,2053,2083,2087,2096,8443",
            "{IPSET:discord}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

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

    private static void AppendTiktokProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        // TikTok upload API — instant (Discord gateway style, 4 passes).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            $"--hostlist-domains={TikTokUploadDomains}",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            $"--hostlist-domains={TikTokUploadDomains}",
            "--payload=all",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,2053,2083,2087,2096,8443",
            "{HOSTLIST:tiktok-upload}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,444-65535",
            "{IPSET:tiktok}",
            "{HOSTLIST:tiktok-upload}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(FlowsealMetaFake);

        // TikTok web — instant (как gateway Discord).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=www.tiktok.com,tiktok.com,libraweb.tiktok.com,ttwstatic.com,sf16-website-login.neutral.ttwstatic.com",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{HOSTLIST:tiktok-web}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,2053,2083,2087,2096,8443",
            "--hostlist-domains=tiktok.com,tiktokcdn.com,tiktokv.com,muscdn.com,byteoversea.com,ibytedtos.com,ibyteimg.com",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,2053,2083,2087,2096,8443",
            "{HOSTLIST:tiktok}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{IPSET:tiktok}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{HOSTLIST:tiktok-upload}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=12:payload=all",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{HOSTLIST:tiktok}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=10:payload=all",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{IPSET:tiktok}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=10:payload=all",
            "--lua-desync=udplen:increment=5:pattern=0xDEADBEEF",
        });
    }

    private static void AppendInstagramProfiles(List<string> a, bool firstSegment)
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
            "--hostlist-domains=instagram.com,cdninstagram.com,facebook.com,fbcdn.net",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{HOSTLIST:instagram}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443",
            "{IPSET:instagram}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443",
            "{IPSET:instagram}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6:payload=all",
        });
    }

    private static void AppendWhatsappProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        // WhatsApp Web — instant (Discord gateway style).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            $"--hostlist-domains={WhatsAppWebDomains}",
            "--out-range=-d2",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            $"--hostlist-domains={WhatsAppWebDomains}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "{HOSTLIST:whatsapp-web}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{HOSTLIST:whatsapp-web}",
            "--payload=all",
            "--out-range=-d10",
        });
        a.AddRange(HostFakeSplit);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "--hostlist-domains=whatsapp.com,whatsapp.net,graph.whatsapp.com,graph.facebook.com,connect.facebook.net,edge-chat.facebook.com",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{HOSTLIST:whatsapp}",
            "--out-range=-n4",
        });
        a.AddRange(FastTls);

        // Desktop/mobile — ipset + TLS fallback chain (как Telegram MTProto).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443,5222",
            "{IPSET:whatsapp}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=send:repeats=1",
            "--lua-desync=syndata:blob=tls_google",
            "--lua-desync=tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443,5222",
            "{IPSET:whatsapp}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443,5222",
            "{IPSET:whatsapp}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=fakedsplit:pos=1:blob=tls_google:ip_autottl=-3,3-20:ip6_autottl=-3,3-20:repeats=2",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=443,3478,5222,5349,59234-59242",
            "{IPSET:whatsapp}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=10:payload=all",
        });

        Next();
        a.AddRange(new[]
        {
            "--filter-udp=1024-65535",
            "{IPSET:whatsapp}",
            "--out-range=-n8",
            "--payload=all",
            "--lua-desync=fake:blob=stun_pat:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
            "--lua-desync=fake:blob=quic2:repeats=6",
        });
    }

    /// <summary>Telegram Desktop — Discord-style hosts + tls_multisplit, then MTProto ipset.</summary>
    private static void AppendTelegramDesktopProfiles(List<string> a, bool firstSegment)
    {
        void Next()
        {
            if (!firstSegment) a.Add("--new");
            firstSegment = false;
        }

        // Desktop gateway (venus/vesta) — same tls_multisplit as Discord gateway.
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=venus.web.telegram.org,vesta.web.telegram.org,venus-1.web.telegram.org,vesta-1.web.telegram.org",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTlsDiscordOnly);

        // Full telegram hostlist — instant TLS bypass on first packet (like Discord).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{HOSTLIST:telegram}",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTlsDiscordOnly);

        // WS/CDN (desktop media).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=443",
            "--hostlist-domains=zws1.web.telegram.org,zws2.web.telegram.org,zws3.web.telegram.org,zws4.web.telegram.org,zws5.web.telegram.org,pluto.web.telegram.org",
            "--payload=all",
            "--out-range=-d2",
        });
        a.AddRange(FastTlsDiscordOnly);

        // MTProto to DC IPs — classic send+syndata+pass (Default v4).
        Next();
        a.AddRange(new[]
        {
            "--filter-tcp=80,443,5222",
            "{IPSET:telegram}",
            "--payload=all",
            "--out-range=-n8",
            "--lua-desync=send:repeats=2",
            "--lua-desync=syndata:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20",
            "--lua-desync=pass",
        });

        // MTProto to DC IPs — extended fakedsplit/fake.
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
