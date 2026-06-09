// ViewModels/MainViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TutzApp.Models;
using TutzApp.Services;

namespace TutzApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly ISystemControlService _sysControl;
        private readonly IProcessMonitorService _processMonitor;
        private readonly IGamepadMonitorService _gamepadMonitor;
        private readonly IKeyboardHookService _keyboardHook;
        private readonly INvidiaVibranceService _nvidiaVibrance;
        private readonly Dispatcher _dispatcher;
        private bool _isInitializing = true;
        private bool _isSaving;

        [ObservableProperty]
        private bool _isGamepadConnected;

        [ObservableProperty]
        private string _gamepadState = "Desconectado";

        [ObservableProperty]
        private int _mouseSpeed = 3;

        [ObservableProperty]
        private string _dpiState = "low";

        [ObservableProperty]
        private string _pollingState = "low";

        [ObservableProperty]
        private int _selectedManualDpi = 800;

        [ObservableProperty]
        private int _selectedManualPolling = 125;

        [ObservableProperty]
        private int _selectedManualSpeed = 10;

        [ObservableProperty]
        private int _autoGameDpi = 8000;

        [ObservableProperty]
        private int _autoGamePolling = 1000;

        [ObservableProperty]
        private int _autoGameSpeed = 3;

        [ObservableProperty]
        private int _autoNormalDpi = 800;

        [ObservableProperty]
        private int _autoNormalPolling = 125;

        [ObservableProperty]
        private int _autoNormalSpeed = 14;

        [ObservableProperty]
        private int _autoGameKeyboardBrightness = 4;

        [ObservableProperty]
        private int _autoNormalKeyboardBrightness = 4;

        [ObservableProperty]
        private int _autoGameKeyboardProfile = 1;

        [ObservableProperty]
        private int _autoNormalKeyboardProfile = 2;

        public System.Collections.Generic.List<int> AvailableDpiOptions { get; } = new() { 800, 1600, 3200, 8000 };
        public System.Collections.Generic.List<int> AvailablePollingOptions { get; } = new() { 125, 250, 500, 1000 };
        public System.Collections.Generic.List<int> AvailableSpeedOptions { get; } = System.Linq.Enumerable.Range(1, 20).ToList();
        public System.Collections.Generic.List<int> AvailableKeyboardProfiles { get; } = new() { 1, 2 };
        public System.Collections.Generic.List<int> AvailableBrightnessOptions { get; } = new() { 0, 1, 2, 3, 4 };
        public ObservableCollection<HidDeviceInfo> KeyboardDevices { get; } = new();
        public ObservableCollection<HidDeviceInfo> MouseDevices { get; } = new();

        [ObservableProperty]
        private int _keyboardProfile = 1;

        [ObservableProperty]
        private string _activeGame = "Nenhum";

        [ObservableProperty]
        private string _currentResolution = "Desconhecida";

        [ObservableProperty]
        private string _statusMessage = "Pronto";

        [ObservableProperty]
        private string _keyboardVidHex = string.Empty;

        [ObservableProperty]
        private string _keyboardPidHex = string.Empty;

        [ObservableProperty]
        private string _keyboardInterfaceId = string.Empty;

        [ObservableProperty]
        private int _selectedKeyboardProfile = 1;

        [ObservableProperty]
        private int _selectedKeyboardBrightness = 4;

        [ObservableProperty]
        private HidDeviceInfo? _selectedKeyboardDevice;

        [ObservableProperty]
        private HidDeviceInfo? _selectedMouseDevice;

        [ObservableProperty]
        private string _mouseVidHex = string.Empty;

        [ObservableProperty]
        private string _mousePidHex = string.Empty;

        [ObservableProperty]
        private string _mouseInterfaceId = string.Empty;

        [ObservableProperty]
        private bool _autoGameProfileSwitch;

        [ObservableProperty]
        private bool _autoResolutionSwitch;

        [ObservableProperty]
        private string _monitoredGamesText = string.Empty;

        [ObservableProperty]
        private int _doublePressIntervalMs = 300;

        [ObservableProperty]
        private bool _isVibranceEnabled = true;

        [ObservableProperty]
        private int _vibranceDefaultDriverLevel = 50;

        public ObservableCollection<string> Logs { get; } = new();
        public ObservableCollection<GamepadShortcut> GamepadShortcuts { get; } = new();
        public ObservableCollection<VibranceAppRule> VibranceAppRules { get; } = new();

        [ObservableProperty]
        private string _newShortcutName = string.Empty;

        [ObservableProperty]
        private string _newShortcutDescription = string.Empty;

        [ObservableProperty]
        private string _newShortcutButton1 = "Nenhum";

        [ObservableProperty]
        private string _newShortcutButton2 = "Nenhum";

        [ObservableProperty]
        private string _newShortcutButton3 = "Nenhum";

        [ObservableProperty]
        private string _newShortcutButton4 = "Nenhum";

        [ObservableProperty]
        private string _newShortcutTriggerMode = "Press";

        [ObservableProperty]
        private string _newShortcutActionType = "System";

        [ObservableProperty]
        private string _newShortcutActionValue = "VirtualKeyboard";

        [ObservableProperty]
        private string _newShortcutCustomAction = string.Empty;

        public System.Collections.Generic.List<string> AvailableButtons { get; } = new() { "Nenhum", "BACK", "START", "A", "B", "X", "Y", "LB", "RB", "LS", "RS", "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT" };
        public System.Collections.Generic.List<string> TriggerModes { get; } = new() { "Press", "DoublePress", "Hold3s" };
        public System.Collections.Generic.List<string> ActionTypes { get; } = new() { "System", "RunExe", "SendKeys" };
        public System.Collections.Generic.List<string> SystemActions { get; } = new() { "VirtualKeyboard", "ToggleKeyboardProfile", "PowerOffMonitor", "ToggleGamepadHelp", "VolumeDown", "VolumeUp", "ShowGameBar", "Screenshot" };

        public bool IsAutoStartEnabled
        {
            get => _sysControl.IsAutoStartEnabled();
            set
            {
                if (_sysControl.IsAutoStartEnabled() != value)
                {
                    _sysControl.SetAutoStart(value);
                    OnPropertyChanged(nameof(IsAutoStartEnabled));
                }
            }
        }

        public AppConfig Config => _sysControl.Config;

        public MainViewModel(
            ISystemControlService sysControl,
            IProcessMonitorService processMonitor,
            IKeyboardHookService keyboardHook)
            : this(sysControl, processMonitor, new NoopGamepadMonitorService(), keyboardHook, new NoopNvidiaVibranceService())
        {
        }

        public MainViewModel(
            ISystemControlService sysControl,
            IProcessMonitorService processMonitor,
            IGamepadMonitorService gamepadMonitor,
            IKeyboardHookService keyboardHook,
            INvidiaVibranceService nvidiaVibrance)
        {
            _sysControl = sysControl;
            _processMonitor = processMonitor;
            _gamepadMonitor = gamepadMonitor;
            _keyboardHook = keyboardHook;
            _nvidiaVibrance = nvidiaVibrance;
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Inscreve nos eventos do serviço
            _sysControl.StatusChanged += UpdateStatusFromService;
            _sysControl.LogAdded += AddLogEntry;

            // Carrega estado inicial
            UpdateStatusFromService();

            // Carrega valores editáveis das configurações
            KeyboardVidHex = Config.Keyboard.VidHex;
            KeyboardPidHex = Config.Keyboard.PidHex;
            KeyboardInterfaceId = Config.Keyboard.InterfaceId;
            SelectedKeyboardBrightness = Config.Keyboard.ManualBrightness;
            MouseVidHex = Config.Mouse.VidHex;
            MousePidHex = Config.Mouse.PidHex;
            MouseInterfaceId = Config.Mouse.InterfaceId;

            // Carrega automações
            AutoGameProfileSwitch = Config.Monitoring.AutoGameProfileSwitch;
            AutoResolutionSwitch = Config.Monitoring.AutoResolutionSwitch;
            DoublePressIntervalMs = Config.Monitoring.DoublePressIntervalMs;
            MonitoredGamesText = string.Join(", ", Config.Monitoring.Games);

            // Carrega valores dos perfis de automação
            AutoGameDpi = Config.Mouse.DpiHigh;
            AutoGamePolling = Config.Mouse.PollingHigh;
            AutoGameSpeed = Config.Mouse.SlowSpeed;
            AutoGameKeyboardBrightness = Config.Keyboard.Profile1Brightness;
            AutoNormalDpi = Config.Mouse.DpiLow;
            AutoNormalPolling = Config.Mouse.PollingLow;
            AutoNormalSpeed = Config.Mouse.HighSpeed;
            AutoNormalKeyboardBrightness = Config.Keyboard.Profile2Brightness;
            AutoGameKeyboardProfile = Config.Keyboard.AutoGameProfile;
            AutoNormalKeyboardProfile = Config.Keyboard.AutoNormalProfile;

            // Inicializa valores manuais atuais
            SelectedManualDpi = Config.Mouse.DpiLow;
            SelectedManualPolling = Config.Mouse.PollingLow;
            SelectedManualSpeed = Config.Mouse.HighSpeed;
            SelectedKeyboardProfile = 1;

            IsVibranceEnabled = Config.Vibrance.Enabled;
            VibranceDefaultDriverLevel = Config.Vibrance.DefaultDriverLevel;
            foreach (var rule in Config.Vibrance.AppRules)
            {
                rule.PropertyChanged += VibranceRule_PropertyChanged;
                VibranceAppRules.Add(rule);
            }
            VibranceAppRules.CollectionChanged += VibranceAppRules_CollectionChanged;

            // Carrega atalhos do gamepad
            if (Config.GamepadShortcuts != null)
            {
                foreach (var sc in Config.GamepadShortcuts)
                {
                    GamepadShortcuts.Add(sc);
                }
            }

            // Mensagem de boas vindas nos logs
            _sysControl.LogDebug("Projeto Tutz inicializado com sucesso.");
            RefreshHidDevices();
            _isInitializing = false;
        }

        private sealed class NoopGamepadMonitorService : IGamepadMonitorService
        {
            public void StartMonitoring(System.Threading.CancellationToken cancellationToken) { }
            public void RefreshSettings() { }
        }

        private sealed class NoopNvidiaVibranceService : INvidiaVibranceService
        {
            public bool IsAvailable => false;
            public void StartMonitoring(System.Threading.CancellationToken cancellationToken) { }
            public void RefreshSettings() { }
            public void RestoreDefault() { }
        }

        private void UpdateStatusFromService()
        {
            Action action = () =>
            {
                var status = _sysControl.Status;
                IsGamepadConnected = status.IsGamepadConnected;
                GamepadState = status.GamepadState;
                MouseSpeed = status.MouseSpeed;
                DpiState = status.DpiState;
                PollingState = status.PollingState;
                KeyboardProfile = status.KeyboardProfile;
                ActiveGame = status.ActiveGame;
                CurrentResolution = status.CurrentResolution;
                StatusMessage = status.StatusMessage;

                // Sincronizar dropdowns manuais com o status atual do hardware
                SelectedManualDpi = status.DpiState == "high" ? Config.Mouse.DpiHigh : Config.Mouse.DpiLow;
                SelectedManualPolling = status.PollingState == "high" ? Config.Mouse.PollingHigh : Config.Mouse.PollingLow;
                SelectedManualSpeed = status.MouseSpeed;
            };

            if (System.Windows.Application.Current == null || _dispatcher.CheckAccess())
            {
                action();
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                try
                {
                    _dispatcher.BeginInvoke(action);
                }
                catch (InvalidOperationException)
                {
                    // O Dispatcher pode encerrar entre a checagem e o BeginInvoke.
                }
            }
        }

        private void AutoSaveKeyboardSettings()
        {
            if (_isInitializing || _isSaving) return;
            SaveKeyboardSettings();
        }

        private void AutoSaveMouseSettings()
        {
            if (_isInitializing || _isSaving) return;
            SaveMouseSettings();
        }

        private void AutoSaveAutomationSettings()
        {
            if (_isInitializing || _isSaving) return;
            SaveAutomationSettings();
        }

        private void AutoSaveVibranceSettings()
        {
            if (_isInitializing || _isSaving) return;
            SaveVibranceSettings();
        }

        partial void OnKeyboardVidHexChanged(string value) => AutoSaveKeyboardSettings();
        partial void OnKeyboardPidHexChanged(string value) => AutoSaveKeyboardSettings();
        partial void OnKeyboardInterfaceIdChanged(string value) => AutoSaveKeyboardSettings();
        partial void OnSelectedKeyboardBrightnessChanged(int value) => AutoSaveKeyboardSettings();
        partial void OnMouseVidHexChanged(string value) => AutoSaveMouseSettings();
        partial void OnMousePidHexChanged(string value) => AutoSaveMouseSettings();
        partial void OnMouseInterfaceIdChanged(string value) => AutoSaveMouseSettings();
        partial void OnSelectedKeyboardDeviceChanged(HidDeviceInfo? value)
        {
            if (value == null) return;
            KeyboardVidHex = value.VidHex;
            KeyboardPidHex = value.PidHex;
            KeyboardInterfaceId = value.InterfaceId;
            Config.Keyboard.DevicePath = value.DevicePath;
            AutoSaveKeyboardSettings();
        }

        partial void OnSelectedMouseDeviceChanged(HidDeviceInfo? value)
        {
            if (value == null) return;
            MouseVidHex = value.VidHex;
            MousePidHex = value.PidHex;
            MouseInterfaceId = value.InterfaceId;
            Config.Mouse.DevicePath = value.DevicePath;
            AutoSaveMouseSettings();
        }
        partial void OnAutoGameProfileSwitchChanged(bool value) => AutoSaveAutomationSettings();
        partial void OnAutoResolutionSwitchChanged(bool value) => AutoSaveAutomationSettings();
        partial void OnMonitoredGamesTextChanged(string value) => AutoSaveAutomationSettings();
        partial void OnDoublePressIntervalMsChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoGameDpiChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoGamePollingChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoGameSpeedChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoNormalDpiChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoNormalPollingChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoNormalSpeedChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoGameKeyboardBrightnessChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoNormalKeyboardBrightnessChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoGameKeyboardProfileChanged(int value) => AutoSaveAutomationSettings();
        partial void OnAutoNormalKeyboardProfileChanged(int value) => AutoSaveAutomationSettings();
        partial void OnIsVibranceEnabledChanged(bool value) => AutoSaveVibranceSettings();
        partial void OnVibranceDefaultDriverLevelChanged(int value) => AutoSaveVibranceSettings();

        private void VibranceRule_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VibranceAppRule.AppVibrancePercent))
            {
                AutoSaveVibranceSettings();
            }
        }

        private void VibranceAppRules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (VibranceAppRule rule in e.NewItems)
                {
                    rule.PropertyChanged += VibranceRule_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (VibranceAppRule rule in e.OldItems)
                {
                    rule.PropertyChanged -= VibranceRule_PropertyChanged;
                }
            }
        }

        private void AddLogEntry(string logLine)
        {
            Action action = () =>
            {
                Logs.Insert(0, logLine); // Adiciona no início (mais recente primeiro)
                if (Logs.Count > 100)
                {
                    Logs.RemoveAt(Logs.Count - 1);
                }
            };

            if (System.Windows.Application.Current == null || _dispatcher.CheckAccess())
            {
                action();
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                try
                {
                    _dispatcher.BeginInvoke(action);
                }
                catch (InvalidOperationException)
                {
                    // O Dispatcher pode encerrar entre a checagem e o BeginInvoke.
                }
            }
        }

        [RelayCommand]
        private void ToggleKeyboardProfile()
        {
            _sysControl.ToggleKeyboardProfile();
        }

        [RelayCommand]
        private void ApplyManualKeyboardSettings()
        {
            _sysControl.ApplyKeyboardSettings(SelectedKeyboardProfile, SelectedKeyboardBrightness);
        }

        [RelayCommand]
        private void RefreshHidDevices()
        {
            try
            {
                var devices = _sysControl.EnumerateHidDevices();
                KeyboardDevices.Clear();
                MouseDevices.Clear();

                foreach (var device in devices)
                {
                    if (IsKeyboardCandidate(device))
                    {
                        KeyboardDevices.Add(device);
                    }

                    if (IsMouseCandidate(device))
                    {
                        MouseDevices.Add(device);
                    }
                }

                SelectedKeyboardDevice = PickKeyboardDevice();
                SelectedMouseDevice = PickMouseDevice();
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RefreshHidDevices: erro ao enumerar HID: {ex.Message}");
            }
        }

        private static bool IsKeyboardCandidate(HidDeviceInfo device)
        {
            return device.FeatureReportByteLength >= 65 &&
                (device.InterfaceId.Equals("mi_02", StringComparison.OrdinalIgnoreCase) ||
                 device.VidHex.Equals("3151", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsMouseCandidate(HidDeviceInfo device)
        {
            return device.FeatureReportByteLength >= 17 &&
                (!device.InterfaceId.Equals("mi_02", StringComparison.OrdinalIgnoreCase) ||
                 device.VidHex.Equals("25A7", StringComparison.OrdinalIgnoreCase));
        }

        private HidDeviceInfo? PickKeyboardDevice()
        {
            return KeyboardDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(Config.Keyboard.DevicePath) && d.DevicePath.Equals(Config.Keyboard.DevicePath, StringComparison.OrdinalIgnoreCase))
                ?? KeyboardDevices.FirstOrDefault(d =>
                    d.VidHex.Equals(Config.Keyboard.VidHex, StringComparison.OrdinalIgnoreCase) &&
                    d.PidHex.Equals(Config.Keyboard.PidHex, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(Config.Keyboard.InterfaceId) || d.InterfaceId.Equals(Config.Keyboard.InterfaceId, StringComparison.OrdinalIgnoreCase)))
                ?? KeyboardDevices.FirstOrDefault(d =>
                    d.VidHex.Equals("3151", StringComparison.OrdinalIgnoreCase) &&
                    d.PidHex.Equals("4015", StringComparison.OrdinalIgnoreCase) &&
                    d.InterfaceId.Equals("mi_02", StringComparison.OrdinalIgnoreCase))
                ?? KeyboardDevices.FirstOrDefault();
        }

        private HidDeviceInfo? PickMouseDevice()
        {
            return MouseDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(Config.Mouse.DevicePath) && d.DevicePath.Equals(Config.Mouse.DevicePath, StringComparison.OrdinalIgnoreCase))
                ?? MouseDevices.FirstOrDefault(d =>
                    d.VidHex.Equals(Config.Mouse.VidHex, StringComparison.OrdinalIgnoreCase) &&
                    d.PidHex.Equals(Config.Mouse.PidHex, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(Config.Mouse.InterfaceId) || d.InterfaceId.Equals(Config.Mouse.InterfaceId, StringComparison.OrdinalIgnoreCase)))
                ?? MouseDevices.FirstOrDefault(d =>
                    d.VidHex.Equals("25A7", StringComparison.OrdinalIgnoreCase) &&
                    d.PidHex.Equals("FA7C", StringComparison.OrdinalIgnoreCase))
                ?? MouseDevices.FirstOrDefault();
        }

        [RelayCommand]
        private void PowerOffMonitor()
        {
            _sysControl.PowerOffMonitor();
        }

        [RelayCommand]
        private void ToggleDpiState()
        {
            string target = DpiState == "high" ? "low" : "high";
            _sysControl.SetDpiState(target);
        }

        [RelayCommand]
        private void SetDpiState(string state)
        {
            _sysControl.SetDpiState(state);
        }

        [RelayCommand]
        private void ApplyManualMouseSettings()
        {
            _sysControl.ApplyMouseSettings(SelectedManualPolling, SelectedManualDpi, SelectedManualSpeed);
        }

        [RelayCommand]
        private void TogglePollingState()
        {
            string target = PollingState == "high" ? "low" : "high";
            _sysControl.SetPollingState(target);
        }

        [RelayCommand]
        private void SetPollingState(string state)
        {
            _sysControl.SetPollingState(state);
        }

        [RelayCommand]
        private void SetMouseSpeedFast()
        {
            _sysControl.SetMouseSpeed(Config.Mouse.HighSpeed);
        }

        [RelayCommand]
        private void SetMouseSpeedSlow()
        {
            _sysControl.SetMouseSpeed(Config.Mouse.SlowSpeed);
        }

        [RelayCommand]
        private void ChangeResolution(string index)
        {
            if (_sysControl.Config.Resolutions.TryGetValue(index, out var resInfo))
            {
                _sysControl.ChangeResolution(resInfo.Width, resInfo.Height, resInfo.RefreshRate);
            }
            else
            {
                _sysControl.LogDebug($"MainViewModel: Resolução com índice '{index}' não encontrada.");
            }
        }

        [RelayCommand]
        private void SaveKeyboardSettings()
        {
            if (!TrySave(() =>
                {
                    Config.Keyboard.VidHex = KeyboardVidHex;
                    Config.Keyboard.PidHex = KeyboardPidHex;
                    Config.Keyboard.InterfaceId = KeyboardInterfaceId;
                    Config.Keyboard.DevicePath = SelectedKeyboardDevice?.DevicePath ?? Config.Keyboard.DevicePath;
                    Config.Keyboard.ManualBrightness = Math.Clamp(SelectedKeyboardBrightness, 0, 4);
                }))
            {
                _sysControl.SetStatusMessage("Falha ao salvar teclado.");
            }
        }

        [RelayCommand]
        private void SaveMouseSettings()
        {
            if (!TrySave(() =>
                {
                    Config.Mouse.VidHex = MouseVidHex;
                    Config.Mouse.PidHex = MousePidHex;
                    Config.Mouse.InterfaceId = MouseInterfaceId;
                    Config.Mouse.DevicePath = SelectedMouseDevice?.DevicePath ?? Config.Mouse.DevicePath;
                }))
            {
                _sysControl.SetStatusMessage("Falha ao salvar mouse.");
            }
        }

        [RelayCommand]
        private void AddVibranceApp()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecionar aplicativo para Digital Vibrance",
                Filter = "Executáveis (*.exe)|*.exe",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string exePath = dialog.FileName;
            string processName = Path.GetFileNameWithoutExtension(exePath);
            if (VibranceAppRules.Any(rule => rule.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)))
            {
                _sysControl.SetStatusMessage("Aplicativo já cadastrado no Digital Vibrance.");
                return;
            }

            var rule = new VibranceAppRule
            {
                DisplayName = GetExecutableDisplayName(exePath),
                ExecutablePath = exePath,
                ProcessName = processName,
                IconPath = ExtractExecutableIcon(exePath),
                AppVibrancePercent = 50
            };

            Config.Vibrance.AppRules.Add(rule);
            VibranceAppRules.Add(rule);
            AutoSaveVibranceSettings();
        }

        [RelayCommand]
        private void RemoveVibranceApp(VibranceAppRule rule)
        {
            if (rule == null) return;

            Config.Vibrance.AppRules.Remove(rule);
            VibranceAppRules.Remove(rule);
            AutoSaveVibranceSettings();
        }

        [RelayCommand]
        private void SaveVibranceSettings()
        {
            if (TrySave(() =>
                {
                    Config.Vibrance.Enabled = IsVibranceEnabled;
                    Config.Vibrance.DefaultDriverLevel = Math.Clamp(VibranceDefaultDriverLevel, 0, 100);
                    VibranceDefaultDriverLevel = Config.Vibrance.DefaultDriverLevel;
                    Config.Vibrance.FocusPollIntervalMs = 2000;
                    Config.Vibrance.AppRules = VibranceAppRules
                        .Select(rule =>
                        {
                            rule.AppVibrancePercent = Math.Clamp(rule.AppVibrancePercent, 0, 100);
                            return rule;
                        })
                        .ToList();
                }))
            {
                _nvidiaVibrance.RefreshSettings();
            }
            else
            {
                _sysControl.SetStatusMessage("Falha ao salvar Digital Vibrance.");
            }
        }

        private static string GetExecutableDisplayName(string exePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                {
                    return versionInfo.ProductName;
                }
            }
            catch { }

            return Path.GetFileNameWithoutExtension(exePath);
        }

        private static string ExtractExecutableIcon(string exePath)
        {
            try
            {
                string iconDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VibranceIcons");
                Directory.CreateDirectory(iconDirectory);

                string iconPath = Path.Combine(iconDirectory, $"{Path.GetFileNameWithoutExtension(exePath)}.png");
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null)
                {
                    return string.Empty;
                }

                using var bitmap = icon.ToBitmap();
                bitmap.Save(iconPath, ImageFormat.Png);
                return iconPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        [RelayCommand]
        private void SaveAutomationSettings()
        {
            if (TrySave(() =>
            {
                Config.Monitoring.AutoGameProfileSwitch = AutoGameProfileSwitch;
                Config.Monitoring.AutoResolutionSwitch = AutoResolutionSwitch;
                Config.Monitoring.DoublePressIntervalMs = Math.Clamp(DoublePressIntervalMs, 120, 1000);
                DoublePressIntervalMs = Config.Monitoring.DoublePressIntervalMs;

                var list = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(MonitoredGamesText))
                {
                    var parts = MonitoredGamesText.Split(new char[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string clean = part.Trim();
                        if (!string.IsNullOrEmpty(clean))
                        {
                            list.Add(clean);
                        }
                    }
                }
                Config.Monitoring.Games = list;

                // Salva configurações de perfil automático
                Config.Mouse.DpiHigh = AutoGameDpi;
                Config.Mouse.PollingHigh = AutoGamePolling;
                Config.Mouse.SlowSpeed = AutoGameSpeed;
                Config.Mouse.DpiLow = AutoNormalDpi;
                Config.Mouse.PollingLow = AutoNormalPolling;
                Config.Mouse.HighSpeed = AutoNormalSpeed;
                Config.Keyboard.Profile1Brightness = Math.Clamp(AutoGameKeyboardBrightness, 0, 4);
                Config.Keyboard.Profile2Brightness = Math.Clamp(AutoNormalKeyboardBrightness, 0, 4);
                Config.Keyboard.AutoGameProfile = AutoGameKeyboardProfile;
                Config.Keyboard.AutoNormalProfile = AutoNormalKeyboardProfile;
            }))
            {
                _gamepadMonitor.RefreshSettings();
            }
            else
            {
                _sysControl.SetStatusMessage("Falha ao salvar automação.");
            }
        }

        private bool TrySave(Action applyChanges)
        {
            if (_isSaving) return false;

            _isSaving = true;
            try
            {
                applyChanges();
                bool saved = _sysControl.SaveAppConfig(Config);
                if (saved)
                {
                    _sysControl.SetStatusMessage("Configurações salvas.");
                }
                return saved;
            }
            finally
            {
                _isSaving = false;
            }
        }

        [RelayCommand]
        private void ClearLogs()
        {
            Logs.Clear();
            try
            {
                SystemControlService.FlushLogQueue();
                string logPath = SystemControlService.GetDebugLogPath();
                if (System.IO.File.Exists(logPath))
                {
                    System.IO.File.WriteAllText(logPath, string.Empty);
                }
                _sysControl.LogDebug("Logs limpos com sucesso.");
                _sysControl.SetStatusMessage("Histórico de logs limpo!");
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Erro ao limpar arquivo de logs: {ex.Message}");
            }
        }

        [RelayCommand]
        private void AddGamepadShortcut()
        {
            if (string.IsNullOrWhiteSpace(NewShortcutName))
            {
                _sysControl.SetStatusMessage("Nome do atalho é obrigatório!");
                return;
            }

            var buttons = new System.Collections.Generic.List<string>();
            if (NewShortcutButton1 != "Nenhum" && !buttons.Contains(NewShortcutButton1)) buttons.Add(NewShortcutButton1);
            if (NewShortcutButton2 != "Nenhum" && !buttons.Contains(NewShortcutButton2)) buttons.Add(NewShortcutButton2);
            if (NewShortcutButton3 != "Nenhum" && !buttons.Contains(NewShortcutButton3)) buttons.Add(NewShortcutButton3);
            if (NewShortcutButton4 != "Nenhum" && !buttons.Contains(NewShortcutButton4)) buttons.Add(NewShortcutButton4);

            if (buttons.Count == 0)
            {
                _sysControl.SetStatusMessage("Selecione pelo menos um botão!");
                return;
            }

            string actionValue = NewShortcutActionType == "System" ? NewShortcutActionValue : NewShortcutCustomAction;
            if (string.IsNullOrWhiteSpace(actionValue))
            {
                _sysControl.SetStatusMessage("Defina o valor da ação!");
                return;
            }

            var newShortcut = new GamepadShortcut
            {
                Name = NewShortcutName,
                Description = NewShortcutDescription,
                Buttons = buttons,
                TriggerMode = NewShortcutTriggerMode,
                ActionType = NewShortcutActionType,
                ActionValue = actionValue
            };

            Config.GamepadShortcuts.Add(newShortcut);
            GamepadShortcuts.Add(newShortcut);

            if (_sysControl.SaveAppConfig(Config))
            {
                _gamepadMonitor.RefreshSettings();
                _sysControl.SetStatusMessage("Atalho adicionado com sucesso!");
                NewShortcutName = string.Empty;
                NewShortcutDescription = string.Empty;
                NewShortcutButton1 = "Nenhum";
                NewShortcutButton2 = "Nenhum";
                NewShortcutButton3 = "Nenhum";
                NewShortcutButton4 = "Nenhum";
                NewShortcutCustomAction = string.Empty;
            }
            else
            {
                _sysControl.SetStatusMessage("Erro ao salvar atalho.");
            }
        }

        [RelayCommand]
        private void DeleteGamepadShortcut(GamepadShortcut shortcut)
        {
            if (shortcut == null) return;

            Config.GamepadShortcuts.Remove(shortcut);
            GamepadShortcuts.Remove(shortcut);

            if (_sysControl.SaveAppConfig(Config))
            {
                _gamepadMonitor.RefreshSettings();
                _sysControl.SetStatusMessage("Atalho removido com sucesso!");
            }
            else
            {
                _sysControl.SetStatusMessage("Erro ao salvar após remover.");
            }
        }
    }
}
