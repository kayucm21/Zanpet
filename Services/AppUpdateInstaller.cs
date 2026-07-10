using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Reliable in-place app update for any legacy layout (2.7.x DLL, Zapret2UI, dotnet host).
/// </summary>
internal static class AppUpdateInstaller
{
    private static readonly string[] LegacyFileNames =
    [
        "ZapretUI.dll",
        "ZapretUI.deps.json",
        "ZapretUI.runtimeconfig.json",
        "ZapretUI.pdb",
        "ZapretUI_old.exe",
        "Zapret2UI.exe",
        "Zapret2UI.dll",
        "Zanpet.exe",
        "ZanpetUI.exe",
        "Zanpet.dll",
    ];

    private static readonly string[] LegacyExeNames =
    [
        "Zapret2UI.exe",
        "Zanpet.exe",
        "ZanpetUI.exe",
    ];

    public static string UpdateLogPath => Path.Combine(AppPaths.LogsDir, "update.log");

    /// <summary>Folder where the running app loads files from (works for dotnet + self-contained).</summary>
    public static string GetInstallDirectory()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string GetTargetExePath(string installDir) =>
        Path.Combine(installDir, "ZapretUI.exe");

    public static string? GetCurrentExePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string sibling = Path.Combine(GetInstallDirectory(), "ZapretUI.exe");
            return File.Exists(sibling) ? sibling : null;
        }

        return path;
    }

    public static void ExtractZip(string zipPath, string stageDir)
    {
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, recursive: true);
        Directory.CreateDirectory(stageDir);

        using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.GetFullPath(Path.Combine(stageDir, relative));
            string fullStage = Path.GetFullPath(stageDir) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Подозрительный путь в архиве: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }

        NormalizeStageDirectory(stageDir);
        EnsureCanonicalExecutable(stageDir);
        VerifyStageHasExecutable(stageDir);
    }

    /// <summary>Hoist nested folders until ZapretUI.exe is at stage root (any zip layout).</summary>
    public static void NormalizeStageDirectory(string stageDir)
    {
        for (int i = 0; i < 8; i++)
        {
            if (FindExecutable(stageDir, topLevelOnly: true) is not null)
                return;

            var topDirs = Directory.GetDirectories(stageDir);
            var topFiles = Directory.GetFiles(stageDir);
            if (topDirs.Length != 1 || topFiles.Length > 0)
                break;

            string nested = topDirs[0];
            foreach (var file in Directory.EnumerateFiles(nested, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(nested, file);
                string dest = Path.Combine(stageDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            Directory.Delete(nested, recursive: true);
        }
    }

    public static void EnsureCanonicalExecutable(string stageDir)
    {
        string target = Path.Combine(stageDir, "ZapretUI.exe");
        if (File.Exists(target)) return;

        string? found = FindExecutable(stageDir, topLevelOnly: false);
        if (found is null) return;

        File.Copy(found, target, overwrite: true);
    }

    public static string? ReadInstalledVersionMarker(string installDir)
    {
        try
        {
            string path = Path.Combine(installDir, "installed_app_version.txt");
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }

    public static string? ReadLastUpdateLogTail(int maxLines = 12)
    {
        foreach (string path in new[] { UpdateLogPath, Path.Combine(Path.GetTempPath(), "ZapretUI-update.log") })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0) continue;
                int start = Math.Max(0, lines.Length - maxLines);
                return string.Join(Environment.NewLine, lines[start..]);
            }
            catch { /* try next */ }
        }
        return null;
    }

    public static void LaunchUpdateBatch(string stageDir, string installDir, string targetVersion, int pid)
    {
        AppPaths.EnsureCreated();
        string targetExe = GetTargetExePath(installDir);
        string? currentExe = GetCurrentExePath();
        string currentExeForBat = currentExe ?? targetExe;
        string batPath = Path.Combine(Path.GetTempPath(), "ZapretUI-update.bat");
        string tempLog = Path.Combine(Path.GetTempPath(), "ZapretUI-update.log");
        string persistentLog = UpdateLogPath;
        string versionMarker = Path.Combine(installDir, "installed_app_version.txt");

        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("setlocal EnableExtensions");
        bat.AppendLine($"echo ===== UPDATE {targetVersion} ===== > \"{tempLog}\"");
        bat.AppendLine($"echo %date% %time% — begin >> \"{tempLog}\"");
        bat.AppendLine($"echo install={installDir} >> \"{tempLog}\"");
        bat.AppendLine($"echo stage={stageDir} >> \"{tempLog}\"");
        bat.AppendLine($"echo pid={pid} >> \"{tempLog}\"");
        bat.AppendLine($"echo current={currentExeForBat} >> \"{tempLog}\"");

        bat.AppendLine($"taskkill /PID {pid} /F >nul 2>&1");
        bat.AppendLine("set /a count=0");
        bat.AppendLine(":waitloop");
        bat.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  set /a count+=1");
        bat.AppendLine("  if %count% GEQ 30 goto killphase");
        bat.AppendLine("  timeout /t 1 /nobreak >nul");
        bat.AppendLine("  goto waitloop");
        bat.AppendLine(")");

        bat.AppendLine(":killphase");
        bat.AppendLine($"echo kill stray processes >> \"{tempLog}\"");
        bat.AppendLine("taskkill /IM ZapretUI.exe /F >nul 2>&1");
        bat.AppendLine("taskkill /IM Zapret2UI.exe /F >nul 2>&1");
        bat.AppendLine("taskkill /IM Zanpet.exe /F >nul 2>&1");
        bat.AppendLine("taskkill /IM ZanpetUI.exe /F >nul 2>&1");
        bat.AppendLine("timeout /t 3 /nobreak >nul");

        bat.AppendLine(":copyphase");
        foreach (var legacy in LegacyFileNames)
            bat.AppendLine($"del /F /Q \"{Path.Combine(installDir, legacy)}\" >nul 2>&1");

        bat.AppendLine($"for %%F in (\"{installDir}\\*.dll\") do del /F /Q \"%%F\" >nul 2>&1");

        bat.AppendLine($"echo robocopy mirror >> \"{tempLog}\"");
        bat.AppendLine($"robocopy \"{stageDir}\" \"{installDir}\" /MIR /IS /IT /R:8 /W:2 /NFL /NDL /NJH /NJS >> \"{tempLog}\" 2>&1");
        bat.AppendLine("set RC=%ERRORLEVEL%");
        bat.AppendLine($"echo robocopy exit %RC% >> \"{tempLog}\"");
        bat.AppendLine("if %RC% GEQ 8 goto failed");

        bat.AppendLine($"if not exist \"{targetExe}\" goto failed");
        bat.AppendLine($"for %%A in (\"{targetExe}\") do set SIZE=%%~zA");
        bat.AppendLine("if %SIZE% LSS 1000000 goto failed");

        bat.AppendLine($"echo {targetVersion.TrimStart('v')} > \"{versionMarker}\"");
        bat.AppendLine($"if /I not \"{currentExeForBat}\"==\"{targetExe}\" del /F /Q \"{currentExeForBat}\" >nul 2>&1");
        foreach (var name in LegacyExeNames)
            bat.AppendLine($"del /F /Q \"{Path.Combine(installDir, name)}\" >nul 2>&1");

        bat.AppendLine($"echo starting {targetExe} >> \"{tempLog}\"");
        bat.AppendLine($"start \"\" \"{targetExe}\" --launched-after-update");
        bat.AppendLine("goto done");

        bat.AppendLine(":failed");
        bat.AppendLine($"echo UPDATE FAILED >> \"{tempLog}\"");
        bat.AppendLine("powershell -NoProfile -Command \"Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('Не удалось установить обновление. Откройте logs\\\\update.log','ZapretUI','OK','Error')\" >nul 2>&1");
        bat.AppendLine("goto done");

        bat.AppendLine(":done");
        bat.AppendLine($"copy /Y \"{tempLog}\" \"{persistentLog}\" >nul 2>&1");
        bat.AppendLine($"rmdir /S /Q \"{stageDir}\" >nul 2>&1");
        bat.AppendLine("del /Q \"%~f0\" >nul 2>&1");
        bat.AppendLine("endlocal");

        File.WriteAllText(batPath, bat.ToString(), new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            CreateNoWindow = true,
        };
        if (!AdminElevation.IsRunningAsAdmin())
            psi.Verb = "runas";

        Process.Start(psi);
    }

    private static void VerifyStageHasExecutable(string stageDir)
    {
        if (FindExecutable(stageDir, topLevelOnly: false) is null)
            throw new InvalidOperationException("В архиве обновления нет ZapretUI.exe.");
    }

    private static string? FindExecutable(string dir, bool topLevelOnly)
    {
        var option = topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        foreach (var name in new[] { "ZapretUI.exe", "Zapret2UI.exe", "Zanpet.exe", "ZanpetUI.exe" })
        {
            foreach (var path in Directory.EnumerateFiles(dir, name, option))
                return path;
        }
        return null;
    }
}
