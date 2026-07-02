namespace ZapretUI_Mobile.Models;

public class VpnServer
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
    public string Security { get; set; } = "reality";
    public string Sni { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string ShortId { get; set; } = string.Empty;
    public string Network { get; set; } = "tcp";
    public string Path { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;

    // Display properties
    public bool IsConnected { get; set; }
    public int PingMs { get; set; }
    public string PingText => PingMs > 0 ? $"{PingMs}ms" : "";
    public string PingColor => PingMs switch
    {
        > 0 and <= 100 => "#34D399",
        > 100 and <= 300 => "#F5A623",
        > 300 => "#E2566A",
        _ => "#555566"
    };
    public string DetailsText => $"{Sni} | {Fingerprint}";
    public string AddressPort => $"{Address}:{Port}";
    public string ConnectButtonText => IsConnected ? "Отключить" : "Подключить";
    public string ConnectButtonBg => IsConnected ? "#E2566A" : "#60A5FA";

    public override string ToString() => Name;
}
