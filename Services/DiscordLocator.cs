using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ZapretUI.Services;

/// <summary>Find Discord Desktop (Stable / Canary / PTB) — Update.exe and Discord.exe.</summary>
internal static class DiscordLocator
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public enum Flavor { Stable, Canary, Ptb }

    public sealed record LocateResult(
        string? DiscordExe,
        string? UpdateExe,
        Flavor Flavor,
        bool ProcessRunning,
        int ProcessCount,
        string? AppDataDir);

    public static LocateResult Locate()
    {
        var procs = Process.GetProcessesByName("Discord");
        int count = procs.Length;
        string? fromProcess = null;
        Flavor flavor = Flavor.Stable;

        foreach (var p in procs)
        {
            try
            {
                string? path = TryGetProcessPath(p.Id) ?? TryMainModule(p);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                fromProcess = path;
                flavor = GuessFlavor(path);
                break;
            }
            catch { /* best-effort */ }
            finally { p.Dispose(); }
        }

        if (fromProcess is not null)
        {
            string? update = FindUpdateExe(Path.GetDirectoryName(fromProcess)!, flavor);
            return new LocateResult(fromProcess, update, flavor, count > 0, count, AppDataFor(flavor));
        }

        foreach (var candidate in EnumerateInstallations())
        {
            if (File.Exists(candidate.DiscordExe))
                return new LocateResult(candidate.DiscordExe, candidate.UpdateExe, candidate.Flavor,
                    count > 0, count, candidate.AppDataDir);
        }

        return new LocateResult(null, null, Flavor.Stable, count > 0, count, null);
    }

    private static IEnumerable<(string DiscordExe, string? UpdateExe, Flavor Flavor, string AppDataDir)> EnumerateInstallations()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var (folder, flavor, appDataName) in new[]
        {
            ("Discord", Flavor.Stable, "discord"),
            ("DiscordCanary", Flavor.Canary, "discordcanary"),
            ("DiscordPTB", Flavor.Ptb, "discordptb"),
        })
        {
            string root = Path.Combine(local, folder);
            string? discord = FindNewestAppExe(root);
            string? update = File.Exists(Path.Combine(root, "Update.exe"))
                ? Path.Combine(root, "Update.exe")
                : null;
            if (discord is not null || update is not null)
            {
                yield return (discord ?? "", update, flavor, Path.Combine(roaming, appDataName));
            }
        }

        foreach (var root in new[] { @"C:\Program Files\Discord", @"C:\Program Files (x86)\Discord" })
        {
            string? discord = FindNewestAppExe(root) ?? TryFile(Path.Combine(root, "Discord.exe"));
            if (discord is not null)
                yield return (discord, null, Flavor.Stable, Path.Combine(roaming, "discord"));
        }

        foreach (var path in FromUninstallRegistry())
        {
            string? discord = File.Exists(path) ? path : FindNewestAppExe(Path.GetDirectoryName(path)!);
            if (discord is not null)
                yield return (discord, null, Flavor.Stable, Path.Combine(roaming, "discord"));
        }
    }

    private static string? FindNewestAppExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        string? best = null;
        string? bestVer = null;
        foreach (var dir in Directory.EnumerateDirectories(root, "app-*"))
        {
            string ver = Path.GetFileName(dir).AsSpan(4).ToString();
            if (bestVer is null || string.CompareOrdinal(ver, bestVer) > 0)
            {
                string exe = Path.Combine(dir, "Discord.exe");
                if (File.Exists(exe))
                {
                    best = exe;
                    bestVer = ver;
                }
            }
        }
        return best;
    }

    private static string? FindUpdateExe(string discordDir, Flavor flavor)
    {
        while (!string.IsNullOrEmpty(discordDir))
        {
            string update = Path.Combine(discordDir, "Update.exe");
            if (File.Exists(update)) return update;
            var parent = Directory.GetParent(discordDir);
            if (parent is null) break;
            discordDir = parent.FullName;
            if (discordDir.EndsWith("Discord", StringComparison.OrdinalIgnoreCase)
                || discordDir.EndsWith("DiscordCanary", StringComparison.OrdinalIgnoreCase)
                || discordDir.EndsWith("DiscordPTB", StringComparison.OrdinalIgnoreCase))
                continue;
            break;
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = flavor switch
        {
            Flavor.Canary => "DiscordCanary",
            Flavor.Ptb => "DiscordPTB",
            _ => "Discord",
        };
        string fallback = Path.Combine(local, folder, "Update.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static Flavor GuessFlavor(string exePath)
    {
        if (exePath.Contains("canary", StringComparison.OrdinalIgnoreCase)) return Flavor.Canary;
        if (exePath.Contains("ptb", StringComparison.OrdinalIgnoreCase)) return Flavor.Ptb;
        return Flavor.Stable;
    }

    private static string AppDataFor(Flavor flavor) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            flavor switch
            {
                Flavor.Canary => "discordcanary",
                Flavor.Ptb => "discordptb",
                _ => "discord",
            });

    private static string? TryFile(string path) => File.Exists(path) ? path : null;

    private static IEnumerable<string> FromUninstallRegistry()
    {
        var results = new List<string>();
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var root in roots)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    if (key is null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub is null) continue;
                        string? name = sub.GetValue("DisplayName") as string;
                        if (name is null || !name.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (sub.GetValue("InstallLocation") is string dir && !string.IsNullOrWhiteSpace(dir))
                        {
                            string exe = Path.Combine(dir.TrimEnd('\\', '/'), "Discord.exe");
                            results.Add(exe);
                            string? nested = FindNewestAppExe(dir);
                            if (nested is not null) results.Add(nested);
                        }

                        if (sub.GetValue("DisplayIcon") is string icon
                            && icon.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                        {
                            string cleaned = icon.Split(',')[0].Trim('"');
                            if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                results.Add(cleaned);
                        }
                    }
                }
                catch { /* best-effort */ }
            }
        }

        return results;
    }

    private static string? TryGetProcessPath(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size)) return null;
            return sb.ToString();
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    private static string? TryMainModule(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
