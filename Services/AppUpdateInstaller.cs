using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Reliable in-place app update: normalize zip layout, mirror files (/MIR), purge legacy 2.x DLL layout.
/// </summary>
internal static class AppUpdateInstaller
{
    private static readonly string[] LegacyFilesToDelete =
    [
        "ZapretUI.dll",
        "ZapretUI.deps.json",
        "ZapretUI.runtimeconfig.json",
        "ZapretUI.pdb",
        "ZapretUI_old.exe",
        "Zapret2UI.exe",
        "Zapret2UI.dll",
    ];

    public static string GetInstallDirectory()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return Path.GetDirectoryName(processPath)!;
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
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
        VerifyStageHasExecutable(stageDir);
    }

    /// <summary>If zip has a single top-level folder, hoist its contents to stage root.</summary>
    public static void NormalizeStageDirectory(string stageDir)
    {
        if (FindExecutable(stageDir, topLevelOnly: true) is not null) return;

        var topDirs = Directory.GetDirectories(stageDir);
        var topFiles = Directory.GetFiles(stageDir);
        if (topDirs.Length != 1 || topFiles.Length > 0) return;

        string nested = topDirs[0];
        string tempRoot = stageDir + "_flat";
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
        Directory.CreateDirectory(tempRoot);

        foreach (var file in Directory.EnumerateFiles(nested, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(nested, file);
            string dest = Path.Combine(tempRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        Directory.Delete(stageDir, recursive: true);
        Directory.Move(tempRoot, stageDir);
        VerifyStageHasExecutable(stageDir);
    }

    public static void WriteInstalledVersionMarker(string installDir, string version)
    {
        string path = Path.Combine(installDir, "installed_app_version.txt");
        File.WriteAllText(path, version.TrimStart('v'), new UTF8Encoding(false));
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

    public static void LaunchUpdateBatch(string stageDir, string installDir, string targetVersion, int pid)
    {
        string selfExe = Path.Combine(installDir, "ZapretUI.exe");
        string batPath = Path.Combine(Path.GetTempPath(), "ZapretUI-update.bat");
        string logPath = Path.Combine(Path.GetTempPath(), "ZapretUI-update.log");
        string versionMarker = Path.Combine(installDir, "installed_app_version.txt");

        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("setlocal EnableExtensions");
        bat.AppendLine($"echo %date% %time% — update {targetVersion} > \"{logPath}\"");

        bat.AppendLine($"echo Waiting for PID {pid}...");
        bat.AppendLine("set /a count=0");
        bat.AppendLine(":waitloop");
        bat.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  set /a count+=1");
        bat.AppendLine("  if %count% GEQ 60 goto killphase");
        bat.AppendLine("  timeout /t 1 /nobreak >nul");
        bat.AppendLine("  goto waitloop");
        bat.AppendLine(")");

        bat.AppendLine(":killphase");
        bat.AppendLine("taskkill /IM ZapretUI.exe /F >nul 2>&1");
        bat.AppendLine("taskkill /IM Zapret2UI.exe /F >nul 2>&1");
        bat.AppendLine("timeout /t 2 /nobreak >nul");

        bat.AppendLine(":copyphase");
        foreach (var legacy in LegacyFilesToDelete)
            bat.AppendLine($"del /F /Q \"{Path.Combine(installDir, legacy)}\" >nul 2>&1");

        bat.AppendLine($"echo Mirroring files to \"{installDir}\" >> \"{logPath}\"");
        bat.AppendLine($"robocopy \"{stageDir}\" \"{installDir}\" /MIR /IS /IT /R:5 /W:2 /NFL /NDL /NJH /NJS >> \"{logPath}\" 2>&1");
        bat.AppendLine("set RC=%ERRORLEVEL%");
        bat.AppendLine($"echo Robocopy exit: %RC% >> \"{logPath}\"");
        bat.AppendLine("if %RC% GEQ 8 goto failed");

        bat.AppendLine($"del /F /Q \"{installDir}\\*.dll\" >nul 2>&1");

        bat.AppendLine($"echo {targetVersion.TrimStart('v')} > \"{versionMarker}\"");
        bat.AppendLine("timeout /t 1 /nobreak >nul");

        bat.AppendLine($"if exist \"{selfExe}\" (");
        bat.AppendLine($"  echo Starting {selfExe} >> \"{logPath}\"");
        bat.AppendLine($"  start \"\" \"{selfExe}\" --launched-after-update");
        bat.AppendLine("  goto cleanup");
        bat.AppendLine(")");

        bat.AppendLine(":failed");
        bat.AppendLine($"echo UPDATE FAILED >> \"{logPath}\"");
        bat.AppendLine("goto cleanup");

        bat.AppendLine(":cleanup");
        bat.AppendLine($"rmdir /S /Q \"{stageDir}\" >nul 2>&1");
        bat.AppendLine("del /Q \"%~f0\" >nul 2>&1");
        bat.AppendLine("endlocal");

        File.WriteAllText(batPath, bat.ToString(), new UTF8Encoding(false));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
        });
    }

    private static void VerifyStageHasExecutable(string stageDir)
    {
        if (FindExecutable(stageDir, topLevelOnly: false) is null)
            throw new InvalidOperationException("В архиве обновления нет ZapretUI.exe.");
    }

    private static string? FindExecutable(string dir, bool topLevelOnly)
    {
        var option = topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        foreach (var path in Directory.EnumerateFiles(dir, "ZapretUI.exe", option))
            return path;
        foreach (var path in Directory.EnumerateFiles(dir, "Zapret2UI.exe", option))
            return path;
        return null;
    }
}
