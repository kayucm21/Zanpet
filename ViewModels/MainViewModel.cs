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

    public event Action<string, string>? Notify;

    public AppSettings Settings => _settingsSvc.Settings;

    public MainViewModel()
    {
        _engine.StateChanged += s => OnUi(() => State = s);
        _engine.LogLine += line => OnUi(() => AppendLog(line));

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _engine.Stop(), _ => CanStop);
        ToggleCommand = new RelayCommand(_ => { if (IsRunning) _engine.Stop(); else Start(); },
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

        SimpleToggleCommand = new RelayCommand(_ => SimpleToggle(),
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

        _vpn.LogLine += line => OnUi(() => AppendLog(line));

        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Preset.GroupTitle)));

        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());

        ReloadPresets();

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
        if (CanStart) Start();
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
        set => SetField(ref _selectedTabIndex, value);
    }

    private const int SettingsTabIndex = 2;

    public Preset? RecommendedPreset =>
        Presets.FirstOrDefault(p => p.IsRecommended) ?? Presets.FirstOrDefault();

    private string _simpleStatus = "Нажмите «Включить обход» — приложение применит рекомендуемый набор и запустит DPI-обход.";
    public string SimpleStatus { get => _simpleStatus; private set => SetField(ref _simpleStatus, value); }

    private void SimpleToggle()
    {
        if (IsRunning) { _engine.Stop(); SimpleStatus = "Обход остановлен."; return; }

        var preset = RecommendedPreset;
        if (preset is null) { SimpleStatus = "Движок ещё не установлен — дождитесь загрузки."; return; }
        SelectedPreset = preset;
        SimpleStatus = $"Запускаю обход: «{preset.Name}».";
        Start();
    }

    // ---- auto-heal ---------------------------------------------------------

    private string _autoStatusText = "";
    public string AutoStatusText { get => _autoStatusText; private set => SetField(ref _autoStatusText, value); }

    // ---- lifecycle ---------------------------------------------------------

    public async Task InitializeAsync()
    {
        // Apply saved theme
        ThemeManager.ApplyTheme(Settings.Theme);

        // If launched after self-update, force re-copy engine from ClassicData
        bool forceEngineUpdate = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--launched-after-update", StringComparison.OrdinalIgnoreCase));

        // Copy engine binaries + data from ClassicData FIRST so the engine
        // check below finds winws2.exe without needing GitHub on a fresh install.
        AutoImportClassicPresets(forceEngineUpdate);

        ReloadPresets();
        _hostlists.SeedDefaults();
        _engine.GameFilter = Settings.GameFilter;
        _engine.BypassAllSites = Settings.BypassAllSites;

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetName)
                         ?? Presets.FirstOrDefault();

        EngineVersion = _updater.InstalledVersionDisplay ?? "не установлен";

        if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete)
            await CheckAndUpdateAsync(silent: true);
        else
            await CheckAndUpdateAsync(silent: true);

        // Show changelog if version changed
        ShowChangelogIfUpdated();

        if (Settings.AutostartEngine && CanStart && SelectedPreset is not null)
            Start();
    }

    private void ShowChangelogIfUpdated()
    {
        string currentVersion = UpdaterService.AppVersion;
        string lastSeen = Settings.LastSeenVersion ?? "";

        if (string.Equals(currentVersion, lastSeen, StringComparison.OrdinalIgnoreCase))
            return;

        // First launch ever — just mark as seen, don't show
        if (string.IsNullOrEmpty(lastSeen))
        {
            Settings.LastSeenVersion = currentVersion;
            _settingsSvc.Save();
            return;
        }

        // Version changed — show changelog
        string changelog = GetEmbeddedChangelog();

        Settings.LastSeenVersion = currentVersion;
        _settingsSvc.Save();

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var win = new ChangelogWindow(currentVersion, changelog);
            win.ShowDialog();
        });
    }

    private static string GetEmbeddedChangelog()
    {
        return @"✦ Исправлено самобновление
Bat-файл теперь ждёт завершения процесса (до 30 сек), затем ren + robocopy. Обновление работает надёжно.

✦ Исправлен кросс-поточный вылет
При запуске движка приложение больше не падает.

✦ Стратегии снова работают
Все пресеты запускаются корректно.

✦ VPN: добавлена поддержка grpc
Новый сервер Irkutsk (grpc протокол).

✦ Движок обновлён
zapret2 v1.0.2 (движок 2.0.0).

✦ Светлая тема
Переключение в Настройки → Тема оформления.";
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

                        if (wasRunning && CanStart) Start();
                    }
                }
                else
                {
                    string installed = _updater.InstalledVersion ?? "—";
                    UpdateStatus = $"Движок актуален: {installed} (GitHub: {latest.Tag})";
                    AppendLog($"Движок актуален: {installed} (GitHub: {latest.Tag})");
                }
            }

            // --- 2. Check app update (kayucm21/Zanpet) ---
            try
            {
                var appLatest = await _updater.FetchAppLatestAsync();
                if (appLatest is { } appInfo && UpdaterService.IsAppUpdate(appInfo.Tag))
                {
                    string appMsg = $"Доступно обновление приложения: {appInfo.Tag}";
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
                            $"Доступна новая версия приложения: {appInfo.Tag}\n\nСкачать и установить?",
                            "Обновление приложения",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes) doInstall = true;
                    }

                    if (doInstall)
                    {
                        UpdateStatus = $"Загрузка {appInfo.Tag}…";
                        AppendLog($"Загрузка обновления {appInfo.Tag}…");
                        var progress = new Progress<double>(p =>
                        {
                            UpdateProgress = p;
                            UpdateStatus = $"Загрузка {appInfo.Tag}… {p:P0}";
                        });
                        try
                        {
                            await _updater.InstallAppUpdateAsync(appInfo.Tag, progress);
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
                else if (appLatest is null)
                {
                    string msg = "Не удалось проверить обновление приложения (GitHub недоступен).";
                    UpdateStatus = msg;
                    AppendLog(msg);
                }
                else
                {
                    string msg = $"Приложение актуально: v{UpdaterService.AppVersion}";
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

    private void Start()
    {
        if (SelectedPreset is null)
        {
            AppendLog("Не выбран пресет.");
            return;
        }
        try
        {
            _engine.Start(SelectedPreset, SelectedPreset.UsesHostlist ? null : null);
            RunningPreset = SelectedPreset;
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ApplyStrategyAsync()
    {
        if (SelectedPreset is null) return;
        if (!IsRunning) { if (CanStart) Start(); return; }

        AppendLog($"Смена стратегии -> «{SelectedPreset.Name}». Перезапуск движка…");
        await _engine.StopAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(250);
        if (CanStart) Start();
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

            bool connected = false;
            foreach (var srv in serversToTry)
            {
                foreach (var s in VpnServers) s.IsConnected = false;
                srv.IsConnected = true;
                _vpn.Start(srv);
                VpnConnectedServerName = srv.Name;
                VpnStatus = $"Подключение к {srv.Name} ({srv.Network}:{srv.Port})…";
                OnPropertyChanged(nameof(IsVpnConnected));
                OnPropertyChanged(nameof(VpnConnectedServerName));
                AppendLog($"VPN: подключение к {srv.Name} ({srv.Address}:{srv.Port})…");

                await Task.Delay(3000);

                if (!_vpn.IsConnected)
                {
                    srv.IsConnected = false;
                    AppendLog($"VPN: {srv.Name} — xray завершился. Пробую следующий сервер…");
                    continue;
                }

                VpnStatus = $"Подключение к {srv.Name}. Проверка прокси…";
                AppendLog($"VPN: xray работает. Проверяю доступность интернета…");
                bool ok = await _vpn.TestProxyAsync();
                if (ok)
                {
                    connected = true;
                    VpnStatus = $"Подключено к {srv.Name}. Работает!";
                    AppendLog($"VPN: HTTP-прокси работает! Интернет через VPN ({srv.Name}).");
                    AppendLog("VPN: Если сайты не открывается — перезапустите браузер!");
                    break;
                }
                else
                {
                    AppendLog($"VPN: {srv.Name} — прокси не отвечает. Останавливаю, пробую другой…");
                    await Task.Run(() => _vpn.Stop());
                    srv.IsConnected = false;
                    await Task.Delay(500);
                }
            }

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
