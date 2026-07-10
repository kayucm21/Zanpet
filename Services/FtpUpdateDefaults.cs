namespace ZapretUI.Services;

/// <summary>
/// Встроенные настройки FTP-обновлений (заполните перед сборкой для друзей).
/// Переопределяются через %LOCALAPPDATA%\ZapretUI\ftp-update.json или settings.json.
/// </summary>
internal static class FtpUpdateDefaults
{
    public const bool Enabled = true;
    public const string Host = "185.117.119.3";
    public const int Port = 21;
    public const bool UseSsl = false;
    public const string User = "user4469441";
    public const string Password = "B2TGm3hEOGsn";
    public const string RemotePath = "/updates";
}
