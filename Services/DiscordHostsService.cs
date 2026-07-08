using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Temporary Windows hosts entries for Discord web + desktop login (discord.com).
/// ISPs often return poisoned/blocked DNS; pinning to a Cloudflare edge IP is a common fix.
/// </summary>
public sealed class DiscordHostsService
{
    private const string BeginMarker = "# BEGIN ZAPRETUI DISCORD";
    private const string EndMarker = "# END ZAPRETUI DISCORD";

    /// <summary>Cloudflare edge — from Flowseal ipset-discord / discord.com A-record.</summary>
    private const string EdgeIp = "162.159.128.233";

    private static readonly string[] Domains =
    {
        "discord.com", "www.discord.com", "discordapp.com", "status.discord.com",
        "gateway.discord.gg", "updates.discord.com",
        "cdn.discordapp.com", "media.discordapp.net", "images-ext-1.discordapp.net",
        "discord.media", "latency.discord.media",
        "challenges.cloudflare.com",
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
            foreach (var d in Domains)
                block.AppendLine($"{EdgeIp} {d}");
            block.AppendLine(EndMarker);
            File.WriteAllText(HostsPath, text.TrimEnd() + block.ToString());
            FlushDns();
            IsApplied = true;
            Emit($"Discord: hosts → {EdgeIp} ({Domains.Length} доменов)");
            return true;
        }
        catch (Exception ex)
        {
            Emit($"Discord hosts: {ex.Message} (запустите от администратора)");
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
            Emit($"Discord hosts (удаление): {ex.Message}");
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
