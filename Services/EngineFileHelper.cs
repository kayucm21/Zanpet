using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ZapretUI.Services;

/// <summary>
/// Safe engine file install: kill orphaned winws2 before copying and never fail startup
/// when WinDivert driver files are locked by a running engine.
/// </summary>
internal static class EngineFileHelper
{
    private static readonly string[] StaleProcessNames = ["winws2", "winws"];

    public static void KillStaleEngineProcesses()
    {
        foreach (string name in StaleProcessNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { /* best-effort */ }
                finally { proc.Dispose(); }
            }
        }

        string killall = Path.Combine(AppPaths.EngineDir, "killall.exe");
        if (File.Exists(killall))
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = killall,
                    WorkingDirectory = AppPaths.EngineDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit(4000);
            }
            catch { /* optional zapret helper */ }
        }

        Thread.Sleep(350);
    }

    public static void SafeCopyFile(string source, string destination, bool overwrite = true)
    {
        if (!File.Exists(source))
            return;

        string? destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(destination) && FilesAreIdentical(source, destination))
            return;

        if (File.Exists(destination) && IsLockedDriverAsset(destination))
            return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (attempt > 0)
                    Thread.Sleep(120 * attempt);
                File.Copy(source, destination, overwrite);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                if (File.Exists(destination) && FilesAreIdentical(source, destination))
                    return;
                if (File.Exists(destination) && IsLockedDriverAsset(destination))
                    return;
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                if (File.Exists(destination) && IsLockedDriverAsset(destination))
                    return;
            }
        }

        if (File.Exists(destination))
            return;

        throw new IOException(
            $"Не удалось скопировать {Path.GetFileName(source)} в {destination}. " +
            "Закройте другие копии Zapret UI или перезагрузите ПК.");
    }

    public static void SafeCopyDirectory(string source, string dest, string pattern)
    {
        if (!Directory.Exists(source))
            return;

        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, pattern))
        {
            string destFile = Path.Combine(dest, Path.GetFileName(file));
            SafeCopyFile(file, destFile);
        }
    }

    private static bool IsLockedDriverAsset(string path)
    {
        string name = Path.GetFileName(path);
        bool driverAsset = path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WinDivert", StringComparison.OrdinalIgnoreCase);
        return driverAsset && IsFileLocked(path);
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool FilesAreIdentical(string left, string right)
    {
        try
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);
            if (a.Length != b.Length)
                return false;
            if (a.Length > 8_000_000)
                return true;
            return string.Equals(ComputeSha256(left), ComputeSha256(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
