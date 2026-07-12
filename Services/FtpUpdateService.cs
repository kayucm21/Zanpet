using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Проверка и загрузка обновлений ZapretUI с вашего FTP-сервера.
/// На сервере: /updates/update.json + ZapretUI-vX.Y.Z.zip
/// </summary>
public static class FtpUpdateService
{
    private const string ManifestName = "update.json";

    public static async Task<AppReleaseInfo?> FetchLatestAsync(
        FtpUpdateSettings cfg,
        CancellationToken ct = default)
    {
        if (!cfg.IsConfigured) return null;

        string json = await DownloadTextAsync(cfg, ManifestName, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag", out var t) ? (t.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(tag) && root.TryGetProperty("version", out var v))
            tag = "v" + (v.GetString() ?? "").TrimStart('v');

        string file = root.TryGetProperty("file", out var f) ? (f.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("update.json на FTP: нужны поля tag и file.");

        int build = root.TryGetProperty("build", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
        string? notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;

        string display = $"ftp://{cfg.Host}{NormalizeRemotePath(cfg.RemotePath)}/{file}";
        return new AppReleaseInfo(tag.TrimStart('v'), display, AppReleaseSource.Ftp, file, build, notes);
    }

    public static async Task DownloadZipAsync(
        FtpUpdateSettings cfg,
        string remoteFile,
        string destPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() => DownloadZipCore(cfg, remoteFile, destPath, progress, ct), ct)
            .ConfigureAwait(false);
    }

    private static void DownloadZipCore(
        FtpUpdateSettings cfg,
        string remoteFile,
        string destPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        string uri = BuildUri(cfg, remoteFile);
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.DownloadFile;
        request.Credentials = new NetworkCredential(cfg.User, cfg.Password);
        request.EnableSsl = cfg.UseSsl;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;

        using var response = (FtpWebResponse)request.GetResponse();
        using var src = response.GetResponseStream()
            ?? throw new InvalidOperationException("FTP: пустой ответ.");
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        long total = response.ContentLength;
        var buf = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, n);
            read += n;
            if (total > 0)
                progress?.Report(Math.Clamp((double)read / total, 0, 1));
        }
    }

    private static async Task<string> DownloadTextAsync(
        FtpUpdateSettings cfg,
        string remoteFile,
        CancellationToken ct)
    {
        string tmp = Path.Combine(AppPaths.TempDir, $"ftp-{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            await DownloadZipAsync(cfg, remoteFile, tmp, null, ct).ConfigureAwait(false);
            return await File.ReadAllTextAsync(tmp, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string BuildUri(FtpUpdateSettings cfg, string remoteFile)
    {
        string path = NormalizeRemotePath(cfg.RemotePath);
        string file = remoteFile.TrimStart('/');
        return $"ftp://{cfg.Host}:{cfg.Port}{path}/{file}";
    }

    private static string NormalizeRemotePath(string path)
    {
        string p = (path ?? "/").Replace('\\', '/').Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        return p.TrimEnd('/');
    }
}
