namespace ZapretUI.Models;

public enum AppReleaseSource
{
    Ftp,
    GitHub,
    Yandex,
    Cdn,
}

/// <summary>Available app release resolved from FTP, GitHub, or other mirrors.</summary>
public sealed record AppReleaseInfo(
    string Tag,
    string DisplayUrl,
    AppReleaseSource Source,
    string? FtpZipFile = null);
