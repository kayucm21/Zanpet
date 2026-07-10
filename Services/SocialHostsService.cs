using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Windows hosts pins for TikTok / Instagram / WhatsApp (Telegram/Discord-style DNS bridge).
/// </summary>
public sealed class SocialHostsService
{
    private const string TikTokBegin = "# BEGIN ZAPRETUI TIKTOK";
    private const string TikTokEnd = "# END ZAPRETUI TIKTOK";
    private const string InstagramBegin = "# BEGIN ZAPRETUI INSTAGRAM";
    private const string InstagramEnd = "# END ZAPRETUI INSTAGRAM";
    private const string WhatsAppBegin = "# BEGIN ZAPRETUI WHATSAPP";
    private const string WhatsAppEnd = "# END ZAPRETUI WHATSAPP";

    private static readonly (string Ip, string[] Domains)[] TikTokEntries =
    [
        ("13.107.42.14", new[]
        {
            "www.tiktok.com", "tiktok.com", "m.tiktok.com", "vm.tiktok.com",
            "libraweb.tiktok.com", "login-no1a.www.tiktok.com",
        }),
        ("161.117.70.136", new[]
        {
            "api.tiktokv.com", "api16-va.tiktokv.com", "api19-va.tiktokv.com",
            "api-h2.tiktokv.com", "api-core-va.tiktokv.com", "v16.tiktokv.com", "v16-up.tiktokv.com",
            "v19.tiktokv.com", "v16.tiktokcdn.com", "v19.tiktokcdn.com",
            "sf16-upload.tiktokcdn.com", "p16-tiktokcdn-com.akamaized.net",
            "open-upload.tiktokapis.com", "open.tiktokapis.com",
            "log.tiktokv.com", "mon.tiktokv.com", "gecko-va.tiktokv.com", "dm16.tiktokv.com",
        }),
        ("205.251.194.210", new[]
        {
            "tiktokv.com", "byteoversea.com", "va-tiktok.byteoversea.com",
            "abtest-va-tiktok.byteoversea.com", "v16-up.amemv.com", "amemv.com",
            "ibytedtos.com", "ibyteimg.com", "ttwstatic.com",
            "sf16-website-login.neutral.ttwstatic.com", "sf16-website.neutral.ttwstatic.com",
        }),
        ("104.244.46.241", new[]
        {
            "tiktokcdn.com", "muscdn.com", "musical.ly", "mon.tiktokv.com",
        }),
    ];

    private static readonly (string Ip, string[] Domains)[] InstagramEntries =
    [
        ("157.240.241.174", new[]
        {
            "instagram.com", "www.instagram.com", "cdninstagram.com", "i.instagram.com",
            "graph.instagram.com", "ig.me",
        }),
        ("31.13.64.51", new[]
        {
            "facebook.com", "www.facebook.com", "fbcdn.net", "fb.com", "graph.facebook.com",
        }),
    ];

    // Classic zapret webchat IP 57.144.223.32 + Meta edges
    private static readonly (string Ip, string[] Domains)[] WhatsAppEntries =
    [
        ("57.144.223.32", new[]
        {
            "web.whatsapp.com", "www.web.whatsapp.com",
            "static.whatsapp.net", "mmg.whatsapp.net", "g.whatsapp.net", "v.whatsapp.net",
            "dyn.web.whatsapp.com", "graph.whatsapp.com", "pps.whatsapp.net",
            "web-fallback.whatsapp.net",
        }),
        ("158.85.224.171", new[]
        {
            "whatsapp.com", "www.whatsapp.com", "api.whatsapp.com",
            "whatsapp.net", "wa.me", "whatsappbrand.com",
        }),
        ("31.13.64.51", new[]
        {
            "graph.facebook.com", "connect.facebook.net", "www.facebook.com", "m.facebook.com",
            "edge-chat.facebook.com", "star.fallback.c10r.facebook.com", "fbcdn.net",
            "challenges.cloudflare.com",
        }),
        ("157.240.241.174", new[]
        {
            "media-fra3-1.cdn.whatsapp.net", "media-ams2-1.cdn.whatsapp.net",
            "media-fra3-2.cdn.whatsapp.net", "media-lhr6-1.cdn.whatsapp.net",
            "media-lhr8-1.cdn.whatsapp.net", "media-sin6-1.cdn.whatsapp.net",
        }),
    ];

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public bool TikTokApplied { get; private set; }
    public bool InstagramApplied { get; private set; }
    public bool WhatsAppApplied { get; private set; }

    public event Action<string>? LogLine;

    public bool ApplyTikTok()
    {
        if (TikTokApplied) return true;
        if (!ApplyBlock(TikTokBegin, TikTokEnd, TikTokEntries, out int count)) return false;
        TikTokApplied = true;
        Emit($"TikTok: hosts ({count} доменов, web + загрузка видео)");
        return true;
    }

    public bool ApplyInstagram()
    {
        if (InstagramApplied) return true;
        if (!ApplyBlock(InstagramBegin, InstagramEnd, InstagramEntries, out int count)) return false;
        InstagramApplied = true;
        Emit($"Instagram: hosts ({count} доменов)");
        return true;
    }

    public bool ApplyWhatsApp()
    {
        if (WhatsAppApplied) return true;
        if (!ApplyBlock(WhatsAppBegin, WhatsAppEnd, WhatsAppEntries, out int count)) return false;
        WhatsAppApplied = true;
        Emit($"WhatsApp: hosts ({count} доменов, web.whatsapp.com 57.144.223.32)");
        return true;
    }

    public void RemoveAll()
    {
        RemoveBlock(InstagramBegin, InstagramEnd, () => InstagramApplied = false);
        RemoveBlock(WhatsAppBegin, WhatsAppEnd, () => WhatsAppApplied = false);
        RemoveBlock(TikTokBegin, TikTokEnd, () => TikTokApplied = false);
    }

    private bool ApplyBlock(string begin, string end, (string Ip, string[] Domains)[] entries, out int count)
    {
        count = 0;
        try
        {
            string text = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            text = StripBlock(text, begin, end);
            var block = new StringBuilder();
            block.AppendLine();
            block.AppendLine(begin);
            foreach (var (ip, domains) in entries)
            {
                foreach (var d in domains)
                {
                    block.AppendLine($"{ip} {d}");
                    count++;
                }
            }
            block.AppendLine(end);
            File.WriteAllText(HostsPath, text.TrimEnd() + block.ToString());
            FlushDns();
            return true;
        }
        catch (Exception ex)
        {
            Emit($"hosts: {ex.Message} (запустите от администратора)");
            return false;
        }
    }

    private void RemoveBlock(string begin, string end, Action clearFlag)
    {
        if (!File.Exists(HostsPath)) { clearFlag(); return; }
        try
        {
            string text = StripBlock(File.ReadAllText(HostsPath), begin, end);
            File.WriteAllText(HostsPath, text.TrimEnd() + Environment.NewLine);
            FlushDns();
        }
        catch (Exception ex)
        {
            Emit($"hosts (удаление): {ex.Message}");
        }
        finally { clearFlag(); }
    }

    private static string StripBlock(string text, string beginMarker, string endMarker)
    {
        int begin = text.IndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;
        int end = text.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0) return text[..begin];
        end += endMarker.Length;
        while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
        return text[..begin] + text[end..];
    }

    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(3000);
        }
        catch { /* best-effort */ }
    }

    private void Emit(string line) => LogLine?.Invoke(line);
}
