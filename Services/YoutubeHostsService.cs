using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Windows hosts pins for YouTube (web + Firefox). ISPs poison Google DNS — Firefox DoH still
/// fails for youtubei/googlevideo while Telegram hosts fix works the same way.
/// </summary>
public sealed class YoutubeHostsService
{
    private const string BeginMarker = "# BEGIN ZAPRETUI YOUTUBE";
    private const string EndMarker = "# END ZAPRETUI YOUTUBE";

    /// <summary>Google edge IPs — same approach as Discord/Telegram hosts bridge.</summary>
    private static readonly (string Ip, string[] Domains)[] Entries =
    [
        ("142.250.191.78", new[]
        {
            "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
            "youtu.be", "youtubekids.com", "youtube-nocookie.com",
            "youtube-ui.l.google.com", "wide-youtube.l.google.com",
        }),
        ("142.250.185.206", new[]
        {
            "youtubei.googleapis.com", "youtube.googleapis.com",
            "youtubeembeddedplayer.googleapis.com", "jnn-pa.googleapis.com",
            "googleapis.com", "ajax.googleapis.com", "fonts.googleapis.com",
        }),
        ("142.250.185.78", new[]
        {
            "googlevideo.com", "redirector.googlevideo.com", "manifest.googlevideo.com",
        }),
        ("142.250.185.14", new[]
        {
            "ytimg.com", "i.ytimg.com", "s.ytimg.com", "ytimg.l.google.com",
            "ggpht.com", "yt3.ggpht.com", "yt4.ggpht.com",
        }),
        ("142.250.191.14", new[]
        {
            "gstatic.com", "www.gstatic.com", "ssl.gstatic.com", "fonts.gstatic.com",
        }),
        ("142.250.185.110", new[]
        {
            "accounts.google.com", "play.google.com", "google.com", "www.google.com",
        }),
    ];

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public event Action<string>? LogLine;

    public bool IsApplied { get; private set; }

    public bool Apply()
    {
        if (IsApplied) return true;
        try
        {
            string text = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            text = StripBlock(text);
            var block = new StringBuilder();
            block.AppendLine();
            block.AppendLine(BeginMarker);
            int count = 0;
            foreach (var (ip, domains) in Entries)
            {
                foreach (var d in domains)
                {
                    block.AppendLine($"{ip} {d}");
                    count++;
                }
            }
            block.AppendLine(EndMarker);
            File.WriteAllText(HostsPath, text.TrimEnd() + block.ToString());
            FlushDns();
            IsApplied = true;
            Emit($"YouTube: hosts ({count} доменов — Firefox/браузер, как Telegram)");
            return true;
        }
        catch (Exception ex)
        {
            Emit($"YouTube hosts: {ex.Message} (запустите от администратора)");
            return false;
        }
    }

    public void Remove()
    {
        if (!IsApplied) return;
        try
        {
            if (File.Exists(HostsPath))
            {
                string text = StripBlock(File.ReadAllText(HostsPath));
                File.WriteAllText(HostsPath, text.TrimEnd() + Environment.NewLine);
            }
            FlushDns();
        }
        catch (Exception ex)
        {
            Emit($"YouTube hosts (удаление): {ex.Message}");
        }
        finally
        {
            IsApplied = false;
        }
    }

    private static string StripBlock(string text)
    {
        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;
        int end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return text[..begin];
        end += EndMarker.Length;
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
