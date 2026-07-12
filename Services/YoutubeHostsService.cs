using System.Diagnostics;
using System.IO;

namespace ZapretUI.Services;

/// <summary>
/// Removes stale YouTube hosts block from v2.9.16 (pinned google.com/googlevideo — broke CDN for everyone).
/// YouTube bypass stays on winws hostlists/ipsets only; no hosts pinning.
/// </summary>
public sealed class YoutubeHostsService
{
    private const string BeginMarker = "# BEGIN ZAPRETUI YOUTUBE";
    private const string EndMarker = "# END ZAPRETUI YOUTUBE";

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public event Action<string>? LogLine;

    public bool IsApplied { get; private set; }

    /// <summary>Always strip our block — safe even if a previous session crashed.</summary>
    public bool StripStaleBlock()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            string text = File.ReadAllText(HostsPath);
            if (text.IndexOf(BeginMarker, StringComparison.Ordinal) < 0) return false;
            File.WriteAllText(HostsPath, StripBlock(text).TrimEnd() + Environment.NewLine);
            FlushDns();
            IsApplied = false;
            Emit("YouTube hosts: удалён (v2.9.16 ломал google.com и CDN)");
            return true;
        }
        catch (Exception ex)
        {
            Emit($"YouTube hosts (очистка): {ex.Message}");
            return false;
        }
    }

    public bool Apply() => false;

    public void Remove() => StripStaleBlock();

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
