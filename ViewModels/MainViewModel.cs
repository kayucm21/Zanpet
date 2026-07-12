using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using ZapretUI.Models;
using ZapretUI.Mvvm;
using ZapretUI.Services;

namespace ZapretUI.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 3000;

    private readonly UpdaterService _updater = new();
    private readonly EngineService _engine = new();
    private readonly PresetService _presets = new();
    private readonly HostlistService _hostlists = new();
    private readonly SettingsService _settingsSvc = new();
    private readonly AutostartService _autostart = new();
    private readonly MonitorService _monitor = new();
    private readonly VpnService _vpn = new();
    private readonly TgWsProxyService _tgWs = new();
    private readonly TelegramHostsService _telegramHosts = new();
    private readonly DiscordHostsService _discordHosts = new();
    private readonly SocialHostsService _socialHosts = new();
    private readonly DiscordDesktopService _discordDesktop = new();
    private readonly DiscordShopBridgeService _discordShopBridge;
    private readonly TargetService _targets = new();
    private readonly DomainAutoConfigService _domainAuto;
    private readonly IpRuleService _ipRules = new();
    private readonly VoiceAssistantService _voice = new();
    private CancellationTokenSource? _listenCts;

    public event Action<string, string>? Notify;

    public AppSettings Settings => _settingsSvc.Settings;

    public MainViewModel()
    {
        _discordShopBridge = new DiscordShopBridgeService(_vpn);
        _domainAuto = new DomainAutoConfigService(_hostlists, _targets);
        _engine.StateChanged += s => OnUi(() =>
        {
            State = s;
            if (s == EngineState.Stopped)
            {
                _tgWs.Stop();
                _discordShopBridge.Stop();
                _discordDesktop.Reset();
                _telegramHosts.Remove();
                _discordHosts.Remove();
                _socialHosts.RemoveAll();
            }
        });
        _engine.LogLine += line => OnUi(() => AppendLog(line));
        _tgWs.LogLine += line => OnUi(() => AppendLog(line));
        _telegramHosts.LogLine += line => OnUi(() => AppendLog(line));
        _discordHosts.LogLine += line => OnUi(() => AppendLog(line));
        _socialHosts.LogLine += line => OnUi(() => AppendLog(line));
        _discordDesktop.LogLine += line => OnUi(() => AppendLog(line));
        _discordShopBridge.LogLine += line => OnUi(() => AppendLog(line));

        StartCommand = new RelayCommand(async _ => await StartAsync(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _engine.Stop(), _ => CanStop);
        ToggleCommand = new RelayCommand(async _ => { if (IsRunning) _engine.Stop(); else await StartAsync(); },
                                         _ => !IsUpdating && (IsRunning || CanStart));
        CheckUpdateCommand = new RelayCommand(async _ => await CheckAndUpdateAsync(silent: false),
                                              _ => !IsUpdating);
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());

        DuplicatePresetCommand = new RelayCommand(_ => DuplicatePreset(), _ => SelectedPreset is not null);
        DeletePresetCommand = new RelayCommand(_ => DeletePreset(),
                                               _ => SelectedPreset is { IsBuiltIn: false });
        SavePresetCommand = new RelayCommand(_ => SavePreset(),
                                             _ => SelectedPreset is { IsBuiltIn: false });

        ImportPresetCommand = new RelayCommand(async _ => await ImportPresetAsync(), _ => !IsUpdating);

        ApplyStrategyCommand = new RelayCommand(async _ => await ApplyStrategyAsync(),
                                                _ => IsStrategyChangePending && !IsUpdating);

        SimpleToggleCommand = new RelayCommand(async _ => await SimpleToggleAsync(),
            _ => !IsUpdating && (IsRunning || CanStart));

        SetSimpleModeCommand = new RelayCommand(_ => IsSimpleMode = true);
        SetAdvancedModeCommand = new RelayCommand(_ => IsSimpleMode = false);
        GoToSettingsCommand = new RelayCommand(_ => { IsSimpleMode = false; SelectedTabIndex = SettingsTabIndex; });

        HomeToggleCommand = new RelayCommand(
            _ => (IsSimpleMode ? SimpleToggleCommand : ToggleCommand).Execute(null),
            _ => (IsSimpleMode ? SimpleToggleCommand : ToggleCommand).CanExecute(null));

        TogglePresetArgsCommand = new RelayCommand(_ => ShowPresetArgs = !ShowPresetArgs);

        VpnDownloadXrayCommand = new RelayCommand(async _ => await VpnDownloadXrayAsync(), _ => !IsVpnBusy);
        VpnConnectCommand = new RelayCommand(async s => await VpnConnectAsync(s as VpnServer), _ => !IsVpnBusy);
        VpnPingCommand = new RelayCommand(async _ => await VpnPingAllAsync(), _ => !IsVpnBusy);
        VpnRefreshCommand = new RelayCommand(async _ => await VpnRefreshAsync(), _ => !IsVpnBusy);

        AcceptDomainCommand = new RelayCommand(async _ => await AcceptDomainAsync(),
            _ => !IsAnalyzingDomain && !string.IsNullOrWhiteSpace(DomainInput));
        DeleteDomainCommand = new RelayCommand(_ => DeleteCustomTarget(),
            _ => SelectedCustomTarget is not null && !IsAnalyzingDomain);

        AcceptIpCommand = new RelayCommand(async _ => await AcceptIpAsync(),
            _ => !IsProbingIp && !string.IsNullOrWhiteSpace(IpInput));
        DeleteIpCommand = new RelayCommand(_ => DeleteIpRule(),
            _ => SelectedIpRule is not null && !IsProbingIp);

        SendVoiceCommand = new RelayCommand(async _ => await SendVoiceAsync(),
            _ => !IsVoiceBusy && !string.IsNullOrWhiteSpace(VoiceInput));
        ToggleMicCommand = new RelayCommand(async _ => await ToggleMicAsync(), _ => !IsVoiceBusy);
        ClearVoiceCommand = new RelayCommand(_ => ClearVoiceChat(), _ => VoiceMessages.Count > 0 && !IsVoiceBusy);
        LaunchOpenCodeTerminalCommand = new RelayCommand(async _ => await LaunchOpenCodeInTerminalAsync());

        _voice.ConfigureFromSettings(Settings);
        ReloadVoiceTtsOptions();

        _vpn.LogLine += line => OnUi(() => AppendLog(line));

        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Preset.GroupTitle)));

        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());

        ReloadPresets();

        SeedVoiceWelcome();
        _ = OnStartupVpnAsync();
    }

    private async Task OnStartupVpnAsync()
    {
        try
        {
            if (!_vpn.IsXrayInstalled)
            {
                VpnXrayStatus = "Скачивание xray-core…";
                AppendLog("VPN: xray-core не найден, скачиваю автоматически…");
                var progress = new Progress<double>(p =>
                {
                    VpnXrayStatus = $"Скачивание xray-core… {p:P0}";
                });
                await _vpn.DownloadXrayAsync(progress);
                VpnXrayStatus = _vpn.IsXrayInstalled ? "Установлен" : "Ошибка установки";
                OnPropertyChanged(nameof(VpnXrayShowDownload));
                AppendLog(_vpn.IsXrayInstalled
                    ? "VPN: xray-core установлен."
                    : "VPN: xray-core НЕ найден после скачивания.");
            }
            else
            {
                VpnXrayStatus = "Установлен";
                AppendLog("VPN: xray-core уже установлен.");
            }

            var servers = _vpn.GetDefaultServers();
            VpnServers.Clear();
            foreach (var s in servers) VpnServers.Add(s);
            VpnStatus = $"{servers.Count} серверов.";
            AppendLog($"VPN: {servers.Count} серверов загружено.");
        }
        catch (Exception ex)
        {
            VpnXrayStatus = $"Ошибка: {ex.Message}";
            AppendLog($"VPN ошибка: {ex.Message}");
        }
    }

    // ---- collections -------------------------------------------------------

    public ObservableCollection<Preset> Presets { get; } = new();
    public ICollectionView PresetsView { get; }
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<CustomTarget> CustomTargets { get; } = new();
    public ObservableCollection<CustomIpRule> CustomIpRules { get; } = new();
    public ObservableCollection<VoiceChatMessage> VoiceMessages { get; } = new();

    // ---- commands ----------------------------------------------------------

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ApplyStrategyCommand { get; }
    public RelayCommand DuplicatePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand ImportPresetCommand { get; }
    public RelayCommand SimpleToggleCommand { get; }
    public RelayCommand SetSimpleModeCommand { get; }
    public RelayCommand SetAdvancedModeCommand { get; }
    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand HomeToggleCommand { get; }
    public RelayCommand TogglePresetArgsCommand { get; }
    public RelayCommand VpnDownloadXrayCommand { get; }
    public RelayCommand VpnConnectCommand { get; }
    public RelayCommand VpnPingCommand { get; }
    public RelayCommand VpnRefreshCommand { get; }
    public RelayCommand AcceptDomainCommand { get; }
    public RelayCommand DeleteDomainCommand { get; }
    public RelayCommand AcceptIpCommand { get; }
    public RelayCommand DeleteIpCommand { get; }
    public RelayCommand SendVoiceCommand { get; }
    public RelayCommand ToggleMicCommand { get; }
    public RelayCommand ClearVoiceCommand { get; }
    public RelayCommand LaunchOpenCodeTerminalCommand { get; }

    // ---- engine state ------------------------------------------------------

    private EngineState _state = EngineState.Stopped;
    public EngineState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                if (value == EngineState.Stopped) RunningPreset = null;
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                UpdateMonitor();
                RaiseCommandStates();
            }
        }
    }

    public bool IsRunning => State == EngineState.Running;
    public bool CanStart => State == EngineState.Stopped && !IsUpdating && _updater.IsEngineInstalled;
    public bool CanStop => State is EngineState.Running or EngineState.Starting;

    // ---- presets -----------------------------------------------------------

    private Preset? _selectedPreset;
    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value))
            {
                Settings.ActivePresetName = value?.Name;
                _settingsSvc.Save();
                OnPropertyChanged(nameof(PresetArgsText));
                OnPropertyChanged(nameof(CommandPreview));
                OnPropertyChanged(nameof(SelectedPresetEditable));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    public bool SelectedPresetEditable => SelectedPreset is { IsBuiltIn: false };

    private bool _showPresetArgs;
    public bool ShowPresetArgs { get => _showPresetArgs; set => SetField(ref _showPresetArgs, value); }

    private Preset? _runningPreset;
    public Preset? RunningPreset
    {
        get => _runningPreset;
        private set
        {
            if (SetField(ref _runningPreset, value))
            {
                OnPropertyChanged(nameof(RunningPresetName));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                RaiseCommandStates();
            }
        }
    }

    public string RunningPresetName => RunningPreset?.Name ?? "—";

    public bool IsStrategyChangePending =>
        IsRunning && RunningPreset is not null && SelectedPreset is not null
        && !ReferenceEquals(RunningPreset, SelectedPreset);

    public string RunStatusText =>
        IsRunning
            ? $"Включён: {RunningPresetName}"
            : SelectedPreset is null ? "пресет не выбран" : $"Выбран: {SelectedPreset.Name}";

    public string PresetArgsText
    {
        get => SelectedPreset is null ? "" : string.Join('\n', SelectedPreset.Args);
        set
        {
            if (SelectedPreset is { IsBuiltIn: false } p)
            {
                p.Args = value.Replace("\r\n", "\n").Split('\n')
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    public string CommandPreview =>
        SelectedPreset is null
            ? ""
            : EngineService.PreviewCommandLine(SelectedPreset, null, Settings.GameFilter,
                                               Settings.BypassAllSites);

    // ---- updates -----------------------------------------------------------

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (SetField(ref _isUpdating, value))
            {
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private string _engineVersion = "—";
    public string EngineVersion { get => _engineVersion; private set => SetField(ref _engineVersion, value); }

    public string AppVersion => "v" + UpdaterService.AppVersion;

    private string _appUpdateFtpVersion = "—";
    public string AppUpdateFtpVersion { get => _appUpdateFtpVersion; private set => SetField(ref _appUpdateFtpVersion, value); }

    private string _appUpdateGitHubVersion = "—";
    public string AppUpdateGitHubVersion { get => _appUpdateGitHubVersion; private set => SetField(ref _appUpdateGitHubVersion, value); }

    private string _appUpdateAvailable = "—";
    public string AppUpdateAvailable { get => _appUpdateAvailable; private set => SetField(ref _appUpdateAvailable, value); }

    // ---- VPN ---------------------------------------------------------------

    private string _vpnXrayStatus = "Не установлен";
    public string VpnXrayStatus { get => _vpnXrayStatus; private set => SetField(ref _vpnXrayStatus, value); }

    private string _vpnStatus = "";
    public string VpnStatus { get => _vpnStatus; private set => SetField(ref _vpnStatus, value); }

    private string _vpnConnectedServerName = "";
    public string VpnConnectedServerName { get => _vpnConnectedServerName; private set => SetField(ref _vpnConnectedServerName, value); }

    private string _lastRefreshTime = "";
    public string LastRefreshTime { get => _lastRefreshTime; private set => SetField(ref _lastRefreshTime, value); }

    public bool IsVpnConnected => _vpn.IsConnected;

    public bool VpnXrayShowDownload => !_vpn.IsXrayInstalled;

    private bool _isVpnBusy;
    public bool IsVpnBusy
    {
        get => _isVpnBusy;
        private set { if (SetField(ref _isVpnBusy, value)) RaiseCommandStates(); }
    }

    public ObservableCollection<VpnServer> VpnServers { get; } = new();



    // ---- settings toggles --------------------------------------------------

    public bool AutostartEnabled
    {
        get => Settings.Autostart;
        set
        {
            Settings.Autostart = value;
            if (value) _autostart.Enable(); else _autostart.Disable();
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    public bool AutostartEngine
    {
        get => Settings.AutostartEngine;
        set { Settings.AutostartEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { Settings.MinimizeToTray = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool AutoHeal
    {
        get => Settings.AutoHeal;
        set { Settings.AutoHeal = value; _settingsSvc.Save(); OnPropertyChanged(); UpdateMonitor(); }
    }

    public bool GameFilter
    {
        get => Settings.GameFilter;
        set
        {
            if (value == Settings.GameFilter) return;
            Settings.GameFilter = value;
            _settingsSvc.Save();
            _engine.GameFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync();
        }
    }

    public bool BypassAllSites
    {
        get => Settings.BypassAllSites;
        set
        {
            if (value == Settings.BypassAllSites) return;
            Settings.BypassAllSites = value;
            _settingsSvc.Save();
            _engine.BypassAllSites = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync();
        }
    }

    public bool IsLightTheme
    {
        get => Settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase);
        set
        {
            string theme = value ? "light" : "dark";
            if (string.Equals(Settings.Theme, theme, StringComparison.OrdinalIgnoreCase)) return;
            Settings.Theme = theme;
            _settingsSvc.Save();
            ThemeManager.ApplyTheme(theme);
            OnPropertyChanged();
        }
    }

    private void UpdateMonitor()
    {
        if (IsRunning && Settings.AutoHeal) { if (!_monitor.IsRunning) _monitor.Start(); }
        else _monitor.Stop();
    }

    private async Task AutoHealAsync()
    {
        if (IsUpdating) return;
        Notify?.Invoke("Zapret UI", "Обход перестал работать — перезапускаю…");
        AppendLog("Авто-починка: обход перестал отвечать, перезапускаю.");
        if (IsRunning)
        {
            await _engine.StopAsync(TimeSpan.FromSeconds(3));
        }
        if (CanStart) await StartAsync();
        if (IsRunning)
            Notify?.Invoke("Zapret UI", "Обход перезапущен автоматически.");
    }

    // ---- simple / advanced mode -------------------------------------------

    public bool IsSimpleMode
    {
        get => Settings.SimpleMode;
        set
        {
            if (Settings.SimpleMode == value) return;
            Settings.SimpleMode = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdvancedMode));
            if (value) SelectedTabIndex = 0;
            HomeToggleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAdvancedMode => !IsSimpleMode;

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetField(ref _selectedTabIndex, value))
                return;
            if (value == VoiceTabIndex)
                _ = OnVoiceTabOpenedAsync();
        }
    }

    private const int SettingsTabIndex = 2;
    private const int VoiceTabIndex = 4;
    private bool _voiceWelcomeSpoken;

    public Preset? RecommendedPreset =>
        Presets.FirstOrDefault(p => p.IsRecommended) ?? Presets.FirstOrDefault();

    private string _simpleStatus = "Нажмите «Включить обход» — приложение применит рекомендуемый набор и запустит DPI-обход.";
    public string SimpleStatus { get => _simpleStatus; private set => SetField(ref _simpleStatus, value); }

    private async Task SimpleToggleAsync()
    {
        if (IsRunning) { _engine.Stop(); SimpleStatus = "Обход остановлен."; return; }

        var preset = RecommendedPreset;
        if (preset is null) { SimpleStatus = "Движок ещё не установлен — дождитесь загрузки."; return; }
        SelectedPreset = preset;
        SimpleStatus = $"Запускаю обход: «{preset.Name}».";
        await StartAsync();
    }

    // ---- auto-heal ---------------------------------------------------------

    private string _autoStatusText = "";
    public string AutoStatusText { get => _autoStatusText; private set => SetField(ref _autoStatusText, value); }

    // ---- custom domains ----------------------------------------------------

    private string _domainInput = "";
    public string DomainInput
    {
        get => _domainInput;
        set
        {
            if (SetField(ref _domainInput, value))
                AcceptDomainCommand.RaiseCanExecuteChanged();
        }
    }

    private string _domainAnalyzeStatus = "Вставьте домен — программа найдёт поддомены и сохранит список для обхода.";
    public string DomainAnalyzeStatus
    {
        get => _domainAnalyzeStatus;
        private set => SetField(ref _domainAnalyzeStatus, value);
    }

    private bool _isAnalyzingDomain;
    public bool IsAnalyzingDomain
    {
        get => _isAnalyzingDomain;
        private set
        {
            if (SetField(ref _isAnalyzingDomain, value))
            {
                AcceptDomainCommand.RaiseCanExecuteChanged();
                DeleteDomainCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private CustomTarget? _selectedCustomTarget;
    public CustomTarget? SelectedCustomTarget
    {
        get => _selectedCustomTarget;
        set
        {
            if (SetField(ref _selectedCustomTarget, value))
                DeleteDomainCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasCustomTargets => CustomTargets.Count > 0;

    private void ReloadCustomTargets()
    {
        CustomTargets.Clear();
        foreach (var t in _targets.GetTargets())
            CustomTargets.Add(t);
        OnPropertyChanged(nameof(HasCustomTargets));
        SelectedCustomTarget ??= CustomTargets.FirstOrDefault();
    }

    private async Task AcceptDomainAsync()
    {
        string normalized = TargetService.Normalize(DomainInput);
        if (normalized.Length == 0)
        {
            DomainAnalyzeStatus = "Некорректный домен. Пример: https://web.whatsapp.com/";
            return;
        }

        string saveKey = TargetService.RegistrableRoot(normalized);
        var plan = _domainAuto.Detect(DomainInput);

        IsAnalyzingDomain = true;
        try
        {
            var quick = TargetService.QuickSeed(normalized);
            _targets.Save(saveKey, quick);
            ReloadCustomTargets();
            DomainInput = "";
            DomainAnalyzeStatus = $"{plan.Label}: сохранено. Подбор доменов и портов (до 6 сек)…";

            var analyzed = await _domainAuto.AnalyzeAsync(plan, CancellationToken.None).ConfigureAwait(true);

            var finalDomains = analyzed.Domains;
            if (_targets.Exists(saveKey))
            {
                finalDomains = _targets.ReadDomains(saveKey)
                    .Concat(analyzed.Domains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(plan.DomainCap)
                    .ToList();
            }

            DomainAnalyzeStatus = "Проверка домена, IP и белого списка…";
            var verify = await DomainVerifyService.VerifyAsync(
                plan, finalDomains, _hostlists, CancellationToken.None).ConfigureAwait(true);
            if (!verify.Ok)
            {
                DomainAnalyzeStatus = verify.Message;
                AppendLog(verify.Message);
                return;
            }

            if (verify.ResolvedIps.Count > 0)
            {
                _ipRules.AddBypassIps(verify.ResolvedIps, verify.PingMs, saveKey);
                ReloadCustomIpRules();
                AppendLog($"IP для обхода: {verify.ResolvedIps.Count} адрес(ов) из DNS.");
            }

            _targets.Save(saveKey, finalDomains, analyzed.OpenPorts);
            plan.Tune?.Invoke(Settings);
            _settingsSvc.Save();

            ReloadCustomTargets();
            SelectedCustomTarget = CustomTargets.FirstOrDefault(t =>
                t.Name.Equals(saveKey, StringComparison.OrdinalIgnoreCase));

            string portsText = analyzed.OpenPorts.Count > 0
                ? string.Join(", ", analyzed.OpenPorts)
                : "443";
            DomainAnalyzeStatus =
                $"Готово: {plan.Label} — {finalDomains.Count} доменов, {verify.ResolvedIps.Count} IP, порты: {portsText}";
            AppendLog($"Домен «{normalized}» ({plan.Label}): {finalDomains.Count} доменов, {verify.ResolvedIps.Count} IP, порты {portsText}. {verify.Message}");

            if (!string.IsNullOrEmpty(plan.ServiceKey) &&
                (SelectedPreset is null || !SelectedPreset.IncludesService(plan.ServiceKey)))
            {
                var better = Presets.FirstOrDefault(p => p.IsRecommended)
                             ?? Presets.FirstOrDefault();
                if (better is not null) SelectedPreset = better;
            }

            if (!IsRunning && CanStart)
            {
                AppendLog("Автозапуск обхода DPI для нового домена…");
                await StartAsync().ConfigureAwait(true);
            }
            else if (IsRunning)
            {
                AppendLog("Перезапуск движка с новым списком…");
                await ApplyStrategyAsync().ConfigureAwait(true);
            }
            else
            {
                AppendLog("Домен сохранён. Запустите обход вручную, когда движок будет готов.");
            }
        }
        catch (OperationCanceledException)
        {
            DomainAnalyzeStatus = "Таймаут — домен сохранён по спискам. Запустите обход.";
            AppendLog("Проверка домена: таймаут (данные сохранены).");
        }
        catch (Exception ex)
        {
            DomainAnalyzeStatus = $"Ошибка: {ex.Message}";
            AppendLog($"Ошибка настройки домена: {ex.Message}");
        }
        finally
        {
            IsAnalyzingDomain = false;
        }
    }

    private void DeleteCustomTarget()
    {
        if (SelectedCustomTarget is not { } target) return;
        if (!ConfirmDialog.Show("Удалить домен?",
                $"Список «{target.Name}» ({target.DomainCount} доменов) будет удалён из обхода."))
            return;

        _targets.Delete(target.Name);
        ReloadCustomTargets();
        DomainAnalyzeStatus = $"Удалено: {target.Name}";
        AppendLog($"Домен удалён из обхода: {target.Name}");

        if (IsRunning)
            _ = ApplyStrategyAsync();
    }

    // ---- custom IP rules ---------------------------------------------------

    private string _ipInput = "";
    public string IpInput
    {
        get => _ipInput;
        set
        {
            if (SetField(ref _ipInput, value))
                AcceptIpCommand.RaiseCanExecuteChanged();
        }
    }

    private string _ipProbeStatus = "IP исключение — игры и серверы без DPI. Проверка ping/TCP перед принятием.";
    public string IpProbeStatus
    {
        get => _ipProbeStatus;
        private set => SetField(ref _ipProbeStatus, value);
    }

    private bool _isProbingIp;
    public bool IsProbingIp
    {
        get => _isProbingIp;
        private set
        {
            if (SetField(ref _isProbingIp, value))
            {
                AcceptIpCommand.RaiseCanExecuteChanged();
                DeleteIpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private CustomIpRule? _selectedIpRule;
    public CustomIpRule? SelectedIpRule
    {
        get => _selectedIpRule;
        set
        {
            if (SetField(ref _selectedIpRule, value))
                DeleteIpCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasCustomIpRules => CustomIpRules.Count > 0;

    private void ReloadCustomIpRules()
    {
        CustomIpRules.Clear();
        foreach (var r in _ipRules.GetRules())
            CustomIpRules.Add(r);
        OnPropertyChanged(nameof(HasCustomIpRules));
        SelectedIpRule ??= CustomIpRules.FirstOrDefault();
    }

    private async Task AcceptIpAsync()
    {
        string cidr = IpRuleService.NormalizeCidr(IpInput);
        if (cidr.Length == 0)
        {
            IpProbeStatus = "Некорректный IP. Пример: 1.2.3.4 или 10.0.0.0/24";
            return;
        }

        IsProbingIp = true;
        IpProbeStatus = $"Сохранение {cidr}…";
        try
        {
            _ipRules.SaveRule(cidr, IpRuleKind.Exclude, 0, "user");
            ReloadCustomIpRules();
            IpInput = "";
            IpProbeStatus = $"Принято: {cidr}. Проверка TCP…";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var probe = await Task.Run(() => IpRuleService.ProbeStabilityAsync(cidr, cts.Token))
                .ConfigureAwait(false);

            if (probe.AvgPingMs > 0)
            {
                _ipRules.SaveRule(cidr, IpRuleKind.Exclude, probe.AvgPingMs, "user");
                ReloadCustomIpRules();
            }

            OnUi(() => IpProbeStatus = $"{cidr}: {probe.Message}");

            AppendLog($"IP исключение {cidr}: {probe.Message}");

            if (IsRunning)
            {
                AppendLog("Перезапуск движка с IP-исключением…");
                _ = Task.Run(async () =>
                {
                    try { await ApplyStrategyAsync().ConfigureAwait(false); }
                    catch (Exception ex) { OnUi(() => AppendLog($"Перезапуск: {ex.Message}")); }
                });
            }
            else
            {
                AppendLog("IP сохранён. Включите обход на главной вкладке.");
            }
        }
        catch (OperationCanceledException)
        {
            IpProbeStatus = "Проверка TCP прервана — IP уже сохранён.";
        }
        catch (Exception ex)
        {
            IpProbeStatus = $"Ошибка: {ex.Message}";
            AppendLog($"Ошибка IP: {ex.Message}");
        }
        finally
        {
            IsProbingIp = false;
        }
    }

    private void DeleteIpRule()
    {
        if (SelectedIpRule is not { } rule) return;
        if (!ConfirmDialog.Show("Удалить IP?",
                $"Правило «{rule.Cidr}» будет удалено из TCP/UDP фильтра."))
            return;

        _ipRules.Delete(rule.Cidr);
        ReloadCustomIpRules();
        IpProbeStatus = $"Удалено: {rule.Cidr}";
        AppendLog($"IP удалён: {rule.Cidr}");

        if (IsRunning)
            _ = ApplyStrategyAsync();
    }

    // ---- voice assistant ---------------------------------------------------

    private string _voiceInput = "";
    public string VoiceInput
    {
        get => _voiceInput;
        set
        {
            if (SetField(ref _voiceInput, value))
                SendVoiceCommand.RaiseCanExecuteChanged();
        }
    }

    private string _voiceStatus = "Нажмите микрофон или напишите запрос. Нужен запущенный OpenCode: opencode serve --port 4096";
    public string VoiceStatus
    {
        get => _voiceStatus;
        private set => SetField(ref _voiceStatus, value);
    }

    private string _openCodeAgentsText = "";
    public string SpeechCapabilityText
    {
        get => _speechCapabilityText;
        private set => SetField(ref _speechCapabilityText, value);
    }
    private string _speechCapabilityText = "";

    public ObservableCollection<VoiceTtsOption> VoiceTtsLanguages { get; } = [];

    public string VoiceTtsLanguage
    {
        get => Settings.VoiceTtsLanguage;
        set
        {
            string code = string.IsNullOrWhiteSpace(value) ? "ru-RU" : value.Trim();
            if (Settings.VoiceTtsLanguage.Equals(code, StringComparison.OrdinalIgnoreCase))
                return;

            Settings.VoiceTtsLanguage = code;
            _settingsSvc.Save();
            _voice.ConfigureFromSettings(Settings);
            OnPropertyChanged();
            SpeechCapabilityText = _voice.SpeechCapability;
            UpdateVoiceAssistantCardStatus();
        }
    }

    public string VoiceAssistantCardStatus
    {
        get => _voiceAssistantCardStatus;
        private set => SetField(ref _voiceAssistantCardStatus, value);
    }
    private string _voiceAssistantCardStatus = "Подключение…";

    public string OpenCodeAgentsText
    {
        get => _openCodeAgentsText;
        private set => SetField(ref _openCodeAgentsText, value);
    }

    private bool _isListening;
    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (SetField(ref _isListening, value))
                ToggleMicCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isVoiceBusy;
    public bool IsVoiceBusy
    {
        get => _isVoiceBusy;
        private set
        {
            if (SetField(ref _isVoiceBusy, value))
                RaiseVoiceCommandStates();
        }
    }

    private void SeedVoiceWelcome()
    {
        SpeechCapabilityText = _voice.SpeechCapability;
        UpdateVoiceAssistantCardStatus();
        if (VoiceMessages.Count > 0) return;
        VoiceMessages.Add(new VoiceChatMessage
        {
            Role = VoiceMessageRole.Assistant,
            Text = BuildWelcomeText(),
            AgentName = "zapret"
        });
    }

    private string BuildWelcomeText() =>
        _voice.CanListen
            ? "Привет! Нажмите микрофон и говорите — я отвечу в чате и озвучу."
            : "Привет! Пока пишите текстом — микрофон подключается автоматически.";

    private async Task OnVoiceTabOpenedAsync()
    {
        _voice.ConfigureFromSettings(Settings);
        OnUi(() =>
        {
            SpeechCapabilityText = _voice.SpeechCapability;
            UpdateVoiceAssistantCardStatus();
        });

        if (_voiceWelcomeSpoken) return;
        _voiceWelcomeSpoken = true;

        string welcome = BuildWelcomeText();
        if (VoiceBackendDefaults.SpeakResponses)
            await SpeakVoiceAsync(welcome).ConfigureAwait(false);
    }

    private async Task ConnectVoiceBackendSilentAsync()
    {
        try
        {
            var (ok, msg) = await _voice.RefreshConnectionAsync(Settings).ConfigureAwait(false);
            if (!ok)
            {
                var (running, runMsg) = await OpenCodeLauncher.GetServerStatusAsync(
                    VoiceBackendDefaults.ResolveOpenCodeUrl(Settings)).ConfigureAwait(false);
                if (running)
                    (ok, msg) = await _voice.RefreshConnectionAsync(Settings).ConfigureAwait(false);
                else
                {
                    bool started = await OpenCodeLauncher.TryEnsureRunningAsync(
                        VoiceBackendDefaults.ResolveOpenCodeUrl(Settings)).ConfigureAwait(false);
                    if (started)
                        (ok, msg) = await _voice.RefreshConnectionAsync(Settings).ConfigureAwait(false);
                }
            }

            OnUi(() =>
            {
                UpdateOpenCodeAgentsText();
                UpdateVoiceAssistantCardStatus();
                if (ok)
                    VoiceStatus = "Готов. OpenCode подключён. Нажмите микрофон или напишите запрос.";
                else
                    VoiceStatus = msg.Contains("недоступен", StringComparison.OrdinalIgnoreCase)
                        ? "OpenCode уже может быть запущен — попробуйте задать вопрос. Если не отвечает, нажмите «Запустить OpenCode в терминале»."
                        : msg;
            });
        }
        catch
        {
            OnUi(() => VoiceStatus = "Голосовой помощник готов. Пишите или говорите.");
        }
    }

    private async Task SendVoiceAsync()
    {
        string text = VoiceInput.Trim();
        if (text.Length == 0) return;
        VoiceInput = "";
        await ProcessVoiceRequestAsync(text).ConfigureAwait(false);
    }

    private async Task ToggleMicAsync()
    {
        if (IsListening)
        {
            _voice.StopListening();
            _listenCts?.Cancel();
            return;
        }

        if (!_voice.CanListen)
        {
            string hint = _voice.RecognitionHint
                ?? "Установите русский язык: Параметры → Время и язык → Речь → Распознавание речи.";
            VoiceStatus = hint;
            await PostSystemAsync(hint).ConfigureAwait(false);
            return;
        }

        IsListening = true;
        VoiceStatus = "Слушаю… говорите (нажмите микрофон ещё раз, чтобы остановить)";
        _listenCts = new CancellationTokenSource();
        try
        {
            string? heard = await _voice.ListenAsync(_listenCts.Token, partial =>
                OnUi(() => { if (IsListening) VoiceInput = partial; }))
                .ConfigureAwait(false);

            if (_listenCts.IsCancellationRequested)
            {
                OnUi(() => VoiceStatus = "Запись остановлена.");
                return;
            }

            if (string.IsNullOrWhiteSpace(heard))
            {
                const string msg = "Не расслышал. Попробуйте ещё раз или напишите текстом.";
                OnUi(() => VoiceStatus = msg);
                await SpeakVoiceAsync(msg).ConfigureAwait(false);
                return;
            }

            OnUi(() => VoiceInput = heard);
            await ProcessVoiceRequestAsync(heard).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnUi(() => VoiceStatus = "Запись остановлена.");
        }
        catch (Exception ex)
        {
            string err = $"Микрофон: {ex.Message}";
            OnUi(() => VoiceStatus = err);
            await PostSystemAsync(err).ConfigureAwait(false);
        }
        finally
        {
            OnUi(() => IsListening = false);
            _listenCts?.Dispose();
            _listenCts = null;
        }
    }

    private async Task ProcessVoiceRequestAsync(string userText)
    {
        IsVoiceBusy = true;
        OnUi(() =>
        {
            VoiceMessages.Add(new VoiceChatMessage { Role = VoiceMessageRole.User, Text = userText });
            VoiceStatus = "Думаю…";
        });

        try
        {
            var (ok, connMsg) = await _voice.RefreshConnectionAsync(Settings).ConfigureAwait(false);
            if (!ok)
            {
                OnUi(() => VoiceStatus = "Запускаю OpenCode…");
                bool started = await OpenCodeLauncher.TryEnsureRunningAsync(
                    VoiceBackendDefaults.ResolveOpenCodeUrl(Settings)).ConfigureAwait(false);
                if (started)
                    (ok, connMsg) = await _voice.RefreshConnectionAsync(Settings).ConfigureAwait(false);
            }

            if (!ok)
            {
                string offline = GetOfflineReply(userText);
                OnUi(() =>
                {
                    VoiceStatus = "OpenCode не запущен — отвечаю локально";
                    VoiceMessages.Add(new VoiceChatMessage
                    {
                        Role = VoiceMessageRole.Assistant,
                        Text = offline,
                        AgentName = "local"
                    });
                });
                await SpeakVoiceAsync(offline).ConfigureAwait(false);
                return;
            }

            OnUi(UpdateOpenCodeAgentsText);

            var (reply, agent) = await _voice.AskAsync(Settings, userText, CancellationToken.None)
                .ConfigureAwait(false);

            OnUi(() =>
            {
                VoiceMessages.Add(new VoiceChatMessage
                {
                    Role = VoiceMessageRole.Assistant,
                    Text = reply,
                    AgentName = agent
                });
                VoiceStatus = agent is not null ? $"Ответ от агента «{agent}»" : "Готово";
            });

            if (VoiceBackendDefaults.SpeakResponses)
                await SpeakVoiceAsync(reply).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string err = $"Произошла ошибка: {ex.Message}";
            OnUi(() => VoiceStatus = err);
            await PostSystemAsync(err).ConfigureAwait(false);
        }
        finally
        {
            OnUi(() => IsVoiceBusy = false);
        }
    }

    private static string GetOfflineReply(string userText)
    {
        string lower = userText.ToLowerInvariant();
        if (lower.Contains("привет") || lower.Contains("здравств"))
            return "Привет! Сейчас сервер OpenCode не запущен. Запустите его, и я смогу полноценно отвечать на вопросы про обход DPI, VPN и настройки.";
        if (lower.Contains("обход") || lower.Contains("dpi") || lower.Contains("zapret"))
            return "Для обхода DPI откройте вкладку Главная и нажмите Включить обход. Для умных подсказок запустите сервер OpenCode.";
        if (lower.Contains("vpn"))
            return "VPN настраивается на вкладке VPN. Скачайте xray и выберите сервер. Для умных ответов запустите OpenCode.";
        return "Я вас услышал, но сервер OpenCode сейчас недоступен. Запустите OpenCode и повторите вопрос.";
    }

    private async Task PostSystemAsync(string text)
    {
        OnUi(() => AddVoiceSystem(text));
        await SpeakVoiceAsync(text).ConfigureAwait(false);
    }

    private async Task SpeakVoiceAsync(string text)
    {
        if (!VoiceBackendDefaults.SpeakResponses || string.IsNullOrWhiteSpace(text))
            return;
        string spoken = VoiceResponseSanitizer.ForSpeech(text, Settings.VoiceTtsLanguage);
        if (spoken.Length == 0) return;
        await _voice.SpeakAsync(SimplifyForSpeech(spoken), CancellationToken.None).ConfigureAwait(false);
    }

    private static string SimplifyForSpeech(string text)
    {
        string s = text.Trim();
        s = s.Replace("127.0.0.1:4096", "локальный сервер", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("opencode serve --port 4096", "запустите сервер OpenCode", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("OpenCode недоступен:", "OpenCode недоступен.", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Подключение не установлено, т.к. конечный компьютер отверг запрос на подключение.",
            "Сервер не запущен. Запустите OpenCode.", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("т.к.", "так как", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("HTTP ", "ошибка ", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private void ClearVoiceChat()
    {
        _voice.StopSpeaking();
        _voice.ResetSession();
        VoiceMessages.Clear();
        _voiceWelcomeSpoken = false;
        SeedVoiceWelcome();
        VoiceStatus = "Диалог очищен. Скажите или напишите новый запрос.";
        ClearVoiceCommand.RaiseCanExecuteChanged();
    }

    private void AddVoiceSystem(string text) =>
        VoiceMessages.Add(new VoiceChatMessage { Role = VoiceMessageRole.System, Text = text });

    private void UpdateOpenCodeAgentsText()
    {
        SpeechCapabilityText = _voice.SpeechCapability;
        string agents = FormatAgents();
        OpenCodeAgentsText = agents.Length > 0
            ? $"Агенты: {agents} · авто-подбор и озвучка всегда включены"
            : "Авто-подбор агента и озвучка всегда включены";
    }

    private void UpdateVoiceAssistantCardStatus()
    {
        string mic = _voice.CanListen
            ? $"Микрофон: {_voice.RecognitionLanguage}"
            : "Микрофон: подключается автоматически";
        string agents = FormatAgents();
        string agentLine = agents.Length > 0 ? $"Агенты: {agents}" : "Агенты: авто-подбор";
        string tts = _voice.TtsLanguageLabel.Length > 0 ? _voice.TtsLanguageLabel : "Русский";
        VoiceAssistantCardStatus = $"{mic} · {agentLine} · озвучка: {tts}";
    }

    private void ReloadVoiceTtsOptions()
    {
        VoiceTtsLanguages.Clear();
        foreach (var opt in VoiceAssistantService.GetTtsOptions())
            VoiceTtsLanguages.Add(opt);

        if (VoiceTtsLanguages.All(o => !o.Code.Equals(Settings.VoiceTtsLanguage, StringComparison.OrdinalIgnoreCase)))
            Settings.VoiceTtsLanguage = "ru-RU";
    }

    private async Task LaunchOpenCodeInTerminalAsync()
    {
        string url = VoiceBackendDefaults.ResolveOpenCodeUrl(Settings);
        VoiceStatus = "Проверяю OpenCode…";

        var progress = new Progress<string>(msg => OnUi(() => VoiceStatus = msg));
        var (ok, msg) = await OpenCodeLauncher.LaunchInTerminalAsync(url, progress).ConfigureAwait(false);

        OnUi(() =>
        {
            if (ok)
            {
                VoiceStatus = msg.Contains("уже работает", StringComparison.OrdinalIgnoreCase)
                    ? msg
                    : "OpenCode запущен. Не закрывайте окно cmd, если оно открылось.";
                Notify?.Invoke("OpenCode", msg);
            }
            else
            {
                VoiceStatus = msg;
                Notify?.Invoke("OpenCode", msg);
            }
        });
    }

    private string FormatAgents() =>
        _voice.KnownAgents.Count > 0
            ? string.Join(", ", _voice.KnownAgents.Take(6))
            : "build, plan";

    private void RaiseVoiceCommandStates()
    {
        SendVoiceCommand.RaiseCanExecuteChanged();
        ToggleMicCommand.RaiseCanExecuteChanged();
        ClearVoiceCommand.RaiseCanExecuteChanged();
    }

    // ---- lifecycle ---------------------------------------------------------

    public async Task InitializeAsync()
    {
        // Apply saved theme
        ThemeManager.ApplyTheme(Settings.Theme);

        bool launchedAfterUpdate = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--launched-after-update", StringComparison.OrdinalIgnoreCase));

        // Clear cooldown if update succeeded (bat launched us with the flag)
        if (launchedAfterUpdate)
        {
            _updater.ClearUpdateCooldown();
            string installDir = AppUpdateInstaller.GetInstallDirectory();
            string? marker = AppUpdateInstaller.ReadInstalledVersionMarker(installDir);
            AppendLog($"Обновление установлено: v{UpdaterService.AppVersion}" +
                      (marker is not null ? $" (маркер: {marker})" : ""));
            if (marker is not null && marker != UpdaterService.AppVersion)
                AppendLog($"Внимание: маркер ({marker}) не совпадает с версией exe ({UpdaterService.AppVersion}).");
        }

        else
        {
            string? failedLog = AppUpdateInstaller.ReadLastUpdateLogTail();
            if (failedLog is not null && failedLog.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("Прошлое обновление не завершилось. Лог:");
                foreach (var line in failedLog.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        AppendLog(line.Trim());
            }
        }

        // Copy engine binaries + data from ClassicData FIRST so the engine
        // check below finds winws2.exe without needing GitHub on a fresh install.
        AutoImportClassicPresets(launchedAfterUpdate);

        ReloadPresets();
        _hostlists.SeedDefaults();
        ReloadCustomTargets();
        ReloadCustomIpRules();
        _engine.GameFilter = Settings.GameFilter;
        _engine.BypassAllSites = Settings.BypassAllSites;

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetName)
                         ?? Presets.FirstOrDefault();

        EngineVersion = _updater.InstalledVersionDisplay ?? "не установлен";

        // Skip app update check if recently updated (cooldown 10 min)
        // This prevents the update loop: bat fails → old exe runs → checks again
        bool recentlyUpdated = _updater.IsRecentlyUpdated();
        if (recentlyUpdated)
        {
            AppendLog("Обновление было недавно — пропускаю проверку (cooldown 10 мин).");
        }
        else if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete)
        {
            await CheckAndUpdateAsync(silent: true);
        }
        else
        {
            await CheckAndUpdateAsync(silent: true);
        }

        // Show changelog if version changed
        ShowChangelogIfUpdated();

        if (Settings.AutostartEngine && CanStart && SelectedPreset is not null)
            await StartAsync();

        _ = ConnectVoiceBackendSilentAsync();
    }

    private void ShowChangelogIfUpdated()
    {
        string currentVersion = UpdaterService.AppVersion;
        string currentKey = $"{currentVersion}+{UpdaterService.AppBuild}";
        string lastSeen = Settings.LastSeenVersion ?? "";

        if (string.Equals(currentKey, lastSeen, StringComparison.OrdinalIgnoreCase))
            return;

        // First launch ever — just mark as seen, don't show
        if (string.IsNullOrEmpty(lastSeen))
        {
            Settings.LastSeenVersion = currentKey;
            _settingsSvc.Save();
            return;
        }

        // Version/build changed — show changelog
        string changelog = GetEmbeddedChangelog();

        Settings.LastSeenVersion = currentKey;
        _settingsSvc.Save();

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var win = new ChangelogWindow(currentVersion, changelog);
            win.ShowDialog();
        });
    }

    private static string GetEmbeddedChangelog()
    {
        return @"✦ Новая иконка приложения
Щит с буквой Z на тёмном фоне — в exe, окне и трее.

✦ Скачать вручную
https://github.com/kayucm21/Zanpet/releases/latest

✦ Обновление
Настройки → Проверить обновления (FTP + GitHub).";
    }

    private void AutoImportClassicPresets(bool force = false)
    {
        try
        {
            string classicDir = AppPaths.ClassicDataDir;
            string presetsDir = Path.Combine(classicDir, "presets");
            if (!Directory.Exists(presetsDir)) return;

            AppendLog("Авто-импорт классических пресетов…");
            ClassicPresetImporter.CopyClassicData(classicDir);
            var result = ClassicPresetImporter.ImportFromDirectory(presetsDir);

            var existing = _presets.All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            int added = 0, updated = 0;
            foreach (var p in result.Presets)
            {
                if (existing.TryGetValue(p.Name, out var old))
                {
                    if (old.Args.SequenceEqual(p.Args)) continue;
                    old.Args = p.Args;
                    old.UsesHostlist = p.UsesHostlist;
                    updated++;
                }
                else
                {
                    _presets.AddUser(p);
                    added++;
                    existing[p.Name] = p;
                }
            }

            if (added > 0 || updated > 0)
            {
                ReloadPresets();
                if (added > 0) AppendLog($"Загружено {added} классических пресетов из zapret2-youtube-discord.");
                if (updated > 0) AppendLog($"Обновлено {updated} классических пресетов (новый формат).");
                if (result.Errors.Count > 0)
                    foreach (var err in result.Errors)
                        AppendLog("Ошибка загрузки пресета: " + err);
            }
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка авто-импорта классических пресетов: " + ex.Message);
        }
    }

    public async Task CheckAndUpdateAsync(bool silent)
    {
        if (IsUpdating) return;
        IsUpdating = true;
        try
        {
            UpdateStatus = "Проверка обновлений…";
            AppendLog("Проверка обновлений…");

            // --- 1. Check engine update (bol-van/zapret2) ---
            ReleaseInfo? latest = null;
            try
            {
                latest = await _updater.FetchLatestAsync();
                AppendLog($"Движок GitHub: {latest.Tag}");
            }
            catch (Exception ex)
            {
                string msg = _updater.IsEngineInstalled
                    ? $"Не удалось проверить обновления движка ({ex.Message}). Работаем на установленной версии."
                    : $"Нет связи с GitHub: {ex.Message}";
                UpdateStatus = msg;
                AppendLog(msg);
                if (!silent)
                    MessageBox.Show(msg, "Обновление движка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (latest is not null)
            {
                bool needsUpdate = !_updater.IsEngineInstalled || _updater.IsUpdateAvailable(latest);
                if (needsUpdate)
                {
                    AppendLog($"Доступно обновление движка: {latest.Tag}. Загрузка…");
                    bool wasRunning = IsRunning;
                    if (wasRunning)
                    {
                        AppendLog("Остановка движка для обновления…");
                        _engine.Stop();
                        await Task.Delay(800);
                    }

                    var progress = new Progress<UpdateProgress>(p =>
                    {
                        UpdateProgress = p.Fraction;
                        UpdateStatus = p.Message;
                        AppendLog(p.Message);
                    });
                    try
                    {
                        await _updater.InstallAsync(latest, progress);
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus = $"Не удалось установить движок: {ex.Message}";
                        AppendLog("Ошибка загрузки движка: " + ex.Message);
                        if (!silent)
                            MessageBox.Show($"Ошибка загрузки движка: {ex.Message}", "Обновление", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    if (_updater.IsEngineInstalled)
                    {
                        EngineVersion = _updater.InstalledVersionDisplay ?? "—";
                        UpdateStatus = $"Движок обновлён: {latest.Tag}";
                        AppendLog($"Движок обновлён: {latest.Tag}");
                        OnPropertyChanged(nameof(CanStart));
                        RaiseCommandStates();

                        if (wasRunning && CanStart) await StartAsync();
                    }
                }
                else
                {
                    string installed = _updater.InstalledVersion ?? "—";
                    UpdateStatus = $"Движок актуален: {installed} (GitHub: {latest.Tag})";
                    AppendLog($"Движок актуален: {installed} (GitHub: {latest.Tag})");
                }
            }

            // --- 2. Check app update (FTP + GitHub) ---
            try
            {
                var ftpCfg = FtpUpdateSettings.Resolve(Settings);
                var snap = await _updater.FetchAppUpdateSnapshotAsync(ftpCfg);

                AppUpdateFtpVersion = snap.FtpDisplay;
                AppUpdateGitHubVersion = snap.GitHubDisplay;
                AppendLog($"Приложение: установлено v{snap.CurrentVersion} | FTP: {snap.FtpDisplay} | GitHub: {snap.GitHubDisplay}");

                if (snap.HasUpdate && snap.NewestRelease is { } appInfo)
                {
                    string sourceLabel = appInfo.Source switch
                    {
                        AppReleaseSource.Ftp => "FTP",
                        AppReleaseSource.Yandex => "Yandex Disk",
                        AppReleaseSource.Cdn => "CDN",
                        _ => "GitHub",
                    };
                    AppUpdateAvailable = $"v{appInfo.Tag} ({sourceLabel})";
                    string ghLink = "https://github.com/kayucm21/Zanpet/releases/latest";
                    string notes = string.IsNullOrWhiteSpace(appInfo.Notes)
                        ? "Новая иконка и исправления."
                        : appInfo.Notes.Trim();
                    string appMsg = $"Доступно обновление: v{appInfo.Tag} ({sourceLabel}) | FTP: {snap.FtpDisplay} | GitHub: {snap.GitHubDisplay}";
                    UpdateStatus = appMsg;
                    AppendLog(appMsg);

                    bool doInstall = false;
                    if (silent)
                    {
                        doInstall = true;
                    }
                    else
                    {
                        var result = MessageBox.Show(
                            $"Установлено: v{snap.CurrentVersion} (сборка {snap.InstalledBuild})\nFTP: {snap.FtpDisplay}\nGitHub: {snap.GitHubDisplay}\n\nДоступно: v{appInfo.Tag} (сборка {appInfo.Build})\n\n{notes}\n\nСкачать с GitHub:\n{ghLink}\n\nСкачать и установить сейчас?",
                            "Обновление приложения",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes) doInstall = true;
                    }

                    if (doInstall)
                    {
                        UpdateStatus = $"Загрузка v{appInfo.Tag} с {sourceLabel}…";
                        AppendLog($"Загрузка обновления v{appInfo.Tag} с {sourceLabel}…");
                        var progress = new Progress<double>(p =>
                        {
                            UpdateProgress = p;
                            UpdateStatus = $"Загрузка v{appInfo.Tag} ({sourceLabel})… {p:P0}";
                        });
                        try
                        {
                            await _updater.InstallAppUpdateAsync(appInfo, ftpCfg, progress);
                        }
                        catch (Exception ex)
                        {
                            UpdateStatus = $"Ошибка обновления: {ex.Message}";
                            AppendLog($"Ошибка обновления: {ex.Message}");
                            if (!silent)
                                MessageBox.Show($"Ошибка обновления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else if (snap.Ftp is null && snap.GitHub is null)
                {
                    AppUpdateAvailable = "недоступно";
                    string msg = "Не удалось проверить обновление (FTP и GitHub недоступны).";
                    UpdateStatus = msg;
                    AppendLog(msg);
                }
                else
                {
                    AppUpdateAvailable = "актуально";
                    string msg = $"Приложение актуально: v{snap.CurrentVersion} | FTP: {snap.FtpDisplay} | GitHub: {snap.GitHubDisplay}";
                    UpdateStatus = msg;
                    AppendLog(msg);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка проверки обновления приложения: {ex.Message}");
            }
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---- actions -----------------------------------------------------------

    private async Task StartAsync()
    {
        if (SelectedPreset is null)
        {
            AppendLog("Не выбран пресет.");
            return;
        }
        try
        {
            if (Settings.DiscordWebHosts && PresetHasService(SelectedPreset, "Discord"))
                _discordHosts.Apply();
            if (Settings.TelegramWebHosts && PresetHasService(SelectedPreset, "Telegram"))
                _telegramHosts.Apply();
            if (Settings.TikTokWebHosts)
                _socialHosts.ApplyTikTok();
            if (Settings.InstagramWebHosts)
                _socialHosts.ApplyInstagram();
            if (Settings.WhatsAppWebHosts)
                _socialHosts.ApplyWhatsApp();

            _engine.Start(SelectedPreset, SelectedPreset.UsesHostlist ? null : null);
            RunningPreset = SelectedPreset;

            bool discordInPreset = PresetHasService(SelectedPreset, "Discord");
            bool shopBridgeOk = false;
            if (discordInPreset && Settings.DiscordShopBridge)
                shopBridgeOk = await _discordShopBridge.StartAsync().ConfigureAwait(false);

            _discordDesktop.UseShopVpnProxy = shopBridgeOk;

            var desktopTasks = new List<Task>();

            if (Settings.DiscordDesktopAutoLaunch && discordInPreset)
            {
                desktopTasks.Add(Task.Run(async () =>
                {
                    await Task.Delay(shopBridgeOk ? 500 : 300);
                    try { await _discordDesktop.StartAsync(); }
                    catch (Exception ex) { AppendLog($"Discord Desktop: {ex.Message}"); }
                }));
            }

            if (Settings.TelegramWsProxy && PresetHasService(SelectedPreset, "Telegram"))
            {
                if (!string.IsNullOrWhiteSpace(Settings.TelegramWsProxySecret))
                    _tgWs.Secret = Settings.TelegramWsProxySecret.Trim();
                desktopTasks.Add(Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    try { await _tgWs.StartAsync(); }
                    catch (Exception ex) { AppendLog($"Telegram Desktop: {ex.Message}"); }
                }));
            }

            if (desktopTasks.Count > 0)
                await Task.WhenAll(desktopTasks);
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool PresetHasService(Preset preset, string service) =>
        preset.IncludesService(service);

    private async Task ApplyStrategyAsync()
    {
        if (SelectedPreset is null) return;
        if (!IsRunning) { if (CanStart) await StartAsync(); return; }

        AppendLog($"Смена стратегии -> «{SelectedPreset.Name}». Перезапуск движка…");
        await _engine.StopAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(250);
        if (CanStart) await StartAsync();
    }

    private void DuplicatePreset()
    {
        if (SelectedPreset is null) return;
        var copy = SelectedPreset.Clone();
        copy.Name = SelectedPreset.Name + " (моя копия)";
        _presets.AddUser(copy);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == copy.Name);
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        if (!ConfirmDialog.Show("Удалить пресет?",
                $"Пресет «{p.Name}» будет удалён без возможности восстановления."))
            return;
        _presets.DeleteUser(p);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault();
    }

    private void SavePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        _presets.UpdateUser(p);
        OnPropertyChanged(nameof(CommandPreview));
        AppendLog($"Пресет «{p.Name}» сохранён.");
    }

    private async Task ImportPresetAsync()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импорт стратегии",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;

            int added = 0, skipped = 0;
            foreach (string file in dlg.FileNames)
            {
                try
                {
                    var preset = ClassicPresetImporter.ConvertPresetFile(file);
                    if (preset is null) { skipped++; continue; }
                    _presets.AddUser(preset);
                    added++;
                }
                catch (Exception ex)
                {
                    AppendLog($"Ошибка импорта {Path.GetFileName(file)}: {ex.Message}");
                    skipped++;
                }
            }

            if (added > 0)
            {
                ReloadPresets();
                AppendLog($"Импортировано {added} стратегий.{(skipped > 0 ? $" Пропущено: {skipped}." : "")}");
                SelectedPreset = Presets.LastOrDefault();
            }
            else
            {
                AppendLog($"Импорт завершён. Не удалось импортировать стратегии (пропущено: {skipped}).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка импорта: {ex.Message}");
        }
    }

    public void StopEngine() => _engine.Stop();

    public void Shutdown()
    {
        try { _listenCts?.Cancel(); } catch { }
        try { _voice.Dispose(); } catch { }
        try { _discordHosts.Remove(); } catch { }
        try { _socialHosts.RemoveAll(); } catch { }
        try { _telegramHosts.Remove(); } catch { }
        try { _monitor.Dispose(); } catch { }
        try { _vpn.Dispose(); } catch { }
        try { _engine.Dispose(); } catch { }
    }

    // ---- helpers -----------------------------------------------------------

    private void AppendLog(string line)
    {
        OnUi(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ToggleCommand.RaiseCanExecuteChanged();
        CheckUpdateCommand.RaiseCanExecuteChanged();
        DuplicatePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        ImportPresetCommand.RaiseCanExecuteChanged();
        SimpleToggleCommand.RaiseCanExecuteChanged();
        ApplyStrategyCommand.RaiseCanExecuteChanged();
        HomeToggleCommand.RaiseCanExecuteChanged();
        VpnDownloadXrayCommand.RaiseCanExecuteChanged();
        VpnConnectCommand.RaiseCanExecuteChanged();
        SendVoiceCommand.RaiseCanExecuteChanged();
        ToggleMicCommand.RaiseCanExecuteChanged();
        ClearVoiceCommand.RaiseCanExecuteChanged();
    }

    // ---- VPN actions -------------------------------------------------------

    private async Task VpnDownloadXrayAsync()
    {
        IsVpnBusy = true;
        try
        {
            VpnXrayStatus = "Загрузка xray-core…";
            AppendLog("VPN: загрузка xray-core…");
            var progress = new Progress<double>(p =>
            {
                VpnXrayStatus = $"Загрузка xray-core… {p:P0}";
            });
            await _vpn.DownloadXrayAsync(progress);
            VpnXrayStatus = "Установлен";
            OnPropertyChanged(nameof(VpnXrayShowDownload));
            AppendLog("VPN: xray-core установлен.");
        }
        catch (Exception ex)
        {
            VpnXrayStatus = $"Ошибка: {ex.Message}";
            AppendLog($"VPN ошибка загрузки xray: {ex.Message}");
        }
        finally { IsVpnBusy = false; }
    }

    private async Task VpnConnectAsync(VpnServer? server)
    {
        if (server is null) return;
        try
        {
            if (_vpn.IsConnected)
            {
                foreach (var s in VpnServers) s.IsConnected = false;
                await Task.Run(() => _vpn.Stop());
                VpnConnectedServerName = "";
                VpnStatus = "Отключено.";
                OnPropertyChanged(nameof(IsVpnConnected));
                OnPropertyChanged(nameof(VpnConnectedServerName));
                AppendLog("VPN: отключено.");
                return;
            }

            if (!_vpn.IsXrayInstalled)
            {
                VpnXrayStatus = "Скачивание xray-core…";
                AppendLog("VPN: xray не найден, скачиваю…");
                await _vpn.DownloadXrayAsync();
                VpnXrayStatus = _vpn.IsXrayInstalled ? "Установлен" : "Ошибка";
                OnPropertyChanged(nameof(VpnXrayShowDownload));
                if (!_vpn.IsXrayInstalled)
                {
                    VpnStatus = "Не удалось установить xray-core.";
                    return;
                }
            }

            // Try the selected server first, then fallback to others
            var serversToTry = new List<VpnServer> { server };
            foreach (var s in VpnServers)
            {
                if (s != server) serversToTry.Add(s);
            }

            bool connected = await TryConnectVpnAsync(serversToTry, Settings.DiscordShopVpnBridge, updateUi: true)
                .ConfigureAwait(false);

            if (!connected)
            {
                VpnConnectedServerName = "";
                VpnStatus = "Все серверы недоступны.";
                OnPropertyChanged(nameof(IsVpnConnected));
                OnPropertyChanged(nameof(VpnConnectedServerName));
                AppendLog("VPN: все серверы недоступны. Проверьте интернет и попробуйте снова.");
            }
        }
        catch (Exception ex)
        {
            VpnStatus = $"Ошибка: {ex.Message}";
            AppendLog($"VPN ошибка: {ex.Message}");
        }
    }

    /// <summary>Try servers in order; returns true when xray proxy responds.</summary>
    private async Task<bool> TryConnectVpnAsync(IReadOnlyList<VpnServer> serversToTry, bool discordThroughVpn,
        bool updateUi)
    {
        foreach (var srv in serversToTry)
        {
            if (updateUi)
            {
                foreach (var s in VpnServers) s.IsConnected = false;
                srv.IsConnected = true;
                VpnConnectedServerName = srv.Name;
                VpnStatus = $"Подключение к {srv.Name} ({srv.Network}:{srv.Port})…";
                OnPropertyChanged(nameof(IsVpnConnected));
                OnPropertyChanged(nameof(VpnConnectedServerName));
            }

            AppendLog($"VPN: подключение к {srv.Name} ({srv.Address}:{srv.Port})…");
            await Task.Run(() => _vpn.Start(srv, discordThroughVpn)).ConfigureAwait(false);

            await Task.Delay(3000).ConfigureAwait(false);

            if (!_vpn.IsConnected)
            {
                if (updateUi) srv.IsConnected = false;
                AppendLog($"VPN: {srv.Name} — xray завершился. Пробую следующий сервер…");
                continue;
            }

            if (updateUi)
                VpnStatus = $"Подключение к {srv.Name}. Проверка прокси…";
            AppendLog("VPN: xray работает. Проверяю доступность интернета…");
            bool ok = await _vpn.TestProxyAsync().ConfigureAwait(false);
            if (ok)
            {
                if (updateUi)
                {
                    VpnStatus = $"Подключено к {srv.Name}. Работает!";
                    AppendLog($"VPN: HTTP-прокси работает! Интернет через VPN ({srv.Name}).");
                    if (!discordThroughVpn)
                        AppendLog("VPN: Если сайты не открывается — перезапустите браузер!");
                }
                return true;
            }

            AppendLog($"VPN: {srv.Name} — прокси не отвечает. Останавливаю, пробую другой…");
            await Task.Run(() => _vpn.Stop()).ConfigureAwait(false);
            if (updateUi) srv.IsConnected = false;
            await Task.Delay(500).ConfigureAwait(false);
        }

        return false;
    }

    private async Task VpnPingAllAsync()
    {
        try
        {
            foreach (var s in VpnServers) { s.PingMs = -1; s.PingStatus = "Пинг…"; }
            AppendLog("VPN: пинг серверов…");
            foreach (var s in VpnServers)
            {
                int ms = await _vpn.PingAsync(s.Address, s.Port);
                s.PingMs = ms;
                s.PingStatus = ms >= 0 ? $"{ms} мс" : "нет ответа";
            }
            AppendLog("VPN: пинг завершён.");
        }
        catch (Exception ex)
        {
            AppendLog($"VPN пинг ошибка: {ex.Message}");
        }
    }

    private async Task VpnRefreshAsync()
    {
        try
        {
            VpnStatus = "Обновление серверов…";
            AppendLog("VPN: обновление серверов…");
            var servers = _vpn.GetDefaultServers();
            VpnServers.Clear();
            foreach (var s in servers) VpnServers.Add(s);
            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss dd.MM.yyyy");
            VpnStatus = $"Обновлено. {servers.Count} серверов.";
            AppendLog($"VPN: подписка обновлена. {servers.Count} серверов.");
        }
        catch (Exception ex)
        {
            VpnStatus = $"Ошибка обновления: {ex.Message}";
            AppendLog($"VPN ошибка обновления: {ex.Message}");
        }
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in _presets.All.Where(p => p.IsBuiltIn)) Presets.Add(p);
        OnPropertyChanged(nameof(RecommendedPreset));
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }
}
