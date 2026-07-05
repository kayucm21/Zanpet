using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Silently keeps the zapret2 engine up to date from the official GitHub releases.
/// Downloads the release zip, verifies the Windows binaries against the release
/// sha256sum.txt, and installs only the files we actually need.
/// </summary>
public sealed class UpdaterService
{
    private const string EngineRepo = "bol-van/zapret2";
    private const string ReleasesLatestApi =
        "https://api.github.com/repos/bol-van/zapret2/releases/latest";

    /// <summary>This UI app's own releases (separate from the engine).</summary>
    private const string AppReleasesLatestApi =
        "https://api.github.com/repos/kayucm21/Zanpet/releases/latest";
    private const string AppReleasesPage =
        "https://github.com/kayucm21/Zanpet/releases/latest";

    /// <summary>Cloudflare R2 fallback: fast CDN download when GitHub is slow/blocked.
    /// Format: direct ZIP URL like https://pub-xxxxx.r2.dev/ZapretUI-v2.5.3.zip</summary>
    private const string AppCdnFallback = "";

    /// <summary>Yandex Disk public share link for app updates (fallback when GitHub is slow).</summary>
    private const string YandexDiskShareUrl = "https://disk.yandex.ru/d/ILFbZC4Pez241w";

    private static HttpClient Http => HttpFactory.GitHub;

    /// <summary>Currently installed engine tag, or null if the engine is absent.</summary>
    public string? InstalledVersion
    {
        get
        {
            try
            {
                if (File.Exists(AppPaths.WinwsExe) && File.Exists(AppPaths.EngineVersionFile))
                    return File.ReadAllText(AppPaths.EngineVersionFile).Trim();
            }
            catch { /* treat as not installed */ }
            return null;
        }
    }

