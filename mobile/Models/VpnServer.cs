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

    public override string ToString() => Name;
}