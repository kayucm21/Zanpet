using System.Text.RegularExpressions;

namespace ZapretUI.Models;

/// <summary>App update check: current install vs FTP and GitHub mirrors.</summary>
public sealed record AppUpdateSnapshot(
    string CurrentVersion,
    AppReleaseInfo? Ftp,
    AppReleaseInfo? GitHub)
{
    public AppReleaseInfo? NewestRelease
    {
        get
        {
            AppReleaseInfo? best = null;
            foreach (var r in new[] { Ftp, GitHub })
            {
                if (r is null) continue;
                if (best is null) { best = r; continue; }
                var a = ParseVersion(r.Tag);
                var b = ParseVersion(best.Tag);
                if (a is null) continue;
                if (b is null || a > b) best = r;
                else if (a == b && r.Source == AppReleaseSource.Ftp) best = r;
            }
            return best;
        }
    }

    public bool HasUpdate
    {
        get
        {
            var newest = NewestRelease;
            if (newest is null) return false;
            var latest = ParseVersion(newest.Tag);
            return latest is not null && Version.TryParse(CurrentVersion, out var cur) && latest > cur;
        }
    }

    public string FtpDisplay => Ftp is null ? "недоступен" : $"v{Ftp.Tag}";
    public string GitHubDisplay => GitHub is null ? "недоступен" : $"v{GitHub.Tag}";

    private static Version? ParseVersion(string? tag)
    {
        var m = Regex.Match(tag ?? "", @"\d+(?:\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }
}
