using System.IO;

namespace ZapretUI.Services;

/// <summary>
/// Manages domain hostlists stored as plain .txt files under the lists folder,
/// one domain per line — exactly the format winws2 expects for --hostlist.
/// </summary>
public sealed class HostlistService
{
    public HostlistService() => AppPaths.EnsureCreated();

    /// <summary>Names (without extension) of all available lists.</summary>
    public IReadOnlyList<string> GetLists()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                // ipset-*.txt are resolved IP sets, not domain hostlists — hide them from the list UI.
                .Where(n => !n.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
                // Custom-target machinery (managed on the Диагностика tab) — hide from the hostlist UI.
                .Where(n => !n.StartsWith("target-", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals("targets", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals("exclude-eff", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public string GetPath(string name) => Path.Combine(AppPaths.ListsDir, name + ".txt");

    public bool Exists(string name) => File.Exists(GetPath(name));

    public string Read(string name)
    {
        string p = GetPath(name);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    public List<string> ReadDomains(string name) =>
        Read(name)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    public void Write(string name, string content)
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(GetPath(name), NormalizeNewlines(content));
    }

    public void Create(string name)
    {
        if (!Exists(name)) Write(name, "");
    }

    public void Delete(string name)
    {
        string p = GetPath(name);
        if (File.Exists(p)) File.Delete(p);
    }

    public void AddDomain(string name, string domain)
    {
        domain = domain.Trim();
        if (domain.Length == 0) return;
        var domains = Exists(name) ? ReadDomains(name) : new List<string>();
        if (!domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            domains.Add(domain);
            Write(name, string.Join('\n', domains));
        }
    }

    /// <summary>The bundled "authored" lists, kept in sync with the code below.</summary>
    public static readonly string[] BundledListNames =
    {
        "youtube", "discord", "discord-shop", "telegram",
        "tiktok", "tiktok-web", "tiktok-upload", "instagram", "facebook", "whatsapp", "whatsapp-web",
        "firefox", "exclude", "general",
    };

    /// <summary>Re-sync the bundled lists from code on EVERY launch, so domain updates reach existing
    /// installs (the user shouldn't be stuck on an old 4-host version). These are app-managed;
    /// user-created lists and the "proxy" list are never touched here.</summary>
    public void SeedDefaults()
    {
        Write("youtube", string.Join('\n', DefaultYoutube));
        Write("discord", string.Join('\n', DefaultDiscord));
        Write("discord-shop", string.Join('\n', DefaultDiscordShop));
        Write("telegram", string.Join('\n', DefaultTelegram));
        Write("tiktok", string.Join('\n', DefaultTiktok));
        Write("tiktok-web", string.Join('\n', DefaultTiktokWeb));
        Write("tiktok-upload", string.Join('\n', DefaultTiktokUpload));
        Write("instagram", string.Join('\n', DefaultInstagram));
        Write("facebook", string.Join('\n', DefaultFacebook));
        Write("whatsapp", string.Join('\n', DefaultWhatsapp));
        Write("whatsapp-web", string.Join('\n', DefaultWhatsappWeb));
        Write("firefox", string.Join('\n', DefaultFirefox));
        Write("exclude",  string.Join('\n', DefaultExclude));
        Write("general",  string.Join('\n', DefaultGeneral));
        WriteIpset("telegram", string.Join('\n', DefaultTelegramIpset));
        WriteIpset("telegram-bypass", string.Join('\n', DefaultTelegramBypassIpset));
        SeedBundledIpset("discord");
        SeedYoutubeIpset();
        SeedBundledIpset("googlevideo");
        SeedBundledIpset("instagram");
        SeedBundledIpset("facebook");
        SeedBundledIpset("whatsapp");
        SeedBundledIpset("tiktok");
        SeedBundledIpset("cloudflare");
    }

    private static void SeedYoutubeIpset()
    {
        var cidrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in new[] { "ipset-youtube.txt", "ipset-googlevideo.txt" })
        {
            string bundled = Path.Combine(AppPaths.ClassicDataDir, "lists", file);
            if (!File.Exists(bundled)) continue;
            foreach (string line in File.ReadAllLines(bundled))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith('#'))
                    cidrs.Add(t);
            }
        }

        if (cidrs.Count == 0)
        {
            SeedBundledIpset("youtube");
            return;
        }

        string merged = string.Join('\n', cidrs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(AppPaths.IpsetFile("youtube"), NormalizeNewlines(merged));
    }

    private static void SeedBundledIpset(string name)
    {
        string bundled = Path.Combine(AppPaths.ClassicDataDir, "lists", $"ipset-{name}.txt");
        if (File.Exists(bundled))
            File.WriteAllText(AppPaths.IpsetFile(name), NormalizeNewlines(File.ReadAllText(bundled)));
    }

    private void WriteIpset(string name, string content)
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(Path.Combine(AppPaths.ListsDir, "ipset-" + name + ".txt"), NormalizeNewlines(content));
    }

    // ---- bundled default lists (synced from Flowseal/zapret-discord-youtube, июнь 2026) ----

    /// <summary>YouTube/Google domains — full set for site + player + CDN (incl. googlevideo).</summary>
    private static readonly string[] DefaultYoutube =
    [
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be", "youtubekids.com",
        "googlevideo.com", "manifest.googlevideo.com", "redirector.googlevideo.com",
        "ggpht.com", "yt3.ggpht.com", "yt4.ggpht.com",
        "ytimg.com", "ytimg.l.google.com", "gvt1.com", "gvt2.com",
        "yt3.googleusercontent.com", "lh3.googleusercontent.com", "googleusercontent.com",
        "youtube-nocookie.com", "youtube-ui.l.google.com", "wide-youtube.l.google.com",
        "yt-video-upload.l.google.com",
        "youtubeembeddedplayer.googleapis.com", "youtube.googleapis.com",
        "youtubei.googleapis.com", "jnn-pa.googleapis.com",
        "googleapis.com", "ajax.googleapis.com", "fonts.googleapis.com",
        "gstatic.com", "www.gstatic.com", "fonts.gstatic.com",
        "play.google.com", "accounts.google.com", "googleadservices.com",
        "google.com", "google.ru",
    ];

    /// <summary>Mozilla/Firefox — player scripts, addons, connectivity checks (Firefox blocks when DPI resets these).</summary>
    private static readonly string[] DefaultFirefox =
    [
        "firefox.com", "www.firefox.com", "mozilla.org", "www.mozilla.org", "mozilla.net", "mozilla.com",
        "addons.mozilla.org", "aus5.mozilla.org", "versioncheck-bg.addons.mozilla.org",
        "detectportal.firefox.com", "push.services.mozilla.com", "normandy.cdn.mozilla.net",
        "firefox.settings.services.mozilla.com", "content-signature-2.cdn.mozilla.net",
    ];

    /// <summary>Full Discord domain set (Flowseal list-general.txt, Discord entries).
    /// zapret matches subdomains, so the base domains are enough.</summary>
    private static readonly string[] DefaultDiscord =
    {
        "dis.gd", "discord.com", "www.discord.com", "discord.gg", "gateway.discord.gg",
        "remote-auth-gateway.discord.gg", "discord.media", "latency.discord.media", "discord.app", "discord.co",
        "discord.dev", "discord.design", "discord.gift", "discord.gifts",
        "discord.new", "discord.store", "discord.status",
        "discordapp.com", "discordapp.net", "cdn.discordapp.com", "media.discordapp.net",
        "images-ext-1.discordapp.net", "status.discord.com",
        "discordcdn.com", "discordstatus.com",
        "discordmerch.com", "discord-activities.com", "discordactivities.com",
        "discordsays.com", "discordsez.com", "discordpartygames.com",
        "discord-attachments-uploads-prd.storage.googleapis.com",
        // Cloudflare Turnstile widget — Discord's login bot-challenge loads from here
        // (challenges.cloudflare.com). In allow-list mode it wasn't desynced by anything, so the
        // challenge couldn't render → login stuck on net::ERR_CONNECTION_RESET. Ride the Discord desync.
        "challenges.cloudflare.com",
        "discordapp.io", "discord.st", "dis.gd",
    };

    /// <summary>Discord Shop / Nitro / billing checkout (Stripe, PayPal, Google Pay).
    /// Separate list so payment CDNs get instant first-packet desync without widening the main discord list.</summary>
    private static readonly string[] DefaultDiscordShop =
    {
        "discord.store", "discord.gift", "discord.gifts", "discordmerch.com",
        "stripe.com", "js.stripe.com", "api.stripe.com", "checkout.stripe.com",
        "m.stripe.com", "hooks.stripe.com", "r.stripe.com", "q.stripe.com",
        "b.stripe.com", "merchant-ui-api.stripe.com",
        "paypal.com", "www.paypal.com", "pay.google.com",
        "secure.xsolla.com", "api.xsolla.com", "static.xsolla.com",
        "challenges.cloudflare.com", "cloudflare-ech.com",
    };

    /// <summary>Telegram-owned SNI domains (curated). zapret matches subdomains, so apex covers
    /// all *.telegram.org. NOTE: this only helps the SNI/web parts (web client, site, login,
    /// telegra.ph, HTTPS CDN). The desktop/mobile app's media goes over MTProto to DC IPs (no SNI).</summary>
    private static readonly string[] DefaultTelegram =
    {
        "telegram.org", "web.telegram.org", "pluto.web.telegram.org",
        "zws1.web.telegram.org", "zws2.web.telegram.org", "zws2-1.web.telegram.org",
        "zws3.web.telegram.org", "zws4.web.telegram.org", "zws4-1.web.telegram.org",
        "zws5.web.telegram.org",
        "kws2.web.telegram.org", "kws2-1.web.telegram.org",
        "kws4.web.telegram.org", "kws4-1.web.telegram.org",
        "venus.web.telegram.org", "venus-1.web.telegram.org",
        "vesta.web.telegram.org", "vesta-1.web.telegram.org",
        "api.telegram.org", "td.telegram.org", "cdn.telegram.org", "k.telegram.org",
        "t.me", "telegram.me", "tx.me", "teleg.xyz",
        "telegra.ph", "graph.org", "telesco.pe", "comments.app",
        "fragment.com", "contest.com", "quiz.directory",
        "tg.dev", "tg.org", "tgram.org", "torg.org", "telegramapp.org",
        "cdn-telegram.org", "telegram-cdn.org", "tdesktop.com",
        "telegram.space", "telega.one", "telegram.dog", "telegramusercontent.com",
    };

    /// <summary>TikTok video feed + CDN (ByteDance).</summary>
    private static readonly string[] DefaultTiktok =
    {
        "tiktok.com", "www.tiktok.com", "m.tiktok.com", "vm.tiktok.com",
        "tiktokv.com", "tiktokcdn.com", "muscdn.com", "musical.ly",
        "byteoversea.com", "ibytedtos.com", "ibyteimg.com", "ttwstatic.com",
        "tiktokv.eu", "tiktokw.eu", "tiktokcdn-eu.com", "tiktokglobalshop.com",
        "mon.tiktokv.com", "libraweb.tiktok.com", "login-no1a.www.tiktok.com",
        "sf16-website-login.neutral.ttwstatic.com", "sf16-website.neutral.ttwstatic.com",
        "v16-webapp-prime.tiktok.com", "v19-webapp-prime.tiktok.com",
        "p16-tiktokcdn-com.akamaized.net", "challenges.cloudflare.com",
    };

    /// <summary>TikTok web app — instant bypass target (browser).</summary>
    private static readonly string[] DefaultTiktokWeb =
    {
        "www.tiktok.com", "tiktok.com", "m.tiktok.com",
        "libraweb.tiktok.com", "ttwstatic.com",
        "sf16-website-login.neutral.ttwstatic.com", "sf16-website.neutral.ttwstatic.com",
        "mon.tiktokv.com", "ibytedtos.com", "ibyteimg.com",
        "challenges.cloudflare.com",
    };

    /// <summary>TikTok Studio / web upload (v16-up, api16-va, open-upload).</summary>
    private static readonly string[] DefaultTiktokUpload =
    {
        "tiktokv.com", "api.tiktokv.com", "api16-va.tiktokv.com", "api19-va.tiktokv.com",
        "api-h2.tiktokv.com", "api-core-va.tiktokv.com", "api2-16-h2.musical.ly",
        "v16.tiktokv.com", "v16-up.tiktokv.com", "v19.tiktokv.com", "v16.tiktokcdn.com",
        "v19.tiktokcdn.com", "sf16-upload.tiktokcdn.com", "p16-tiktokcdn-com.akamaized.net",
        "open-upload.tiktokapis.com", "open.tiktokapis.com", "tiktokapis.com",
        "va-tiktok.byteoversea.com", "abtest-va-tiktok.byteoversea.com",
        "v16-up.amemv.com", "amemv.com", "log.tiktokv.com", "mon.tiktokv.com",
        "gecko-va.tiktokv.com", "dm16.tiktokv.com",
    };

    /// <summary>Instagram + Meta login/CDN.</summary>
    private static readonly string[] DefaultInstagram =
    {
        "instagram.com", "www.instagram.com", "cdninstagram.com", "ig.me",
        "i.instagram.com", "graph.instagram.com", "help.instagram.com",
    };

    /// <summary>Facebook / Meta login (WhatsApp Web QR).</summary>
    private static readonly string[] DefaultFacebook =
    {
        "facebook.com", "www.facebook.com", "fbcdn.net", "fb.com", "fbsbx.com", "fburl.com",
        "graph.facebook.com", "connect.facebook.net", "m.facebook.com",
        "edge-chat.facebook.com", "star.fallback.c10r.facebook.com",
        "challenges.cloudflare.com",
    };

    /// <summary>WhatsApp web + mobile API.</summary>
    private static readonly string[] DefaultWhatsapp =
    {
        "whatsapp.com", "www.whatsapp.com", "web.whatsapp.com", "api.whatsapp.com",
        "whatsapp.net", "whatsapp.co", "wa.me", "wl.co", "whatsappbrand.com",
        "static.whatsapp.net", "mmg.whatsapp.net", "g.whatsapp.net", "v.whatsapp.net",
        "dyn.web.whatsapp.com", "graph.whatsapp.com", "pps.whatsapp.net",
        "media-fra3-1.cdn.whatsapp.net", "media-ams2-1.cdn.whatsapp.net",
        "media-fra3-2.cdn.whatsapp.net", "media-lhr6-1.cdn.whatsapp.net",
        "media-lhr8-1.cdn.whatsapp.net", "media-sin6-1.cdn.whatsapp.net",
        "challenges.cloudflare.com",
    };

    /// <summary>WhatsApp Web client — QR, websocket, static (NSDI/DNS poison fix via hosts+winws).</summary>
    private static readonly string[] DefaultWhatsappWeb =
    {
        "web.whatsapp.com", "www.web.whatsapp.com", "www.whatsapp.com",
        "static.whatsapp.net", "mmg.whatsapp.net", "g.whatsapp.net", "v.whatsapp.net",
        "dyn.web.whatsapp.com", "graph.whatsapp.com", "pps.whatsapp.net",
        "graph.facebook.com", "connect.facebook.net", "m.facebook.com",
        "edge-chat.facebook.com", "star.fallback.c10r.facebook.com",
        "challenges.cloudflare.com",
    };

    /// <summary>Official Telegram DC CIDRs (core.telegram.org/resources/cidr.txt) + IPv6.</summary>
    private static readonly string[] DefaultTelegramIpset =
    {
        "91.108.4.0/22",
        "91.108.8.0/22",
        "91.108.12.0/22",
        "91.108.16.0/22",
        "91.108.20.0/22",
        "91.108.32.0/20",
        "91.108.56.0/22",
        "91.105.192.0/23",
        "149.154.160.0/20",
        "149.154.175.0/24",
        "149.154.174.0/24",
        "149.154.167.51/32",
        "149.154.175.50/32",
        "91.108.56.130/32",
        "185.76.151.0/24",
        "2001:b28:f23d::/48",
        "2001:b28:f23f::/48",
        "2001:67c:4e8::/48",
        "2001:b28:f23c::/48",
        "2a0a:f280::/32",
    };

    /// <summary>High-priority DC subnets (often hit first by desktop clients).</summary>
    private static readonly string[] DefaultTelegramBypassIpset =
    {
        "91.108.56.0/22",
        "91.108.16.0/22",
        "149.154.175.0/24",
        "91.108.20.0/22",
        "149.154.174.0/24",
        "91.105.192.0/23",
    };

    /// <summary>General "everything else worth bypassing" domains (Flowseal list-general.txt, the
    /// non-Discord part): Cloudflare ECH/edge, Twitch ecosystem (BTTV/FFZ/7TV), CDNs. The catch-all
    /// profile already covers unknown SNIs, so this list is a reference users can attach explicitly.</summary>
    private static readonly string[] DefaultGeneral =
    {
        "cloudflare-ech.com", "encryptedsni.com", "cloudflareaccess.com", "cloudflareapps.com",
        "cloudflarebolt.com", "cloudflareclient.com", "cloudflareinsights.com", "cloudflareok.com",
        "cloudflarepartners.com", "cloudflareportal.com", "cloudflarepreview.com", "cloudflareresolve.com",
        "cloudflaressl.com", "cloudflarestatus.com", "cloudflarestorage.com", "cloudflarestream.com",
        "cloudflaretest.com", "cloudfront.net",
        "frankerfacez.com", "ffzap.com", "betterttv.net", "7tv.app", "7tv.io",
        "localizeapi.com", "klipy.com",
    };

    /// <summary>Domains that must NEVER be desynced (Flowseal list-exclude.txt) — banks, gov,
    /// big RU services, Microsoft/Steam/Riot/Epic, etc. Wired into catch-all profiles via
    /// --hostlist-exclude so a broad fallback strategy can't break them.</summary>
    private static readonly string[] DefaultExclude =
    {
        "pusher.com", "live-video.net", "ttvnw.net", "twitch.tv", "mail.ru", "citilink.ru",
        "yandex.com", "yandex.net", "yandex.org", "yandex.md", "yandex.ru", "yandexadexchange.net",
        "yandexcloud.net", "yandexcom.net", "yandexmetrica.com", "yandexwebcache.net",
        "yandexwebcache.org", "yastat.net", "yastatic-net.ru", "yastatic.net", "ya.ru",
        "adfox.ru", "admetrica.ru", "naydex.net", "rostaxi.org", "turbopages.org", "webvisor.com",
        "webvisor.org", "nvidia.com", "donationalerts.com", "vk.com", "yandex.kz", "mts.ru",
        "multimc.org", "dns-shop.ru", "habr.com", "3dnews.ru", "microsoft.com", "microsoftonline.com",
        "live.com", "sharepoint.com", "minecraft.net", "xboxlive.com", "akamaitechnologies.com",
        "msi.com", "2ip.ru", "boosty.to", "tanki.su", "lesta.ru", "korabli.su", "tanksblitz.ru",
        "reg.ru", "epicgames.dev", "epicgames.com", "unrealengine.com", "riotgames.com", "riotcdn.net",
        "leagueoflegends.com", "playvalorant.com", "marketplace.visualstudio.com", "gallery.vsassets.io",
        "gallerycdn.vsassets.io", "gosuslugi.ru", "gov.ru", "nalog.ru", "spb.ru", "mos.ru", "vk.ru",
        "vk.me", "vkvideo.ru", "ok.ru", "mycdn.me", "okcdn.ru", "odkl.ru", "wb.ru", "geobasket.ru",
        "paywb.com", "rwb.ru", "wb-basket.ru", "wbbasket.ru", "wbpay.ru", "wibes.ru", "wildberries.ru",
        "ozon.by", "ozon.com", "ozon.com.by", "ozon.com.kz", "ozon.kz", "ozon.ru", "ozon.tm",
        "ozone.ru", "ozonru.me", "ozonusercontent.com", "alfabank.ru", "gazprombank.ru", "gpb.ru",
        "dbo-dengi.online", "mtsdengi.ru", "psbank.ru", "bankline.ru", "rosbank.ru", "abr.ru",
        "rshb.ru", "sber.ru", "sberbank.com", "sberbank.ru", "cdn-tinkoff.ru", "tbank-online.com",
        "tbank.ru", "t-bank-app.ru", "tochka-tech.com", "tochka.com", "vtb.ru", "steamcommunity.com",
        // --- Game services (extended): the game filter is port-based, but these domains are also
        //     excluded so a catch-all desync never touches game logins/CDNs even with the filter ON.
        "steampowered.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com",
        "steamserver.net", "valvesoftware.com", "steamgames.com",
        "ea.com", "eaassets-a.akamaihd.net", "origin.com", "dice.se",
        "battle.net", "battlenet.com.cn", "blizzard.com", "blz-contentstack.com",
        "ubisoft.com", "ubi.com", "ubisoftconnect.com",
        "rockstargames.com", "socialclub.rockstargames.com",
        "playstation.com", "playstation.net", "sonyentertainmentnetwork.com",
        "xbox.com", "nintendo.com", "nintendo.net", "nintendowifi.net",
        "gog.com", "gog-statics.com", "mojang.com",
        "wargaming.net", "faceit.com", "supercell.com",
        "hoyoverse.com", "mihoyo.com", "yuanshen.com",
        // --- Xbox / Microsoft sign-in (fixes "infinite Xbox login" e.g. in Forza). The token
        //     endpoints live under *.xboxlive.com / live.com / microsoftonline.com (already above),
        //     but the embedded sign-in web-view loads its assets from these CDNs — if the catch-all
        //     desyncs their TLS the login page never finishes and spins forever.
        "xboxservices.com", "msftauth.net", "msauth.net", "msftauthimages.net", "msauthimages.net",
        "microsoftonline-p.com", "s-microsoft.com", "login.windows.net", "gamepass.com",
        // --- Forza + Azure PlayFab (Forza Motorsport/Horizon backend & matchmaking).
        "forzamotorsport.net", "forzaracing.com", "playfab.com", "playfabapi.com",
        // --- Anti-cheat: mangled TLS here blocks game LAUNCH (not just login), so never desync.
        "easyanticheat.net", "eac-cdn.com", "battleye.com",
        // --- More launchers / publishers / online backends (auth, CDN, matchmaking) for the future.
        "fortnite.com", "activision.com", "callofduty.com", "demonware.net",
        "roblox.com", "rbxcdn.com", "2k.com", "take2games.com",
        "bethesda.net", "bethesda.com", "zenimax.com", "easports.com",
        "square-enix.com", "finalfantasyxiv.com", "bungie.net", "minecraftservices.com",
        "pubg.com", "krafton.com", "garena.com", "levelinfinite.com",
        "gaijin.net", "warthunder.com", "vkplay.ru", "hoyolab.com", "nvidiagrid.net",
        // --- Riot full set (LoL/TFT/LoR/Wild Rift): pvp.net is the League login/chat backend.
        "pvp.net", "teamfighttactics.com", "legendsofruneterra.com", "wildrift.com",
        // --- NetEase (Marvel Rivals, Naraka; easebar = NetEase anti-cheat) + Kuro (Wuthering Waves).
        "netease.com", "neteasegames.com", "easebar.com", "marvelrivals.com",
        "kurogames.com", "kurogame.com",
        // --- Other popular online games / publishers.
        "warframe.com", "digitalextremes.com", "pathofexile.com",
        "amazongames.com", "playlostark.com", "deadbydaylight.com", "bhvr.com",
        "escapefromtarkov.com", "battlestategames.com", "halowaypoint.com",
        "jagex.com", "arena.net", "guildwars2.com", "ncsoft.com",
        "nexon.com", "nexon.net", "pearlabyss.com", "embark-studios.com",
        "moonton.com", "mobilelegends.com",
        // --- Anti-cheat (mangled TLS blocks game launch): Denuvo, Wellbia (Xigncode3), GameGuard.
        "denuvo.com", "wellbia.com", "nprotect.com",
        // --- Third-party multiplayer backends / netcode: one platform powers MANY indie & AAA
        //     games (most don't run their own servers), so a single mangled host breaks multiplayer
        //     everywhere. EOS (epicgames.dev) + Azure PlayFab (playfab*) are already covered above.
        "photonengine.com", "exitgames.com",       // Photon — the biggest Unity/UE netcode SaaS
        "unity.com", "unity3d.com", "vivox.com",    // Unity Gaming Services + Vivox in-game voice
        "heroiclabs.com", "accelbyte.io",           // Nakama / AccelByte backends
        // --- Dedicated game-server hosting / orchestration (rented community servers).
        "nitrado.net", "gamefabric.com", "g-portal.com", "i3d.net", "edgegap.com",
    };

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');
}