    /// <summary>Display-friendly engine version. Converts zapret2 tags to readable form.</summary>
    public string? InstalledVersionDisplay
    {
        get
        {
            var raw = InstalledVersion;
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw.Contains("zapret2", StringComparison.OrdinalIgnoreCase))
                return "v2.0.0 (zapret2)";
            return raw;
        }
    }

    public bool IsEngineInstalled => File.Exists(AppPaths.WinwsExe);

    // ---- app (this UI) self-update check ----------------------------------

    /// <summary>This app's own version (from the assembly), e.g. "0.1.0".</summary>
    public static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Latest app release (tag + page URL) from GitHub, or null on any failure.</summary>
    public async Task<(string Tag, string Url)?> FetchAppLatestAsync(CancellationToken ct = default)
    {
        // Try API first
        try
        {
            using var resp = await Http.GetAsync(AppReleasesLatestApi, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(url)) url = AppReleasesPage;
            if (!string.IsNullOrEmpty(tag)) return (tag, url);
        }
        catch { /* API blocked or failed, try web fallback */ }

        // Web fallback: scrape the releases page HTML
        try
        {
            using var resp = await Http.GetAsync(AppReleasesPage, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Look for tag in /releases/tag/vX.Y.Z pattern
            var m = System.Text.RegularExpressions.Regex.Match(html, @"/releases/tag/([^""\s]+)");
            if (m.Success)
            {
                string tag = m.Groups[1].Value.TrimStart('v');
                return (tag, AppReleasesPage);
            }
        }
        catch { /* both paths failed */ }
        return null;
    }

    /// <summary>Fetch the zip download URL for a specific app release tag.</summary>
    public async Task<string?> FetchAppZipUrlAsync(string tag, CancellationToken ct = default)
    {
        try
        {
            string apiUrl = $"https://api.github.com/repos/kayucm21/Zanpet/releases/tags/{tag}";
            using var resp = await Http.GetAsync(apiUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return asset.GetProperty("browser_download_url").GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Resolve a Yandex Disk public share link to a direct download URL via the public API.</summary>
    private static async Task<string?> ResolveYandexDiskUrlAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(YandexDiskShareUrl)) return null;
        try
        {
            string api = $"https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key={Uri.EscapeDataString(YandexDiskShareUrl)}";
            using var resp = await HttpFactory.General.GetAsync(api, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("href", out var href))
                return href.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Download app update, extract, replace files and restart.</summary>
    public async Task InstallAppUpdateAsync(string tag, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? zipUrl = await FetchAppZipUrlAsync(tag, ct).ConfigureAwait(false);

        // Build fallback URLs: GitHub primary → Yandex Disk → CDN
        var urls = new List<string>();
        if (zipUrl is not null) urls.Add(zipUrl);

        // Resolve Yandex Disk share link to actual download URL
        string? yandexUrl = await ResolveYandexDiskUrlAsync(ct).ConfigureAwait(false);
        if (yandexUrl is not null) urls.Add(yandexUrl);

        if (!string.IsNullOrEmpty(AppCdnFallback)) urls.Add(AppCdnFallback);

        if (urls.Count == 0) throw new InvalidOperationException("Не найден zip-файл релиза.");

        string zipPath = Path.Combine(Path.GetTempPath(), $"ZapretUI-{tag}.zip");
        string stageDir = Path.Combine(Path.GetTempPath(), $"ZapretUI-stage-{Guid.NewGuid():N}");

        try
        {
            progress?.Report(0);
            bool downloaded = false;
            Exception? lastError = null;

            foreach (var downloadUrl in urls)
            {
                try
                {
                    using var dl = await HttpFactory.General.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    dl.EnsureSuccessStatusCode();
                    long total = dl.Content.Headers.ContentLength ?? 0;
                    await using var stream = await dl.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    var buf = new byte[1 << 16];
                    long read = 0;
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        read += n;
                        if (total > 0)
                        {
                            double frac = Math.Clamp((double)read / total, 0, 1);
                            progress?.Report(frac);
                        }
                    }
                    downloaded = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (!downloaded)
                throw lastError ?? new InvalidOperationException("Не удалось скачать обновление.");
            Directory.CreateDirectory(stageDir);

            using (var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string dest = Path.Combine(stageDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var src = entry.Open();
                    using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    src.CopyTo(dst);
                }
            }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string batPath = Path.Combine(Path.GetTempPath(), "ZapretUI-update.bat");
            string selfExe = Environment.ProcessPath ?? Path.Combine(exeDir, "ZapretUI.exe");
            string exeName = Path.GetFileName(selfExe);
            string logPath = Path.Combine(Path.GetTempPath(), "ZapretUI-update.log");
            string oldExe = Path.Combine(exeDir, "ZapretUI_old.exe");

            var bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine($"echo %date% %time% — Starting update > \"{logPath}\"");

            // Wait for process to fully exit (check every 1 second, max 30s)
            int pid = Environment.ProcessId;
            bat.AppendLine($"echo Waiting for PID {pid} to exit...");
            bat.AppendLine($"set /a count=0");
            bat.AppendLine(":waitloop");
            bat.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
            bat.AppendLine($"if %ERRORLEVEL%==0 (");
            bat.AppendLine($"  set /a count+=1");
            bat.AppendLine($"  if %count% GEQ 30 (echo TIMEOUT waiting for exit >> \"{logPath}\" & goto copyphase)");
            bat.AppendLine($"  timeout /t 1 /nobreak >nul");
            bat.AppendLine($"  goto waitloop");
            bat.AppendLine($")");
            bat.AppendLine($"echo Process exited after %count%s >> \"{logPath}\"");

            bat.AppendLine(":copyphase");
            // Rename running exe (ren works on locked files on Windows)
            bat.AppendLine($"del /Q \"{oldExe}\" >nul 2>&1");
            bat.AppendLine($"ren \"{selfExe}\" \"ZapretUI_old.exe\" >nul 2>&1");
            bat.AppendLine($"echo Renamed old exe >> \"{logPath}\"");
            // Copy new files from stageDir to exeDir
            bat.AppendLine($"echo Copying files...");
            bat.AppendLine($"robocopy \"{stageDir}\" \"{exeDir}\" /E /Y /R:3 /W:1 >> \"{logPath}\" 2>&1");
            bat.AppendLine($"echo Robocopy exit: %ERRORLEVEL% >> \"{logPath}\"");
            // Wait for filesystem to settle
            bat.AppendLine($"timeout /t 2 /nobreak >nul");
            // Verify the new exe exists before starting
            bat.AppendLine($"if exist \"{selfExe}\" (");
            bat.AppendLine($"  echo Starting new exe... >> \"{logPath}\"");
            bat.AppendLine($"  start \"\" \"{selfExe}\" --launched-after-update");
            bat.AppendLine($") else (");
            bat.AppendLine($"  echo ERROR: exe not found after copy: {selfExe} >> \"{logPath}\"");
            bat.AppendLine($"  echo Trying fallback path... >> \"{logPath}\"");
            bat.AppendLine($"  if exist \"{exeDir}\\{exeName}\" (");
            bat.AppendLine($"    start \"\" \"{exeDir}\\{exeName}\" --launched-after-update");
            bat.AppendLine($"  ) else (");
            bat.AppendLine($"    echo FATAL: cannot find exe anywhere >> \"{logPath}\"");
            bat.AppendLine($"  )");
            bat.AppendLine($")");
            // Cleanup: delete old exe and temp files
            bat.AppendLine($"timeout /t 3 /nobreak >nul");
            bat.AppendLine($"del /Q \"{oldExe}\" >nul 2>&1");
            bat.AppendLine($"del /Q \"{zipPath}\" >nul 2>&1");
            bat.AppendLine($"rmdir /S /Q \"{stageDir}\" >nul 2>&1");
            bat.AppendLine($"del /Q \"%~f0\" >nul 2>&1");

            File.WriteAllText(batPath, bat.ToString(), new UTF8Encoding(false));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                CreateNoWindow = false,
            });

            // Exit this process
            await Task.Delay(500);
            Environment.Exit(0);
        }
        catch
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            throw;
        }
    }

    /// <summary>Pull the numeric version out of an arbitrary release tag — handles any prefix
    /// ("v1.2.0", "Zapret2UI-0.3.0", "release-1.2"), not just a leading "v".</summary>
    public static Version? ParseTagVersion(string? tag)
    {
        var m = Regex.Match(tag ?? "", @"\d+(?:\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>True if the release tag is a newer SemVer than the running app.</summary>
    public static bool IsAppUpdate(string tag)
    {
        var latest = ParseTagVersion(tag);
        return latest is not null && Version.TryParse(AppVersion, out var cur) && latest > cur;
    }

    /// <summary>
    /// True if the installed engine is missing parts that newer UI versions need
    /// (e.g. the windivert filter set added after the first install). Such installs
    /// should be re-extracted even when the version tag is unchanged.
    /// </summary>
    public bool IsEngineComplete =>
        IsEngineInstalled && Directory.Exists(AppPaths.WinDivertFilterDir);

    /// <summary>
    /// Resolve the latest release and its asset URLs. Tries the GitHub <b>API</b> first, then falls
    /// back to scraping the regular <b>github.com</b> release page — some ISPs block api.github.com
    /// but allow github.com (or the reverse), so we try both before giving up.
    /// </summary>
    public async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            return await FetchLatestViaApiAsync(ct).ConfigureAwait(false);
        }
        catch (Exception apiEx) when (apiEx is not OperationCanceledException)
        {
            try { return await FetchLatestViaWebAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw apiEx; } // both paths failed — surface the original API error
        }
    }

    /// <summary>Latest release via api.github.com (JSON).</summary>
    private async Task<ReleaseInfo> FetchLatestViaApiAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(ReleasesLatestApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("В ответе GitHub нет tag_name.");

        string? zipUrl = null, shaUrl = null;
        long zipSize = 0;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";

            // The all-platforms bundle, e.g. zapret2-v1.0.1.zip
            // (exclude the openwrt-embedded variant which is .tar.gz anyway)
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("openwrt", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = url;
                zipSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (name.Equals("sha256sum.txt", StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }

        if (zipUrl is null)
            throw new InvalidOperationException($"В релизе {tag} не найден zip-ассет.");

        return new ReleaseInfo(tag, zipUrl, shaUrl, zipSize);
    }

    /// <summary>Latest release by scraping github.com (no API): the latest-redirect gives the tag,
    /// the expanded_assets partial gives the download links. Asset size is unknown (0).</summary>
    private async Task<ReleaseInfo> FetchLatestViaWebAsync(CancellationToken ct)
    {
        // 1. The tag — github.com/<repo>/releases/latest 302-redirects to …/releases/tag/<tag>.
        using var resp = await Http.GetAsync(
            $"https://github.com/{EngineRepo}/releases/latest", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        int i = finalUrl.IndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0) throw new InvalidOperationException("Не удалось определить версию движка на github.com.");
        string tag = finalUrl[(i + 5)..].Trim('/');
        if (tag.Length == 0) throw new InvalidOperationException("Пустой тег релиза на github.com.");

        // 2. The assets — the expanded_assets partial lists every download link.
        string html = await Http.GetStringAsync(
            $"https://github.com/{EngineRepo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct)
            .ConfigureAwait(false);

        string? zipUrl = null, shaUrl = null;
        foreach (Match m in Regex.Matches(html,
            "href=\"(/" + Regex.Escape(EngineRepo) + "/releases/download/[^\"]+)\""))
        {
            string path = m.Groups[1].Value;
            string url = "https://github.com" + path;
            string name = path[(path.LastIndexOf('/') + 1)..];
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("openwrt", StringComparison.OrdinalIgnoreCase))
                zipUrl = url;
            else if (name.Equals("sha256sum.txt", StringComparison.OrdinalIgnoreCase))
                shaUrl = url;
        }

        if (zipUrl is null)
            throw new InvalidOperationException($"На странице релиза {tag} не найден zip-ассет.");

        return new ReleaseInfo(tag, zipUrl, shaUrl, 0);
    }

    /// <summary>True if a newer release than the installed one is available.</summary>
    public bool IsUpdateAvailable(ReleaseInfo latest)
    {
        if (string.IsNullOrEmpty(InstalledVersion)) return true;
        var installed = ParseTagVersion(InstalledVersion);
        var latestVer = ParseTagVersion(latest.Tag);
        if (installed is null || latestVer is null)
            return !string.Equals(InstalledVersion, latest.Tag, StringComparison.OrdinalIgnoreCase);
        return latestVer > installed;
    }

    /// <summary>Download, verify and install the engine from the given release.</summary>
    public async Task InstallAsync(
        ReleaseInfo release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        AppPaths.EnsureCreated();

        string zipPath = Path.Combine(AppPaths.TempDir, $"zapret2-{release.Tag}.zip");
        string stageDir = Path.Combine(AppPaths.TempDir, $"stage-{Guid.NewGuid():N}");

        try
        {
            // 1. Download the zip with progress.
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, "Загрузка движка…"));
            await DownloadFileAsync(release.ZipUrl, zipPath, release.ZipSize, progress, ct)
                .ConfigureAwait(false);

            // 2. Pull the checksum manifest (per-binary hashes).
            Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
            if (release.Sha256Url is not null)
            {
                progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, "Проверка контрольных сумм…"));
                string shaText = await Http.GetStringAsync(release.Sha256Url, ct).ConfigureAwait(false);
                hashes = ParseSha256Sum(shaText);
            }

            // 3. Extract only what we need into a staging folder.
            progress?.Report(new UpdateProgress(UpdatePhase.Extracting, 0, "Распаковка…"));
            Directory.CreateDirectory(stageDir);
            ExtractNeeded(zipPath, stageDir, ct);

            // 4. Verify the Windows binaries against the manifest (integrity).
            if (hashes.Count > 0)
                VerifyBinaries(stageDir, hashes);

            // 5. Move staged engine into place.
            progress?.Report(new UpdateProgress(UpdatePhase.Extracting, 0.95, "Установка…"));
            InstallStaged(stageDir);
            File.WriteAllText(AppPaths.EngineVersionFile, release.Tag);

            progress?.Report(new UpdateProgress(UpdatePhase.Done, 1.0, $"Готово — {release.Tag}"));
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDir(stageDir);
        }
    }

    // ---- internals ---------------------------------------------------------

    private async Task DownloadFileAsync(
        string url, string destPath, long knownSize,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? knownSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                double frac = Math.Clamp((double)read / total, 0, 1);
                progress?.Report(new UpdateProgress(
                    UpdatePhase.Downloading, frac,
                    $"Загрузка движка… {read / 1_048_576.0:F1}/{total / 1_048_576.0:F1} МБ"));
            }
        }
    }

    /// <summary>Parse <c>&lt;hash&gt;␠␠&lt;path&gt;</c> lines, keyed by file name.</summary>
    private static Dictionary<string, string> ParseSha256Sum(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 66) continue;
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            string hash = line[..64];
            string path = line[sp..].TrimStart(' ', '*').Replace('\\', '/');
            // Key by "<arch>/<filename>" suffix so we can match staged files unambiguously.
            string fileName = path[(path.LastIndexOf('/') + 1)..];
            // Prefer the most specific key: arch/file, but also store bare file name.
            int binIdx = path.IndexOf("binaries/", StringComparison.OrdinalIgnoreCase);
            if (binIdx >= 0)
            {
                string rel = path[(binIdx + "binaries/".Length)..]; // e.g. windows-x86_64/winws2.exe
                map[rel] = hash;
            }
            map[fileName] = hash;
        }
        return map;
    }

    private static void ExtractNeeded(string zipPath, string stageDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // top folder inside the zip, e.g. "zapret2-v1.0.1/"
        string? top = null;
        foreach (var e in zip.Entries)
        {
            int slash = e.FullName.IndexOf('/');
            if (slash > 0) { top = e.FullName[..(slash + 1)]; break; }
        }
        if (top is null) throw new InvalidOperationException("Неожиданная структура архива релиза.");

        string arch = AppPaths.ReleaseArchFolder;
        string binPrefix = $"{top}binaries/{arch}/";
        string luaPrefix = $"{top}lua/";
        string filesPrefix = $"{top}files/";
        string wfPrefix = $"{top}init.d/windivert.filter.examples/";

        bool gotWinws = false;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue; // directory marker

            string? target = null;
            if (entry.FullName.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, entry.FullName[binPrefix.Length..]);
            else if (entry.FullName.StartsWith(luaPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "lua", entry.FullName[luaPrefix.Length..]);
            else if (entry.FullName.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "files", entry.FullName[filesPrefix.Length..]);
            else if (entry.FullName.StartsWith(wfPrefix, StringComparison.OrdinalIgnoreCase) &&
                     entry.Name.StartsWith("windivert_part", StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "windivert.filter", entry.FullName[wfPrefix.Length..]);

            if (target is null) continue;

            // Defense in depth against zip path traversal.
            string fullStage = Path.GetFullPath(stageDir) + Path.DirectorySeparatorChar;
            string fullTarget = Path.GetFullPath(target);
            if (!fullTarget.StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Подозрительный путь в архиве: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
            entry.ExtractToFile(fullTarget, overwrite: true);

            if (entry.Name.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase))
                gotWinws = true;
        }

        if (!gotWinws)
            throw new InvalidOperationException(
                $"В архиве нет winws2.exe для {arch}. Возможно, релиз без Windows-бинарников.");
    }

    private static void VerifyBinaries(string stageDir, Dictionary<string, string> hashes)
    {
        string arch = AppPaths.ReleaseArchFolder;
        int verified = 0;
        bool winwsVerified = false;
        // Windows binaries were staged flat at the root of stageDir.
        foreach (var file in Directory.EnumerateFiles(stageDir, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            string archKey = $"{arch}/{name}";
            string? expected = hashes.GetValueOrDefault(archKey) ?? hashes.GetValueOrDefault(name);
            if (expected is null) continue; // not all files are listed (only binaries are)

            string actual = ComputeSha256(file);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Контрольная сумма не совпала для {name}. Загрузка повреждена или подменена.");
            verified++;
            if (name.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase)) winwsVerified = true;
        }

        // Fail closed: a manifest was provided (caller only calls us when hashes.Count > 0), so it must
        // actually cover what we staged. Zero matches = the manifest doesn't line up with the release →
        // we verified nothing. And if the manifest lists winws2.exe, that match is mandatory — otherwise
        // a tampered engine binary could install just because its name wasn't a manifest key.
        bool manifestHasWinws = hashes.Keys.Any(k => k.EndsWith("winws2.exe", StringComparison.OrdinalIgnoreCase));
        if (verified == 0 || (manifestHasWinws && !winwsVerified))
            throw new InvalidOperationException(
                "Не удалось проверить целостность движка по манифесту sha256 — установка отменена.");
    }

    private static void InstallStaged(string stageDir)
    {
        foreach (var src in Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(stageDir, src);
            string dst = Path.Combine(AppPaths.EngineDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
