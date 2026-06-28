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
        _engine.LogLine += AppendLog;

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

        _vpn.LogLine += line => OnUi(() => AppendLog(line));

        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Preset.GroupTitle)));

        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());

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

            var servers = _vpn.LoadCachedSubscription();
            if (servers.Count == 0)
            {
                AppendLog("VPN: загрузка подписки…");
                servers = await _vpn.FetchSubscriptionAsync(VpnService.VpnSubscriptionUrl);
            }
            VpnServers.Clear();
            foreach (var s in servers) VpnServers.Add(s);
            VpnStatus = servers.Count > 0
                ? $"Загружено {servers.Count} серверов."
                : "Нет серверов.";
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
    public RelayCommand SimpleToggleCommand { get; }
    public RelayCommand SetSimpleModeCommand { get; }
    public RelayCommand SetAdvancedModeCommand { get; }
    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand HomeToggleCommand { get; }
    public RelayCommand TogglePresetArgsCommand { get; }
    public RelayCommand VpnDownloadXrayCommand { get; }
    public RelayCommand VpnConnectCommand { get; }

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

    // ---- VPN ---------------------------------------------------------------

    private string _vpnXrayStatus = "Не установлен";
    public string VpnXrayStatus { get => _vpnXrayStatus; private set => SetField(ref _vpnXrayStatus, value); }

    private string _vpnStatus = "";
    public string VpnStatus { get => _vpnStatus; private set => SetField(ref _vpnStatus, value); }

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
            _engine.Stop();
            await Task.Delay(1000);
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
        // Copy engine binaries + data from ClassicData FIRST so the engine
        // check below finds winws2.exe without needing GitHub on a fresh install.
        AutoImportClassicPresets();

        ReloadPresets();
        _hostlists.SeedDefaults();
        _engine.GameFilter = Settings.GameFilter;
        _engine.BypassAllSites = Settings.BypassAllSites;

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetName)
                         ?? Presets.FirstOrDefault();

        EngineVersion = _updater.InstalledVersion ?? "не установлен";

        if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete)
            await CheckAndUpdateAsync(silent: true);
        else if (Settings.AutoUpdateEngine)
            await CheckAndUpdateAsync(silent: true);

        if (Settings.AutostartEngine && CanStart && SelectedPreset is not null)
            Start();
    }

    private void AutoImportClassicPresets()
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
            ReleaseInfo latest;
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
                return;
            }

            if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete || _updater.IsUpdateAvailable(latest))
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
                    return;
                }
                EngineVersion = _updater.InstalledVersion ?? "—";
                UpdateStatus = $"Движок обновлён: {latest.Tag}";
                AppendLog($"Движок обновлён: {latest.Tag}");
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();

                if (wasRunning && CanStart) Start();
            }
            else
            {
                UpdateStatus = $"Актуальная версия движка: {latest.Tag}";
                AppendLog($"Движок актуален: {latest.Tag}");
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
                    if (!silent)
                    {
                        var result = MessageBox.Show(
                            $"Доступна новая версия приложения: {appInfo.Tag}\n\nОткрыть страницу загрузки?",
                            "Обновление приложения",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes)
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appInfo.Url) { UseShellExecute = true });
                    }
                }
            }
            catch { /* app update check is non-critical */ }
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

        AppendLog($"Смена стратегии → «{SelectedPreset.Name}». Перезапуск движка…");
        _engine.Stop();
        for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
            await Task.Delay(50);
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

    public void StopEngine() => _engine.Stop();

    public void Shutdown()
    {
        try { _monitor.Stop(); } catch { }
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
                _vpn.Stop();
                VpnStatus = "Отключено.";
                OnPropertyChanged(nameof(IsVpnConnected));
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

            _vpn.Start(server);
            VpnStatus = $"Подключено к {server.Name}";
            OnPropertyChanged(nameof(IsVpnConnected));
            AppendLog($"VPN: подключено к {server.Name} ({server.Address}:{server.Port}).");
        }
        catch (Exception ex)
        {
            VpnStatus = $"Ошибка: {ex.Message}";
            AppendLog($"VPN ошибка: {ex.Message}");
        }
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in _presets.All) Presets.Add(p);
        OnPropertyChanged(nameof(RecommendedPreset));
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }
}
