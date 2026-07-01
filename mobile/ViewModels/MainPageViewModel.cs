using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretUI_Mobile.Models;
using ZapretUI_Mobile.Services;

namespace ZapretUI_Mobile.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly XrayService _xrayService;

    public MainPageViewModel(XrayService xrayService)
    {
        _xrayService = xrayService;
        InitializeServers();
    }

    [ObservableProperty]
    private VpnServer? _selectedServer;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _statusColor = "#ff4444";

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _toggleIcon = "Power";

    [ObservableProperty]
    private string _toggleText = "OFF";

    public ObservableCollection<VpnServer> Servers { get; } = new();

    public event EventHandler<string>? LogUpdated;

    private void InitializeServers()
    {
        Servers.Add(new VpnServer
        {
            Name = "Moscow (TCP/Reality)",
            Address = "31.76.14.166",
            Port = 9443,
            Id = "52729ad8-eb3f-4ab8-89b7-f6715c81623f",
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Sni = "www.cloudflare.com",
            Fingerprint = "safari",
            PublicKey = "tr7AGu2HJSs2PMWJWVJu5Wb_j4m30D5XydUB5mJZAlE",
            ShortId = "ec58f673d73750cb",
            Network = "tcp"
        });

        Servers.Add(new VpnServer
        {
            Name = "Saint-Petersburg (XHTTP/Reality)",
            Address = "31.76.14.166",
            Port = 444,
            Id = "3e1dad94-2e0f-4ad7-8a53-8b304397bd65",
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Sni = "n.sni-347-default.ssl.fastly.net",
            Fingerprint = "firefox",
            PublicKey = "tr7AGu2HJSs2PMWJWVJu5Wb_j4m30D5XydUB5mJZAlE",
            ShortId = "8bf978f2a63c420a",
            Network = "xhttp",
            Path = "/xhttp",
            Mode = "auto"
        });

        SelectedServer = Servers[0];
    }

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected)
        {
            await Disconnect();
        }
        else
        {
            await Connect();
        }
    }

    private async Task Connect()
    {
        if (SelectedServer == null)
        {
            AppendLog("[ERROR] No server selected");
            return;
        }

        IsConnected = true;
        StatusText = "Connecting...";
        StatusColor = "#ffaa00";
        ToggleIcon = "...";
        ToggleText = "ON";
        AppendLog($"[INFO] Connecting to {SelectedServer.Name}...");

        try
        {
            var connected = await _xrayService.StartAsync(SelectedServer);
            if (connected)
            {
                StatusText = "Connected";
                StatusColor = "#00ff88";
                ToggleIcon = "VPN";
                ToggleText = "ON";
                AppendLog("[OK] Connected successfully");
            }
            else
            {
                IsConnected = false;
                StatusText = "Connection failed";
                StatusColor = "#ff4444";
                ToggleIcon = "Power";
                ToggleText = "OFF";
                AppendLog("[ERROR] Failed to connect");
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = "Error";
            StatusColor = "#ff4444";
            ToggleIcon = "Power";
            ToggleText = "OFF";
            AppendLog($"[ERROR] {ex.Message}");
        }
    }

    private async Task Disconnect()
    {
        AppendLog("[INFO] Disconnecting...");
        StatusText = "Disconnecting...";
        StatusColor = "#ffaa00";
        ToggleIcon = "...";
        ToggleText = "OFF";

        try
        {
            await _xrayService.StopAsync();
            IsConnected = false;
            StatusText = "Disconnected";
            StatusColor = "#ff4444";
            ToggleIcon = "Power";
            ToggleText = "OFF";
            AppendLog("[OK] Disconnected");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] {ex.Message}");
        }
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText = $"[{timestamp}] {message}\n{LogText}";
        LogUpdated?.Invoke(this, LogText);
    }
}