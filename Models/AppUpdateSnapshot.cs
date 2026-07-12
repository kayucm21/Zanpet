using System.Text.RegularExpressions;
using ZapretUI.Services;

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
                else if (a == b)
                {
                    if (r.Build > best.Build) best = r;
                    else if (r.Build == best.Build && r.Source == AppReleaseSource.Ftp) best = r;
                }
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
            if (!Version.TryParse(CurrentVersion, out var cur)) return false;
            var latest = ParseVersion(newest.Tag);
            if (latest is null) return false;
            if (latest > cur) return true;
            if (latest == cur && newest.Build > AppUpdateSnapshot.CurrentBuild) return true;
            return false;
        }
    }

    public static int CurrentBuild => UpdaterService.AppBuild;

    public int InstalledBuild => CurrentBuild;

    public string FtpDisplay => Ftp is null ? "недоступен" : $"v{Ftp.Tag}";
    public string GitHubDisplay => GitHub is null ? "недоступен" : $"v{GitHub.Tag}";

    private static Version? ParseVersion(string? tag)
    {
        var m = Regex.Match(tag ?? "", @"\d+(?:\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }
}
