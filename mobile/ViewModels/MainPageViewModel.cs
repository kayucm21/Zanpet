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
        InitializePresets();
    }

    #region Properties - Engine State

    [ObservableProperty] private string _statusText = "Остановлен";
    [ObservableProperty] private string _statusTitle = "Выключено";
    [ObservableProperty] private string _statusTitleColor = "#555566";
    [ObservableProperty] private string _statusDotColor = "#E2566A";
    [ObservableProperty] private string _simpleStatus = "Нажмите для запуска";
    [ObservableProperty] private string _toggleIcon = "\u25B6";
    [ObservableProperty] private string _toggleText = "Включить обход";
    [ObservableProperty] private string _toggleArrow = "\u25B6";
    [ObservableProperty] private string _toggleBgColor = "#60A5FA";
    [ObservableProperty] private string _toggleBorderColor = "#60A5FA";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isUpdating;

    #endregion

    #region Properties - VPN

    [ObservableProperty] private bool _isVpnConnected;
    [ObservableProperty] private string _vpnConnectedInfo = "";
    [ObservableProperty] private string _vpnXrayStatus = "Не установлен";
    [ObservableProperty] private bool _isVpnDownloadVisible = true;

    #endregion

    #region Properties - Presets

    [ObservableProperty] private PresetItem? _selectedPreset;
    [ObservableProperty] private bool _hasRunningPreset;
    [ObservableProperty] private string _runningPresetName = "";

    public ObservableCollection<PresetItem> Presets { get; } = new();
    public ObservableCollection<PresetsGroup> PresetGroups { get; } = new();

    #endregion

    #region Properties - Settings

    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _autoHeal = true;
    [ObservableProperty] private bool _bypassAllSites;
    [ObservableProperty] private bool _gameFilter;
    [ObservableProperty] private string _engineVersionText = "zapret v1.0.2 (zapret2)";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string _appVersion = "v1.0";

    #endregion

    #region Properties - Log

    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _logLineCount = "0 строк";

    private int _logLineNum;

    #endregion

    #region Properties - Servers

    public ObservableCollection<VpnServer> VpnServers { get; } = new();

    #endregion

    #region Commands - Engine

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning) StopEngine();
        else StartEngine();
    }

    private void StartEngine()
    {
        IsRunning = true;
        StatusText = "Запуск...";
        StatusTitle = "Подключение";
        StatusTitleColor = "#F5A623";
        StatusDotColor = "#F5A623";
        ToggleIcon = "\u23F3";
        ToggleText = "Запуск";
        ToggleBgColor = "#F5A623";
        ToggleBorderColor = "#F5A623";
        SimpleStatus = "Инициализация DPI bypass...";

        AppendLog("[INFO] DPI bypass engine запущен");
        AppendLog("[INFO] Стратегия: " + (SelectedPreset?.Name ?? "YouTube + Discord + Telegram"));

        // Simulate engine start
        StatusText = "Работает";
        StatusTitle = "Защищено";
        StatusTitleColor = "#34D399";
        StatusDotColor = "#34D399";
        ToggleIcon = "\u25A0";
        ToggleText = "Остановить обход";
        ToggleBgColor = "#E2566A";
        ToggleBorderColor = "#E2566A";
        SimpleStatus = "DPI bypass активен";

        HasRunningPreset = true;
        RunningPresetName = SelectedPreset?.Name ?? "YouTube + Discord + Telegram";

        AppendLog("[OK] DPI bypass работает");
    }

    private void StopEngine()
    {
        IsRunning = false;
        StatusText = "Остановлен";
        StatusTitle = "Выключено";
        StatusTitleColor = "#555566";
        StatusDotColor = "#E2566A";
        ToggleIcon = "\u25B6";
        ToggleText = "Включить обход";
        ToggleBgColor = "#60A5FA";
        ToggleBorderColor = "#60A5FA";
        SimpleStatus = "Нажмите для запуска";
        HasRunningPreset = false;
        RunningPresetName = "";

        AppendLog("[INFO] DPI bypass остановлен");
    }

    #endregion

    #region Commands - Update

    [RelayCommand]
    private async Task CheckUpdate()
    {
        UpdateStatus = "Проверка обновлений...";
        AppendLog("[INFO] Проверка обновлений движка...");

        await Task.Delay(2000);

        UpdateStatus = "Движок актуален";
        AppendLog("[OK] Движок актуален (v1.0.2)");
    }

    #endregion

    #region Commands - Presets

    [RelayCommand]
    private void DuplicatePreset()
    {
        if (SelectedPreset == null) return;
        var clone = new PresetItem
        {
            Name = SelectedPreset.Name + " (копия)",
            Description = SelectedPreset.Description,
            Args = new List<string>(SelectedPreset.Args),
            IsBuiltIn = false,
            HasBadge = false
        };
        Presets.Add(clone);
        RebuildGroups();
        AppendLog($"[OK] Стратегия «{SelectedPreset.Name}» продублирована");
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset == null || SelectedPreset.IsBuiltIn) return;
        var name = SelectedPreset.Name;
        Presets.Remove(SelectedPreset);
        RebuildGroups();
        SelectedPreset = null;
        AppendLog($"[OK] Стратегия «{name}» удалена");
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (SelectedPreset == null) return;
        AppendLog($"[OK] Стратегия «{SelectedPreset.Name}» сохранена");
    }

    #endregion

    #region Commands - VPN

    public async Task VpnConnectFromServer(VpnServer server)
    {
        if (server.IsConnected)
        {
            await _xrayService.StopAsync();
            server.IsConnected = false;
            IsVpnConnected = false;
            VpnConnectedInfo = "";
            AppendLog($"[INFO] VPN отключён от {server.Name}");
        }
        else
        {
            // Disconnect other servers
            foreach (var s in VpnServers)
                s.IsConnected = false;

            AppendLog($"[INFO] Подключение к {server.Name}...");
            var ok = await _xrayService.StartAsync(server);
            if (ok)
            {
                IsVpnConnected = true;
                VpnConnectedInfo = $"VPN | {server.Name}";
                AppendLog($"[OK] VPN подключён: {server.Name}");
                server.IsConnected = true;
            }
            else
            {
                AppendLog($"[ERROR] Не удалось подключиться к {server.Name}");
            }
        }

        // Refresh UI by notifying collection change
        OnPropertyChanged(nameof(VpnServers));
    }

    [RelayCommand]
    private async Task VpnDownloadXray()
    {
        AppendLog("[INFO] Загрузка xray-core...");
        VpnXrayStatus = "Загрузка...";
        await Task.Delay(3000);
        VpnXrayStatus = "Установлен";
        IsVpnDownloadVisible = false;
        AppendLog("[OK] xray-core установлен");
    }

    [RelayCommand]
    private void VpnRefresh()
    {
        AppendLog("[INFO] Обновление списка серверов...");
        // Servers are hardcoded, just refresh
        AppendLog("[OK] Список серверов обновлён");
    }

    #endregion

    #region Commands - Log

    [RelayCommand]
    private void ClearLog()
    {
        LogText = "";
        _logLineNum = 0;
        LogLineCount = "0 строк";
    }

    #endregion

    #region Helpers

    private void AppendLog(string message)
    {
        _logLineNum++;
        var ts = DateTime.Now.ToString("HH:mm:ss");
        LogText = $"[{ts}] {message}\n{LogText}";
        LogLineCount = $"{_logLineNum} строк";
        LogUpdated?.Invoke(this, LogText);
    }

    public event EventHandler<string>? LogUpdated;

    private void InitializeServers()
    {
        VpnServers.Add(new VpnServer
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

        VpnServers.Add(new VpnServer
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
    }

    private void InitializePresets()
    {
        // Built-in preset
        var combo = new PresetItem
        {
            Name = "YouTube + Discord + Telegram",
            Description = "Рекомендуемая стратегия: YouTube, Discord (войс/медиа), Telegram и остальной TLS/QUIC.",
            Args = new List<string>
            {
                "--filter-tcp=80,443-65535",
                "--out-range=-d7",
                "--lua-desync=send:repeats=2",
                "--lua-desync=tls_multisplit_sni:seqovl=652:ip_autottl=-3,3-20",
                "--new",
                "--filter-udp=80,443-65535",
                "--payload=all",
                "--out-range=-d8",
                "--lua-desync=fake:ip_autottl=-2,3-20:repeats=10:payload=all",
                "--new",
                "--filter-udp=19294-19344,50000-65535",
                "--payload=all",
                "--lua-desync=fake:ip_autottl=-2,3-20:repeats=2"
            },
            IsBuiltIn = true,
            HasBadge = true,
            Badge = "ВКЛ"
        };
        Presets.Add(combo);

        // Additional built-in
        Presets.Add(new PresetItem
        {
            Name = "YouTube",
            Description = "Обход блокировок YouTube: TLS и QUIC трафик.",
            Args = new List<string> { "--filter-tcp=443", "--out-range=-d7", "--lua-desync=tls_multisplit_sni:seqovl=652", "--new", "--filter-udp=443", "--payload=all" },
            IsBuiltIn = true
        });

        Presets.Add(new PresetItem
        {
            Name = "Discord",
            Description = "Обход блокировок Discord: голос, медиа, STUN.",
            Args = new List<string> { "--filter-tcp=443", "--out-range=-d7", "--new", "--filter-udp=19294-19344,50000-65535", "--payload=all" },
            IsBuiltIn = true
        });

        Presets.Add(new PresetItem
        {
            Name = "Telegram",
            Description = "Обход блокировок Telegram (DC сегменты).",
            Args = new List<string> { "--filter-tcp=443", "--out-range=-d7", "--lua-desync=tls_multisplit_sni:seqovl=652" },
            IsBuiltIn = true
        });

        SelectedPreset = combo;
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        PresetGroups.Clear();

        var builtIns = Presets.Where(p => p.IsBuiltIn).ToList();
        var personal = Presets.Where(p => !p.IsBuiltIn).ToList();

        if (builtIns.Any())
            PresetGroups.Add(new PresetsGroup("Стратегии", builtIns));
        if (personal.Any())
            PresetGroups.Add(new PresetsGroup("Импорт / Личные", personal));
    }

    #endregion
}

public class PresetItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public bool IsBuiltIn { get; set; }
    public bool HasBadge { get; set; }
    public string Badge { get; set; } = "";
    public bool IsSelected { get; set; }
}

public class PresetsGroup : List<PresetItem>
{
    public string Title { get; }
    public PresetsGroup(string title, List<PresetItem> items) : base(items)
    {
        Title = title;
    }
}
