using System.IO;
using System.Text.Json;

namespace ZapretUI.Services;

public sealed class FtpUpdateSettings
{
    public bool Enabled { get; init; } = FtpUpdateDefaults.Enabled;
    public string Host { get; init; } = FtpUpdateDefaults.Host;
    public int Port { get; init; } = FtpUpdateDefaults.Port;
    public bool UseSsl { get; init; } = FtpUpdateDefaults.UseSsl;
    public string User { get; init; } = FtpUpdateDefaults.User;
    public string Password { get; init; } = FtpUpdateDefaults.Password;
    public string RemotePath { get; init; } = FtpUpdateDefaults.RemotePath;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Host);

    public static FtpUpdateSettings Resolve(AppSettings? appSettings = null)
    {
        var cfg = FromDefaults();

        if (appSettings is not null)
            cfg = Merge(cfg, FromAppSettings(appSettings));

        string file = AppPaths.FtpUpdateConfigFile;
        if (File.Exists(file))
        {
            try
            {
                var fromFile = JsonSerializer.Deserialize<FtpUpdateSettings>(
                    File.ReadAllText(file),
                    JsonOpts);
                if (fromFile is not null)
                    cfg = Merge(cfg, fromFile);
            }
            catch { /* ignore broken file */ }
        }

        return cfg;
    }

    private static FtpUpdateSettings FromDefaults() => new();

    private static FtpUpdateSettings FromAppSettings(AppSettings s) => new()
    {
        Enabled = s.FtpUpdateEnabled,
        Host = s.FtpUpdateHost ?? "",
        Port = s.FtpUpdatePort > 0 ? s.FtpUpdatePort : 21,
        UseSsl = s.FtpUpdateUseSsl,
        User = s.FtpUpdateUser ?? "",
        Password = s.FtpUpdatePassword ?? "",
        RemotePath = string.IsNullOrWhiteSpace(s.FtpUpdatePath) ? "/updates" : s.FtpUpdatePath!,
    };

    private static FtpUpdateSettings Merge(FtpUpdateSettings baseCfg, FtpUpdateSettings over)
    {
        static string Pick(string overVal, string baseVal) =>
            string.IsNullOrWhiteSpace(overVal) ? baseVal : overVal.Trim();

        return new FtpUpdateSettings
        {
            Enabled = over.Enabled,
            Host = Pick(over.Host, baseCfg.Host),
            Port = over.Port > 0 ? over.Port : baseCfg.Port,
            UseSsl = over.UseSsl || baseCfg.UseSsl,
            User = Pick(over.User, baseCfg.User),
            Password = string.IsNullOrEmpty(over.Password) ? baseCfg.Password : over.Password,
            RemotePath = Pick(over.RemotePath, baseCfg.RemotePath),
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
