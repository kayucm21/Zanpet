using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ZapretUI.Services;

/// <summary>Find Telegram Desktop executable path (running process, registry, common folders).</summary>
internal static class TelegramLocator
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public sealed record LocateResult(string? ExePath, bool ProcessRunning, int ProcessCount);

    public static LocateResult Locate()
    {
        var procs = Process.GetProcessesByName("Telegram");
        int count = procs.Length;
        string? fromProcess = null;

        foreach (var p in procs)
        {
            try
            {
                string? path = TryGetProcessPath(p.Id) ?? TryMainModule(p);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    fromProcess = path;
                    break;
                }
            }
            catch { /* best-effort */ }
            finally { p.Dispose(); }
        }

        if (fromProcess is not null)
            return new LocateResult(fromProcess, count > 0, count);

        foreach (var path in EnumerateCandidatePaths())
        {
            if (File.Exists(path))
                return new LocateResult(path, count > 0, count);
        }

        return new LocateResult(null, count > 0, count);
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Telegram Desktop", "Telegram.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telegram Desktop", "Telegram.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Telegram", "Telegram.exe");

        foreach (var root in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
        {
            yield return Path.Combine(root, "Telegram Desktop", "Telegram.exe");
            yield return Path.Combine(root, "Telegram", "Telegram.exe");
        }

        yield return Path.Combine(AppPaths.TempDir, "Telegram", "Telegram.exe");

        foreach (var path in FromUninstallRegistry())
            yield return path;
    }

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
                        if (name is null || !name.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (sub.GetValue("InstallLocation") is string dir && !string.IsNullOrWhiteSpace(dir))
                            results.Add(Path.Combine(dir.TrimEnd('\\', '/'), "Telegram.exe"));

                        if (sub.GetValue("DisplayIcon") is string icon
                            && icon.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
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
            if (!QueryFullProcessImageName(handle, 0, sb, ref size))
                return null;
            return sb.ToString();
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
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
