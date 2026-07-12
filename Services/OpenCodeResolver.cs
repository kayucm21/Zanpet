using System.IO;

namespace ZapretUI.Services;

/// <summary>Finds opencode.exe on Windows (PATH, npm, scoop, bundled install).</summary>
public static class OpenCodeResolver
{
    public static string InstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZapretUI", "opencode");

    public static string BundledExePath => Path.Combine(InstallDir, "opencode.exe");

    public static string? Resolve()
    {
        if (File.Exists(BundledExePath))
            return BundledExePath;

        string classicBundled = Path.Combine(AppPaths.ClassicDataDir, "exe", "opencode.exe");
        if (File.Exists(classicBundled))
            return classicBundled;

        string? fromPath = FindOnPath("opencode");
        if (fromPath is not null)
            return fromPath;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] extra =
        [
            Path.Combine(appData, "npm", "opencode.cmd"),
            Path.Combine(home, ".local", "bin", "opencode.exe"),
            Path.Combine(home, "scoop", "shims", "opencode.exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "opencode.exe"),
            Path.Combine(localAppData, "Programs", "opencode", "opencode.exe"),
        ];

        foreach (string path in extra)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (trimmed.Length == 0) continue;

            foreach (string candidate in new[]
                     {
                         Path.Combine(trimmed, name),
                         Path.Combine(trimmed, name + ".exe"),
                         Path.Combine(trimmed, name + ".cmd"),
                         Path.Combine(trimmed, name + ".bat"),
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
