using ZapretUI.Mvvm;

namespace ZapretUI.Models;

/// <summary>
/// Parsed VLESS/REALITY server from a subscription.
/// </summary>
public sealed class VpnServer : ObservableObject
{
    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set => SetField(ref _isConnected, value); }

    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Uuid { get; set; } = "";
    public string Security { get; set; } = "reality";
    public string Sni { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Network { get; set; } = "tcp";
    public string Spx { get; set; } = "";
    public string RawUri { get; set; } = "";
}
