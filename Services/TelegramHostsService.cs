using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Temporary Windows hosts entries for Telegram Web (web.telegram.org).
/// Russian ISPs often poison DNS for Telegram; redirecting web domains to a working DC IP
/// is a proven Flowseal community fix (see zapret-discord-youtube issues #5956, #11409).
/// </summary>
public sealed class TelegramHostsService
{
    private const string BeginMarker = "# BEGIN ZAPRETUI TELEGRAM";
    private const string EndMarker = "# END ZAPRETUI TELEGRAM";

    /// <summary>Amsterdam DC — commonly used for web.telegram.org WS endpoints.</summary>
    private const string WebDcIp = "149.154.167.220";

    private static readonly string[] WebDomains =
    {
        "web.telegram.org", "pluto.web.telegram.org",
        "zws1.web.telegram.org", "zws2.web.telegram.org", "zws2-1.web.telegram.org",
        "zws3.web.telegram.org", "zws4.web.telegram.org", "zws4-1.web.telegram.org",
        "zws5.web.telegram.org",
        "kws2.web.telegram.org", "kws2-1.web.telegram.org",
        "kws4.web.telegram.org", "kws4-1.web.telegram.org",
        "venus.web.telegram.org", "venus-1.web.telegram.org",
        "vesta.web.telegram.org", "vesta-1.web.telegram.org",
        "api.telegram.org", "td.telegram.org", "cdn.telegram.org",
        "telegram.org", "t.me", "telegram.me", "telegram.dog",
        "telegram.space", "telesco.pe", "tg.dev",
    };

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
            foreach (var d in WebDomains)
                block.AppendLine($"{WebDcIp} {d}");
            block.AppendLine(EndMarker);
            File.WriteAllText(HostsPath, text.TrimEnd() + block.ToString());
            FlushDns();
            IsApplied = true;
            Emit($"Telegram Web: hosts → {WebDcIp} ({WebDomains.Length} доменов)");
            return true;
        }
        catch (Exception ex)
        {
            Emit($"Telegram Web hosts: {ex.Message} (запустите от администратора)");
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
            Emit($"Telegram Web hosts (удаление): {ex.Message}");
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
