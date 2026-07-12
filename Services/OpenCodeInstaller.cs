using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ZapretUI.Services;

/// <summary>Downloads opencode-windows-x64.zip into %LOCALAPPDATA%\ZapretUI\opencode.</summary>
public static class OpenCodeInstaller
{
    private const string ReleasesApi =
        "https://api.github.com/repos/anomalyco/opencode/releases/latest";

    private const string FtpZipName = "opencode-windows-x64.zip";

    public static async Task<(bool Ok, string Message, string? ExePath)> EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string? existing = OpenCodeResolver.Resolve();
        if (existing is not null && File.Exists(existing))
            return (true, "OpenCode найден", existing);

        if (TryInstallFromBundled())
            return (true, "OpenCode установлен из ClassicData", OpenCodeResolver.BundledExePath);

        progress?.Report("Скачиваю OpenCode (~70 МБ), подождите…");

        try
        {
            string zipPath = Path.Combine(OpenCodeResolver.InstallDir, "opencode-download.zip");
            Directory.CreateDirectory(OpenCodeResolver.InstallDir);

            bool downloaded = await TryDownloadFromFtpAsync(zipPath, progress, ct).ConfigureAwait(false);
            if (!downloaded)
            {
                string? zipUrl = await GetWindowsZipUrlAsync(ct).ConfigureAwait(false);
                if (zipUrl is null)
                    return (false, "Не найден Windows-архив OpenCode (GitHub и FTP недоступны)", null);

                await DownloadAsync(zipUrl, zipPath, progress, ct).ConfigureAwait(false);
            }

            return await ExtractAndInstallAsync(zipPath, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, "Установка OpenCode отменена", null);
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось установить OpenCode: {ex.Message}", null);
        }
    }

    private static bool TryInstallFromBundled()
    {
        string bundled = Path.Combine(AppPaths.ClassicDataDir, "exe", "opencode.exe");
        if (!File.Exists(bundled))
            return false;

        Directory.CreateDirectory(OpenCodeResolver.InstallDir);
        File.Copy(bundled, OpenCodeResolver.BundledExePath, overwrite: true);
        return File.Exists(OpenCodeResolver.BundledExePath);
    }

    private static async Task<bool> TryDownloadFromFtpAsync(
        string zipPath, IProgress<string>? progress, CancellationToken ct)
    {
        var cfg = FtpUpdateSettings.Resolve();
        if (!cfg.IsConfigured) return false;

        try
        {
            progress?.Report("Скачиваю OpenCode с FTP-сервера обновлений…");
            await FtpUpdateService.DownloadZipAsync(cfg, FtpZipName, zipPath, null, ct)
                .ConfigureAwait(false);
            return File.Exists(zipPath) && new FileInfo(zipPath).Length > 1_000_000;
        }
        catch
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return false;
        }
    }

    private static async Task<(bool Ok, string Message, string? ExePath)> ExtractAndInstallAsync(
        string zipPath, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Распаковка OpenCode…");
        string tempDir = Path.Combine(OpenCodeResolver.InstallDir, "_extract");
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

        string? extracted = Directory.GetFiles(tempDir, "opencode.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (extracted is null)
            return (false, "В архиве OpenCode нет opencode.exe", null);

        string exePath = OpenCodeResolver.BundledExePath;
        if (File.Exists(exePath))
            File.Delete(exePath);
        File.Move(extracted, exePath);

        try { Directory.Delete(tempDir, true); } catch { }
        try { File.Delete(zipPath); } catch { }

        return (true, "OpenCode установлен", exePath);
    }

    private static async Task<string?> GetWindowsZipUrlAsync(CancellationToken ct)
    {
        using var resp = await HttpFactory.GitHub.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
            .ConfigureAwait(false);
        return release?.Assets?
            .FirstOrDefault(a => a.Name.Equals("opencode-windows-x64.zip", StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl;
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<string>? progress, CancellationToken ct)
    {
        using var resp = await HttpFactory.General.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);

        var buffer = new byte[1024 * 128];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                progress?.Report($"Скачиваю OpenCode… {pct}%");
            }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = "";
    }
}
