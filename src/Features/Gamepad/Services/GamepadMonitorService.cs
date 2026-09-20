using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    public class GamepadMonitorService : IGamepadMonitorService
    {
        private static readonly IntPtr SimulatedKeySignature = new IntPtr(0x12345);
        private const double MouseModeBaseMaxPixelsPerSecond = 800;
        private const double MouseModeTimedAccelerationThreshold = 0.95;
        private const double MouseModeTimedAccelerationDurationSeconds = 0.5;
        private const double MouseModeTimedAccelerationMaxMultiplier = 1.5;
        private const ushort VirtualKeyLeftControl = 0xA2;
        private const ushort VirtualKeyLeftShift = 0xA0;
        private readonly ISystemControlService _sysControl;
        private readonly bool _forceKillOnly;
        private readonly RawInputGamepadStateProvider _rawInputProvider;
        private readonly RtssOsdController _rtssOsdController;
        private readonly GuideGestureRecognizer _guideRecognizer;
        private readonly GameInputGuideMonitor _gameInputGuideMonitor;
        private readonly GameBarMonitor _gameBarMonitor;
        private readonly object _inputStateLock = new();
        private readonly object _virtualKeyboardActionLock = new();
        private bool _gameBarVkMappingRequested;
        private bool _virtualKeyboardVkMappingRequested;
        private bool _virtualKeyboardObservedOpen;
        private long _lastVirtualKeyboardCheck;
        private bool _taskViewVkMappingRequested;
        private bool _taskViewWindowMappingRequested;
        private bool _lastTaskViewWindowDetected;
        private bool _nativeVkMappingOverrideActive;
        private bool _temporaryVkMappingWasMissing;
        private int _temporaryVkMappingOriginalValue = -1;
        private long _taskViewMappingRequestedAt;

        public enum GamepadTriggerMode
        {
            Press,
            DoublePress,
            Hold3s
        }

        public enum GamepadActionType
        {
            System,
            RunExe,
            SendKeys
        }

        private int _monitorStarted;

        private ushort _prevButtons = 0;
        private bool _backWasUsedAsModifier = false;
        private bool _backPressInProgress = false;
        private bool _backPressHadOtherButtons = false;
        private bool _backDoublePressConsumed = false;
        private bool _hasObservedNeutralFrame = false;
        private bool _startupButtonsSuppressed = false;
        private ushort _lastLoggedBackMask = 0;
        private long _lastBackMaskLogAt = 0;
        private long _lastRtssOsdToggleAt = 0;
        private long _rtssOsdRearmAt = 0;
        private bool _rtssOsdConsumedForBackHold = false;
        private bool _fallbackGuideDown;
        private int _statusMonitorStarted = 0;
        private bool _mouseModeEnabled = false;
        private GamepadInputState _latestInputState = GamepadInputState.Empty;
        private ushort _previousMouseModeButtons = 0;
        private bool _previousLeftTriggerDown = false;
        private bool _previousRightTriggerDown = false;
        private bool _leftMouseDown = false;
        private bool _rightMouseDown = false;
        private bool _ctrlModifierDown = false;
        private bool _shiftModifierDown = false;
        private CancellationTokenSource? _mouseModeLoopCancellation;
        private double _mouseResidualX = 0;
        private double _mouseResidualY = 0;
        private double _wheelResidual = 0;
        private double _mouseHighZoneElapsed = 0;
        private double _mouseHighZoneDirectionX = 0;
        private double _mouseHighZoneDirectionY = 0;

        // Timestamps para duplo clique e hold genérico (usando Environment.TickCount64)
        private long _lastBackPress = 0;
        private readonly Dictionary<ushort, long> _lastButtonPressTimes = new();
        private readonly Dictionary<ushort, CancellationTokenSource> _buttonHoldCancellationSources = new();
        private long _gamepadHelpHoldStartedAt;
        private bool _gamepadHelpHoldTriggered;
        private readonly object _settingsLock = new();
        private int _doublePressIntervalMs;
        private double _mouseModeMaxPixelsPerSecond = MouseModeBaseMaxPixelsPerSecond * 0.67;
        private List<RuntimeShortcut> _runtimeShortcuts = new();

        public event Action? RecognizedGamepadsChanged;

        private sealed class RuntimeShortcut
        {
            public required Models.GamepadShortcut Shortcut { get; init; }
            public ushort ComboMask { get; init; }
            public GamepadTriggerMode TriggerMode { get; init; }
            public GamepadActionType ActionType { get; init; }
        }

        public GamepadMonitorService(ISystemControlService sysControl, bool forceKillOnly = false)
        {
            _sysControl = sysControl;
            _forceKillOnly = forceKillOnly;
            _rawInputProvider = new RawInputGamepadStateProvider(sysControl);
            _rtssOsdController = new RtssOsdController(sysControl.LogDebug);
            _guideRecognizer = new GuideGestureRecognizer(sysControl.LogDebug, sysControl.Config.GameConsole?.GuideHoldThresholdMs ?? 700);
            _gameInputGuideMonitor = new GameInputGuideMonitor(
                HandleGameInputGuideChanged,
                HandleGameInputBChanged,
                sysControl.LogDebug);
            _gameBarMonitor = new GameBarMonitor(sysControl.LogDebug);

            _guideRecognizer.TapDetected += HandleGuideTap;
            _guideRecognizer.HoldDetected += HandleGuideHold;
            _gameBarMonitor.InputRedirectedChanged += HandleGameBarInputRedirectedChanged;

            _rawInputProvider.RecognizedGamepadsChanged += () => RecognizedGamepadsChanged?.Invoke();
            RefreshSettings();
        }

        private void HandleGuideTap()
        {
            SetTaskViewMappingRequested(false);
            _sysControl.LogDebug(
                "GamepadMonitorService: Guide Tap released; native Windows handling remains responsible for the tap.");
        }

        private void HandleGuideHold()
        {
            _sysControl.LogDebug(
                "GamepadMonitorService: Guide Hold detected by GameInput; " +
                "keeping ControllerToVKMapping active for native Windows Win+Tab handling.");
            _sysControl.LaunchFocusAssist?.Disarm("User activated Task View via Guide hold");
        }

        private void HandleGameInputGuideChanged(bool isGuideDown)
        {
            try
            {
                if (isGuideDown)
                {
                    // Enable the mapping on the physical Guide-down transition,
                    // before Windows evaluates the native Guide hold gesture.
                    SetTaskViewMappingRequested(true);
                }

                _guideRecognizer.Update(isGuideDown, Environment.TickCount64);
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitorService: erro no Guide via GameInput: {ex}");
            }
        }

        private void HandleGameInputBChanged(bool isBDown)
        {
            if (isBDown)
            {
                HandleContextExitButtonPressed();
            }
        }

        private void HandleContextExitButtonPressed()
        {
            if (_taskViewVkMappingRequested)
            {
                _sysControl.LogDebug(
                    "GamepadMonitorService: B encerrou o contexto aproximado do Task View.");
                SetTaskViewMappingRequested(false);
            }

            if (_virtualKeyboardVkMappingRequested)
            {
                _sysControl.LogDebug(
                    "GamepadMonitorService: B encerrou o contexto aproximado do teclado virtual; " +
                    "liberando ControllerToVKMapping.");
                SetVirtualKeyboardMappingRequested(false);
            }
        }

        private void HandleGameBarInputRedirectedChanged(bool redirected)
        {
            bool mapGamepadToKeyboard = _sysControl.Config.GameConsole?.MapGamepadToKeyboardInGameBar == true;
            _gameBarVkMappingRequested = redirected && mapGamepadToKeyboard;

            bool nativeVkMappingActive = _nativeVkMappingOverrideActive;
            if (Volatile.Read(ref _monitorStarted) != 0)
            {
                nativeVkMappingActive = ReconcileControllerVkMapping();
            }

            _sysControl.LogDebug(
                $"GamepadMonitorService: GameBar InputRedirected={redirected}, " +
                $"mapKeyboard={mapGamepadToKeyboard}, nativeVkMapping={nativeVkMappingActive}, " +
                $"virtualKeyboardMapping={_virtualKeyboardVkMappingRequested}, " +
                $"taskViewMapping={_taskViewVkMappingRequested}.");
            if (redirected)
            {
                _sysControl.LaunchFocusAssist?.Disarm("User focused Game Bar overlay");
            }
        }

        public void ToggleVirtualKeyboardMappingRequested()
        {
            lock (_virtualKeyboardActionLock)
            {
                SetVirtualKeyboardMappingRequested(!_virtualKeyboardVkMappingRequested);
            }
        }

        private void ToggleVirtualKeyboard()
        {
            lock (_virtualKeyboardActionLock)
            {
                bool requested = !_virtualKeyboardVkMappingRequested;
                _sysControl.LogDebug(
                    $"GamepadMonitorService: Select+X -> " +
                    $"{(requested ? "abrindo" : "fechando")} teclado virtual; " +
                    $"ControllerToVKMapping request={requested}.");
                SetVirtualKeyboardMappingRequested(requested);

                // O estado do mapping já foi decidido acima. A chamada abaixo só
                // alterna a superfície do OSK; não pode inverter o mapping outra vez.
                _sysControl.InvokeTouchKeyboard(manageControllerVkMapping: false);
            }
        }

        public void ReleaseVirtualKeyboardMappingRequested()
        {
            lock (_virtualKeyboardActionLock)
            {
                if (_virtualKeyboardVkMappingRequested)
                {
                    _sysControl.LogDebug(
                        "GamepadMonitorService: encerramento do teclado virtual detectado pelo Escape; " +
                        "liberando ControllerToVKMapping.");
                    SetVirtualKeyboardMappingRequested(false);
                }
            }
        }

        private void SetVirtualKeyboardMappingRequested(bool requested)
        {
            lock (_virtualKeyboardActionLock)
            {
                _virtualKeyboardVkMappingRequested = requested;
                _virtualKeyboardObservedOpen = false;
                _sysControl.LogDebug(
                    $"GamepadMonitorService: virtual keyboard ControllerToVKMapping request={_virtualKeyboardVkMappingRequested}.");
                if (Volatile.Read(ref _monitorStarted) != 0)
                {
                    ReconcileControllerVkMapping();
                }
            }
        }

        private void SetTaskViewMappingRequested(bool requested)
        {
            _taskViewVkMappingRequested = requested;
            if (requested)
            {
                _taskViewMappingRequestedAt = Environment.TickCount64;
            }

            _sysControl.LogDebug(
                $"GamepadMonitorService: Task View ControllerToVKMapping request={requested}.");
            if (Volatile.Read(ref _monitorStarted) != 0)
            {
                ReconcileControllerVkMapping();
            }
        }

        private bool ReconcileControllerVkMapping()
        {
            bool shouldEnable =
                _gameBarVkMappingRequested ||
                _virtualKeyboardVkMappingRequested ||
                _taskViewVkMappingRequested ||
                _taskViewWindowMappingRequested;
            if (shouldEnable)
            {
                if (_nativeVkMappingOverrideActive)
                {
                    if (ControllerVkMapping.GetCurrentState() != 1)
                    {
                        ControllerVkMapping.SetMappingEnabled(
                            enabled: true,
                            out _,
                            out _);
                        _sysControl.LogDebug(
                            "GamepadMonitorService: ControllerToVKMapping reapplied while a transient UI requested it.");
                    }

                    return true;
                }

                bool enabled = ControllerVkMapping.SetMappingEnabled(
                    enabled: true,
                    out bool wasMissing,
                    out int originalValue);
                _sysControl.LogDebug(
                    $"GamepadMonitorService: transient ControllerToVKMapping enable={enabled}, " +
                    $"GameBar={_gameBarVkMappingRequested}, VirtualKeyboard={_virtualKeyboardVkMappingRequested}, " +
                    $"TaskView={_taskViewVkMappingRequested}, TaskViewWindow={_taskViewWindowMappingRequested}, " +
                    $"WasMissing={wasMissing}, OriginalVal={originalValue}.");
                if (enabled)
                {
                    _temporaryVkMappingWasMissing = wasMissing;
                    _temporaryVkMappingOriginalValue = originalValue;
                    _nativeVkMappingOverrideActive = true;
                }

                return _nativeVkMappingOverrideActive;
            }

            if (!_nativeVkMappingOverrideActive)
            {
                return false;
            }

            bool suppressWindowsMapping =
                _sysControl.Config.GameConsole?.SuppressWindowsControllerVkMapping == true;
            bool baseWasMissing = suppressWindowsMapping
                ? false
                : _sysControl.Config.GameConsole?.WasVkMappingOriginallyMissing
                    ?? _temporaryVkMappingWasMissing;
            int baseOriginalValue = suppressWindowsMapping
                ? 0
                : _sysControl.Config.GameConsole?.OriginalVkMappingValue
                    ?? _temporaryVkMappingOriginalValue;
            bool restored = suppressWindowsMapping
                ? ControllerVkMapping.SetMappingEnabled(false, out _, out _)
                : ControllerVkMapping.RestoreOriginal(baseWasMissing, baseOriginalValue);

            _sysControl.LogDebug(
                $"GamepadMonitorService: transient ControllerToVKMapping restore={restored}, " +
                $"SuppressWindowsMapping={suppressWindowsMapping}.");
            if (restored)
            {
                _nativeVkMappingOverrideActive = false;
            }

            return false;
        }

        public bool RestoreNativeVkMappingOverrideForShutdown()
        {
            _gameBarVkMappingRequested = false;
            _virtualKeyboardVkMappingRequested = false;
            _taskViewVkMappingRequested = false;
            _taskViewWindowMappingRequested = false;
            _lastTaskViewWindowDetected = false;
            if (!_nativeVkMappingOverrideActive)
            {
                return true;
            }

            bool restored = ReconcileControllerVkMapping();
            _sysControl.LogDebug(
                $"GamepadMonitorService: shutdown restaurou o estado anterior do ControllerToVKMapping. " +
                $"Result={!_nativeVkMappingOverrideActive && !restored}.");
            return !_nativeVkMappingOverrideActive;
        }

        public void RefreshSettings()
        {
            if (_guideRecognizer != null && _sysControl.Config.GameConsole != null)
            {
                _guideRecognizer.HoldThresholdMs = _sysControl.Config.GameConsole.GuideHoldThresholdMs;
            }

            var shortcuts = new List<RuntimeShortcut>();
            foreach (var shortcut in _sysControl.Config.GamepadShortcuts)
            {
                if (_forceKillOnly && !IsForceKillShortcut(shortcut))
                {
                    continue;
                }

                // START / OPTIONS held for three seconds is a built-in help gesture.
                // It is counted from the interpreted Raw HID state in the polling loop,
                // so keeping the equivalent config entry in the generic runtime list
                // would toggle the overlay twice.
                if (IsBuiltInGamepadHelpShortcut(shortcut))
                {
                    continue;
                }

                ushort comboMask = GetComboMask(shortcut.Buttons);
                if (comboMask == 0) continue;

                shortcuts.Add(new RuntimeShortcut
                {
                    Shortcut = shortcut,
                    ComboMask = comboMask,
                    TriggerMode = ParseTriggerMode(shortcut.TriggerMode),
                    ActionType = ParseActionType(shortcut.ActionType)
                });
            }

            shortcuts.Sort((x, y) => (y.Shortcut.Buttons?.Count ?? 0).CompareTo(x.Shortcut.Buttons?.Count ?? 0));

            lock (_settingsLock)
            {
                _doublePressIntervalMs = Math.Clamp(_sysControl.Config.Monitoring.DoublePressIntervalMs, 120, 1000);
                int sensitivityPercent = Math.Clamp(_sysControl.Config.Gamepad.MouseModeSensitivityPercent, 10, 200);
                _mouseModeMaxPixelsPerSecond = MouseModeBaseMaxPixelsPerSecond * sensitivityPercent / 100.0;
                _runtimeShortcuts = shortcuts;
            }

            // Configuration is also reloaded into the elevated helper while it is running.
            // Reconcile the active profile immediately instead of waiting for the next
            // Game Bar WinRT event (which may never arrive if the overlay is already open).
            HandleGameBarInputRedirectedChanged(_gameBarMonitor.IsInputRedirected);
        }

        private static GamepadTriggerMode ParseTriggerMode(string? mode)
        {
            if (string.IsNullOrEmpty(mode)) return GamepadTriggerMode.Press;
            if (mode.Equals("DoublePress", StringComparison.OrdinalIgnoreCase)) return GamepadTriggerMode.DoublePress;
            if (mode.Equals("Hold3s", StringComparison.OrdinalIgnoreCase)) return GamepadTriggerMode.Hold3s;
            return GamepadTriggerMode.Press;
        }

        private static GamepadActionType ParseActionType(string? actionType)
        {
            if (string.IsNullOrEmpty(actionType)) return GamepadActionType.System;
            if (actionType.Equals("RunExe", StringComparison.OrdinalIgnoreCase)) return GamepadActionType.RunExe;
            if (actionType.Equals("SendKeys", StringComparison.OrdinalIgnoreCase)) return GamepadActionType.SendKeys;
            return GamepadActionType.System;
        }

        private static bool IsForceKillShortcut(Models.GamepadShortcut shortcut)
        {
            return string.Equals(shortcut.ActionType, "System", StringComparison.OrdinalIgnoreCase)
                && string.Equals(shortcut.ActionValue, "ForceKillForegroundApp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBuiltInGamepadHelpShortcut(Models.GamepadShortcut shortcut)
        {
            return string.Equals(shortcut.ActionType, "System", StringComparison.OrdinalIgnoreCase)
                && string.Equals(shortcut.ActionValue, "ToggleGamepadHelp", StringComparison.OrdinalIgnoreCase)
                && string.Equals(shortcut.TriggerMode, "Hold3s", StringComparison.OrdinalIgnoreCase)
                && shortcut.Buttons is { Count: 1 }
                && IsStartOrOptionsName(shortcut.Buttons[0]);
        }

        public void StartMonitoring(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _monitorStarted, 1) != 0)
            {
                _sysControl.LogDebug("GamepadMonitorService: tentativa duplicada de iniciar polling ignorada.");
                return;
            }

            _rawInputProvider.ButtonsChanged += HandleHidButtonsChanged;
            _rawInputProvider.StateChanged += HandleHidStateChanged;
            _rawInputProvider.Start(cancellationToken);
            bool gameInputGuideAvailable = _gameInputGuideMonitor.Start();
            _sysControl.LogDebug(
                $"GamepadMonitorService: fonte do Guide={(gameInputGuideAvailable ? "GameInput" : "HID/XInput fallback")}.");
            _gameBarMonitor.Start();
            HandleGameBarInputRedirectedChanged(_gameBarMonitor.IsInputRedirected);
            _ = Task.Run(() => RunMonitoringAsync(cancellationToken));
        }

        public bool StartCalibration()
        {
            try
            {
                _rawInputProvider.Start(CancellationToken.None);
                var app = System.Windows.Application.Current;
                if (app == null)
                {
                    return _rawInputProvider.StartCalibration(TimeSpan.FromSeconds(30));
                }

                app.Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        var window = new TutzApp.Views.GamepadCalibrationWindow(_rawInputProvider, _sysControl);
                        window.Show();
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"GamepadCalibration: falha ao abrir janela: {ex}");
                        _sysControl.SetStatusMessage("Falha ao abrir calibração do gamepad.");
                    }
                }));
                return true;
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadCalibration: erro ao iniciar: {ex}");
                return false;
            }
        }

        public void StartConnectionStatusMonitoring(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _statusMonitorStarted, 1) != 0)
            {
                _sysControl.LogDebug("GamepadMonitorService: tentativa duplicada de iniciar monitor de status HID ignorada.");
                return;
            }

            _ = Task.Run(() => RunConnectionStatusMonitoringAsync(cancellationToken));
        }

        private async Task RunConnectionStatusMonitoringAsync(CancellationToken cancellationToken)
        {
            _sysControl.LogDebug("GamepadMonitorService: iniciando monitor de status HID do gamepad para a GUI.");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        bool connected = IsGamepadPresentByHidEnumeration();
                        if (connected != _sysControl.Status.IsGamepadConnected)
                        {
                            _sysControl.Status.IsGamepadConnected = connected;
                            _sysControl.Status.GamepadState = connected ? "Conectado" : "Desconectado";
                            _sysControl.LogDebug($"GamepadMonitorService: status HID da GUI alterado para connected={connected}.");
                            _sysControl.NotifyStatusChanged();
                        }
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"GamepadMonitorService: erro no monitor de status HID da GUI: {ex.Message}");
                    }

                    await Task.Delay(2000, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _statusMonitorStarted, 0);
                _sysControl.LogDebug("GamepadMonitorService: monitor de status HID da GUI encerrado.");
            }
        }

        private bool IsGamepadPresentByHidEnumeration()
        {
            var devices = _sysControl.EnumerateHidDevices();
            var recognized = _sysControl.Config.Gamepad?.RecognizedDevices;

            if (recognized != null && recognized.Count > 0)
            {
                foreach (var known in recognized)
                {
                    if (devices.Any(device =>
                        device.VidHex.Equals(known.VidHex, StringComparison.OrdinalIgnoreCase) &&
                        device.PidHex.Equals(known.PidHex, StringComparison.OrdinalIgnoreCase) &&
                        (known.UsagePage == 0 || device.UsagePage == known.UsagePage) &&
                        (known.Usage == 0 || device.Usage == known.Usage)))
                    {
                        return true;
                    }
                }
            }

            return devices.Any(device =>
                device.UsagePage == 0x01 &&
                (device.Usage == 0x04 || device.Usage == 0x05 || device.Usage == 0x08));
        }

        private async Task RunMonitoringAsync(CancellationToken cancellationToken)
        {
            _sysControl.LogDebug(_forceKillOnly
                ? "GamepadMonitorService: Iniciando loop de polling em modo ForceKill-only..."
                : "GamepadMonitorService: Iniciando loop de polling...");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int delayMs = Math.Clamp(_sysControl.Config.Monitoring.GamepadPollIntervalMs, 20, 5000);

                    try
                    {
                        bool connected = PollGamepad();
                        UpdateTaskViewMappingState();
                        UpdateTaskViewWindowMappingState();
                        UpdateVirtualKeyboardComState();
                        if (connected != _sysControl.Status.IsGamepadConnected)
                        {
                            _sysControl.Status.IsGamepadConnected = connected;
                            _sysControl.Status.GamepadState = connected ? "Conectado" : "Desconectado";
                            _sysControl.LogDebug($"GamepadMonitor: Estado de conexão alterado para: {connected}");
                            _sysControl.SetStatusMessage(connected ? "Controle Conectado" : "Controle Desconectado");
                        }

                        // Se não estiver conectado, reduz a taxa de polling para economizar CPU.
                        if (!connected)
                        {
                            delayMs = 1500;
                        }
                        else if (Environment.TickCount64 - _rawInputProvider.LastHidReportAt > 250)
                        {
                            // HID silenciado por jogo com DirectInput exclusivo (ex: The Last Faith). Polling rápido no XInput.
                            delayMs = Math.Min(delayMs, 50);
                        }
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"GamepadMonitorService: erro no polling: {ex}");
                    }

                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Encerramento normal do aplicativo.
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitorService: loop encerrado inesperadamente: {ex}");
            }
            finally
            {
                _rawInputProvider.ButtonsChanged -= HandleHidButtonsChanged;
                _rawInputProvider.StateChanged -= HandleHidStateChanged;
                RestoreNativeVkMappingOverrideForShutdown();
                _rawInputProvider.Dispose();
                _gameInputGuideMonitor.Dispose();
                _gameBarMonitor.Stop();
                lock (_inputStateLock)
                {
                    CancelAllHoldShortcutTimers();
                }
                SetMouseModeEnabled(false);
                _sysControl.LogDebug("GamepadMonitorService: loop de polling encerrado.");
                Interlocked.Exchange(ref _monitorStarted, 0);
            }
        }

        private void UpdateTaskViewMappingState()
        {
            if (!_taskViewVkMappingRequested ||
                Environment.TickCount64 - _taskViewMappingRequestedAt < 30000)
            {
                return;
            }

            _taskViewVkMappingRequested = false;
            _sysControl.LogDebug(
                "GamepadMonitorService: timeout de segurança do Task View; liberando ControllerToVKMapping.");
            ReconcileControllerVkMapping();
        }

        private void UpdateTaskViewWindowMappingState()
        {
            bool detected = IsTaskViewForeground();
            if (detected == _lastTaskViewWindowDetected)
            {
                return;
            }

            _lastTaskViewWindowDetected = detected;
            _taskViewWindowMappingRequested = detected;
            _sysControl.LogDebug(
                $"GamepadMonitorService: Task View window detected={detected}; " +
                "reconciling ControllerToVKMapping.");
            ReconcileControllerVkMapping();
        }

        private void UpdateVirtualKeyboardComState()
        {
            lock (_virtualKeyboardActionLock)
            {
                long now = Environment.TickCount64;
                if (!_virtualKeyboardVkMappingRequested || now - _lastVirtualKeyboardCheck < 100)
                    return;
                _lastVirtualKeyboardCheck = now;
                object? pane = null;
                try
                {
                    pane = new NativeMethods.FrameworkInputPane();
                    if (((NativeMethods.IFrameworkInputPane)pane).Location(out var rect) < 0)
                        return;
                    bool visible = rect.Right > rect.Left && rect.Bottom > rect.Top;
                    if (visible && !_virtualKeyboardObservedOpen)
                    {
                        _virtualKeyboardObservedOpen = true;
                        _sysControl.LogDebug("OSK COM: abertura confirmada; aguardando fechamento.");
                    }
                    else if (!visible && _virtualKeyboardObservedOpen)
                    {
                        _sysControl.LogDebug("OSK COM: fechamento confirmado; liberando ControllerToVKMapping.");
                        SetVirtualKeyboardMappingRequested(false);
                    }
                }
                catch (COMException)
                {
                    // A failed query is not a close notification.
                }
                finally
                {
                    if (pane != null) Marshal.ReleaseComObject(pane);
                }
            }
        }

        private static bool IsTaskViewForeground()
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var className = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            string windowClass = className.ToString();
            if (windowClass.Equals("MultitaskingViewFrame", StringComparison.Ordinal) ||
                windowClass.Equals("TaskSwitcherWnd", StringComparison.Ordinal) ||
                windowClass.Equals("TaskSwitcherOverlayWnd", StringComparison.Ordinal) ||
                windowClass.Equals("ForegroundStaging", StringComparison.Ordinal) ||
                windowClass.Equals("XamlExplorerHostIslandWindow", StringComparison.Ordinal))
            {
                return true;
            }

            int titleLength = NativeMethods.GetWindowTextLength(hwnd);
            if (titleLength <= 0)
            {
                return false;
            }

            var title = new System.Text.StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            return title.ToString().Equals("Task View", StringComparison.OrdinalIgnoreCase) ||
                title.ToString().Equals("Task Switching", StringComparison.OrdinalIgnoreCase);
        }

        private bool PollGamepad()
        {
            bool hidConnected = _rawInputProvider.TryReadButtons(out ushort interpretedButtons);
            bool xinputConnected = TryReadXInputButtons(out ushort xinputButtons);
            bool connected = hidConnected || xinputConnected;

            if (!connected)
            {
                lock (_inputStateLock)
                {
                    ResetButtonTrackingState();
                }

                return false;
            }

            long now = Environment.TickCount64;
            long lastHidReport = _rawInputProvider.LastHidReportAt;
            // Se o HID estiver silenciado por jogos com DirectInput exclusivo (como The Last Faith)
            // ou se ainda não recebemos pacote HID mas o controle está ativo no XInput, usamos o XInput como fallback.
            // Quando o HID está transmitindo normalmente (ex: modo Xbox), lastHidReport é recente (< 250ms),
            // mantendo a detecção 100% pura via HID.
            bool useXInput = (lastHidReport == 0 || (now - lastHidReport) > 250) && xinputConnected;

            if (useXInput)
            {
                lock (_inputStateLock)
                {
                    ProcessButtons(xinputButtons);
                }
            }

            ushort effectiveButtons = useXInput ? xinputButtons : interpretedButtons;

            bool shouldToggleHelp = false;
            if (!_forceKillOnly)
            {
                lock (_inputStateLock)
                {
                    shouldToggleHelp = UpdateGamepadHelpHoldFromInterpretedState(
                        effectiveButtons,
                        now);
                }
            }

            if (shouldToggleHelp)
            {
                _sysControl.LogDebug(
                    "GamepadMonitor: START/OPTIONS permaneceu pressionado por 3s; alternando overlay.");
                ExecuteSystemAction("ToggleGamepadHelp");
            }

            return true;
        }

        private static bool TryReadXInputButtons(out ushort buttons)
        {
            buttons = 0;
            bool anyConnected = false;
            for (uint i = 0; i < 4; i++)
            {
                uint result = NativeMethods.XInputGetStateEx(i, out var state);
                if (result == 0)
                {
                    anyConnected = true;
                    if (state.Gamepad.wButtons != 0)
                    {
                        buttons = state.Gamepad.wButtons;
                        return true;
                    }
                }
            }

            return anyConnected;
        }

        private void HandleHidButtonsChanged(ushort buttons)
        {
            try
            {
                if ((buttons & NativeMethods.XINPUT_GAMEPAD_B) != 0)
                {
                    lock (_virtualKeyboardActionLock)
                    {
                        if (_virtualKeyboardVkMappingRequested)
                        {
                            _sysControl.LogDebug("GamepadMonitorService: RawInput B -> encerrando contexto do teclado virtual.");
                            SetVirtualKeyboardMappingRequested(false);
                        }
                    }
                }
                lock (_inputStateLock)
                {
                    ProcessButtons(buttons);
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitor: erro ao processar evento HID: {ex}");
            }
        }

        private void HandleHidStateChanged(GamepadInputState state)
        {
            try
            {
                lock (_inputStateLock)
                {
                    _latestInputState = state;
                    if (_mouseModeEnabled)
                    {
                        ProcessMouseModeButtons(state);
                    }
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitorService: erro ao processar estado HID: {ex}");
            }
        }

        private bool ProcessButtons(ushort buttons)
        {
            // GameInput expõe Guide pelo canal de botão de sistema. Só use o
            // bit HID/XInput como fallback quando esse canal não estiver disponível.
            bool isGuideDown = (buttons & NativeMethods.XINPUT_GAMEPAD_GUIDE) != 0;
            if (!_gameInputGuideMonitor.IsAvailable)
            {
                if (isGuideDown != _fallbackGuideDown)
                {
                    _fallbackGuideDown = isGuideDown;
                    if (isGuideDown)
                    {
                        SetTaskViewMappingRequested(true);
                    }

                    _guideRecognizer.Update(isGuideDown, Environment.TickCount64);
                }

            }

            buttons = (ushort)(buttons & ~NativeMethods.XINPUT_GAMEPAD_GUIDE);

            // Segurança: não execute atalhos a partir de um estado inicial já pressionado.
            // Isso evita que um relatório HID ambíguo na conexão seja interpretado como
            // combo destrutivo, especialmente BACK+Y/ForceKill no AdminHelper.
            if (!_hasObservedNeutralFrame)
            {
                if (buttons != 0 && !_startupButtonsSuppressed)
                {
                    _startupButtonsSuppressed = true;
                    _sysControl.LogDebug($"GamepadMonitor: estado inicial com botões pressionados usado como baseline. Mask=0x{buttons:X4}");
                }

                _hasObservedNeutralFrame = true;
                _prevButtons = buttons;

                return true;
            }

            // Idle short-circuit
            if (buttons == 0 && _prevButtons == 0)
            {
                return true;
            }

            // Detectar transições (botões recém pressionados e soltos)
            ushort pressedThisFrame = (ushort)(buttons & ~_prevButtons);
            ushort releasedThisFrame = (ushort)(~buttons & _prevButtons);

            if (_mouseModeEnabled && (pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB) != 0)
            {
                OpenClipboardHistory();
            }

            if (_taskViewVkMappingRequested &&
                (pressedThisFrame & (NativeMethods.XINPUT_GAMEPAD_A | NativeMethods.XINPUT_GAMEPAD_B)) != 0)
            {
                if ((pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_A) != 0)
                {
                    _sysControl.LogDebug(
                        "GamepadMonitorService: A encerrou o contexto aproximado do Task View.");
                    SetTaskViewMappingRequested(false);
                }
                else
                {
                    HandleContextExitButtonPressed();
                }
            }

            if (_virtualKeyboardVkMappingRequested &&
                (pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_B) != 0)
            {
                HandleContextExitButtonPressed();
            }

            long now = Environment.TickCount64;
            LogBackRelatedButtonState(buttons, pressedThisFrame, releasedThisFrame, now);
            TrackBackPressState(buttons, pressedThisFrame, releasedThisFrame, now);

            List<RuntimeShortcut> runtimeShortcuts;
            int doublePressIntervalMs;
            lock (_settingsLock)
            {
                runtimeShortcuts = _runtimeShortcuts;
                doublePressIntervalMs = _doublePressIntervalMs;
            }

            foreach (var runtimeShortcut in runtimeShortcuts)
            {
                var shortcut = runtimeShortcut.Shortcut;
                ushort comboMask = runtimeShortcut.ComboMask;

                // R3 is the clipboard-history button while mouse mode is active;
                // keep the normal screenshot shortcut available everywhere else.
                if (_mouseModeEnabled && comboMask == NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB)
                {
                    continue;
                }

                bool allPressed = (buttons & comboMask) == comboMask;
                bool anyNewPress = (pressedThisFrame & comboMask) != 0;
                bool anyReleased = (releasedThisFrame & comboMask) != 0;

                if (runtimeShortcut.TriggerMode == GamepadTriggerMode.Hold3s)
                {
                    if (allPressed && anyNewPress)
                    {
                        StartHoldShortcutTimer(runtimeShortcut, comboMask);
                    }

                    if (anyReleased)
                    {
                        CancelHoldShortcutTimer(comboMask);
                    }
                }
                else if (runtimeShortcut.TriggerMode == GamepadTriggerMode.DoublePress)
                {
                    if (comboMask == NativeMethods.XINPUT_GAMEPAD_BACK)
                    {
                        // BACK é tratado em uma rotina dedicada para não conflitar com BACK como modificador
                        // (BACK+A, BACK+X, BACK+Y etc.).
                        continue;
                    }

                    if (allPressed && anyNewPress)
                    {
                        _lastButtonPressTimes.TryGetValue(comboMask, out var lastPressTime);
                        long msSinceLast = lastPressTime != 0 ? now - lastPressTime : long.MaxValue;
                        if (msSinceLast <= doublePressIntervalMs)
                        {
                            ExecuteShortcutAction(runtimeShortcut);
                            _lastButtonPressTimes[comboMask] = 0; // reset
                        }
                        else
                        {
                            _lastButtonPressTimes[comboMask] = now;
                        }
                    }
                }
                else // Press
                {
                    if (allPressed && anyNewPress)
                    {
                        // Se contiver BACK e outro botão, conta como modificador usado
                        if ((comboMask & NativeMethods.XINPUT_GAMEPAD_BACK) != 0 && comboMask != NativeMethods.XINPUT_GAMEPAD_BACK)
                        {
                            _backWasUsedAsModifier = true;
                        }
                        ExecuteShortcutAction(runtimeShortcut);
                    }
                }
            }

            HandleBackDoublePressOnRelease(runtimeShortcuts, releasedThisFrame, now, doublePressIntervalMs);

            // Fechamento manual do overlay se B for pressionado individualmente (não em combo)
            bool isBackHeld = (buttons & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;
            if ((pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_B) != 0 && !isBackHeld)
            {
                // O monitor de atalhos roda no helper administrativo, enquanto a janela
                // pertence ao processo GUI. O processo principal decide se há algo a fechar.
                ThreadPool.QueueUserWorkItem(_ =>
                    TrySendCommandToMainApp("hide-gamepad-help"));
            }

            _prevButtons = buttons;
            return true;
        }

        private void TrackBackPressState(ushort buttons, ushort pressedThisFrame, ushort releasedThisFrame, long now)
        {
            bool backDown = (buttons & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;
            bool backPressed = (pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;
            bool backReleased = (releasedThisFrame & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;
            bool otherButtonsDown = (buttons & ~NativeMethods.XINPUT_GAMEPAD_BACK) != 0;

            if (backPressed)
            {
                _backPressInProgress = true;
                _backPressHadOtherButtons = otherButtonsDown;
                _backDoublePressConsumed = false;
                _backWasUsedAsModifier = otherButtonsDown;
                return;
            }

            if (backDown && _backPressInProgress && otherButtonsDown)
            {
                _backPressHadOtherButtons = true;
                _backWasUsedAsModifier = true;
                _lastBackPress = 0;
            }

            if (backReleased && !_backPressInProgress)
            {
                if (_rtssOsdConsumedForBackHold)
                {
                    _rtssOsdRearmAt = now + 500;
                }

                _backPressHadOtherButtons = false;
                _backDoublePressConsumed = false;
                _backWasUsedAsModifier = false;
                _rtssOsdConsumedForBackHold = false;
            }
            else if (backReleased)
            {
                if (_rtssOsdConsumedForBackHold)
                {
                    _rtssOsdRearmAt = now + 500;
                }

                _rtssOsdConsumedForBackHold = false;
            }
        }

        private void HandleBackDoublePressOnRelease(List<RuntimeShortcut> runtimeShortcuts, ushort releasedThisFrame, long now, int doublePressIntervalMs)
        {
            if ((releasedThisFrame & NativeMethods.XINPUT_GAMEPAD_BACK) == 0)
            {
                return;
            }

            bool backWasStandalone = _backPressInProgress && !_backPressHadOtherButtons;

            _backPressInProgress = false;
            _backPressHadOtherButtons = false;
            _backWasUsedAsModifier = false;

            if (!backWasStandalone || _backDoublePressConsumed)
            {
                _lastBackPress = 0;
                _backDoublePressConsumed = false;
                return;
            }

            foreach (var runtimeShortcut in runtimeShortcuts)
            {
                if (runtimeShortcut.TriggerMode != GamepadTriggerMode.DoublePress ||
                    runtimeShortcut.ComboMask != NativeMethods.XINPUT_GAMEPAD_BACK)
                {
                    continue;
                }

                long msSinceLast = _lastBackPress != 0 ? now - _lastBackPress : long.MaxValue;
                if (msSinceLast <= doublePressIntervalMs)
                {
                    ExecuteShortcutAction(runtimeShortcut);
                    _lastBackPress = 0;
                    _backDoublePressConsumed = true;
                }
                else
                {
                    _lastBackPress = now;
                }

                return;
            }
        }

        private bool UpdateGamepadHelpHoldFromInterpretedState(ushort interpretedButtons, long now)
        {
            bool startOrOptionsDown =
                (interpretedButtons & NativeMethods.XINPUT_GAMEPAD_START) != 0;

            if (!startOrOptionsDown)
            {
                _gamepadHelpHoldStartedAt = 0;
                _gamepadHelpHoldTriggered = false;
                return false;
            }

            if (_gamepadHelpHoldStartedAt == 0)
            {
                _gamepadHelpHoldStartedAt = now;
                _gamepadHelpHoldTriggered = false;
                _sysControl.LogDebug(
                    $"GamepadMonitor: START/OPTIONS detectado pelo parser HID. Mask=0x{interpretedButtons:X4}; iniciando contagem de 3s.");
                return false;
            }

            if (_gamepadHelpHoldTriggered || now - _gamepadHelpHoldStartedAt < 3000)
            {
                return false;
            }

            _gamepadHelpHoldTriggered = true;
            return true;
        }

        private static bool IsStartOrOptionsName(string? buttonName)
        {
            return buttonName != null && buttonName.ToUpperInvariant() is
                "START" or "START_MENU" or "MENU" or "OPTIONS" or "OPTION" or "PS_OPTIONS";
        }

        private void StartHoldShortcutTimer(RuntimeShortcut runtimeShortcut, ushort comboMask)
        {
            CancelHoldShortcutTimer(comboMask);

            var cancellation = new CancellationTokenSource();
            _buttonHoldCancellationSources[comboMask] = cancellation;

            _ = Task.Run(async () =>
            {
                bool shouldExecute = false;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token).ConfigureAwait(false);

                    lock (_inputStateLock)
                    {
                        if (!cancellation.IsCancellationRequested &&
                            _buttonHoldCancellationSources.TryGetValue(comboMask, out var activeCancellation) &&
                            ReferenceEquals(activeCancellation, cancellation) &&
                            (_prevButtons & comboMask) == comboMask)
                        {
                            _buttonHoldCancellationSources.Remove(comboMask);
                            shouldExecute = true;
                        }
                    }

                    if (shouldExecute)
                    {
                        _sysControl.LogDebug(
                            $"GamepadMonitor: hold de 3s confirmado para '{runtimeShortcut.Shortcut.Name}' " +
                            $"(mask=0x{comboMask:X4}).");
                        ExecuteShortcutAction(runtimeShortcut);
                    }
                }
                catch (OperationCanceledException)
                {
                    // O botão foi solto antes de completar os três segundos.
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug(
                        $"GamepadMonitor: falha no temporizador hold de '{runtimeShortcut.Shortcut.Name}': {ex}");
                }
                finally
                {
                    cancellation.Dispose();
                }
            });
        }

        private void CancelHoldShortcutTimer(ushort comboMask)
        {
            if (_buttonHoldCancellationSources.Remove(comboMask, out CancellationTokenSource? cancellation))
            {
                cancellation.Cancel();
            }
        }

        private void CancelAllHoldShortcutTimers()
        {
            foreach (CancellationTokenSource cancellation in _buttonHoldCancellationSources.Values)
            {
                cancellation.Cancel();
            }

            _buttonHoldCancellationSources.Clear();
        }

        private void ResetButtonTrackingState()
        {
            if (_mouseModeEnabled)
            {
                ReleaseMouseModeButtons();
                _previousMouseModeButtons = 0;
                _previousLeftTriggerDown = false;
                _previousRightTriggerDown = false;
            }

            _prevButtons = 0;
            _backWasUsedAsModifier = false;
            _backPressInProgress = false;
            _backPressHadOtherButtons = false;
            _backDoublePressConsumed = false;
            _hasObservedNeutralFrame = false;
            _startupButtonsSuppressed = false;
            _lastBackPress = 0;
            _lastLoggedBackMask = 0;
            _lastBackMaskLogAt = 0;
            _rtssOsdRearmAt = 0;
            _lastButtonPressTimes.Clear();
            _gamepadHelpHoldStartedAt = 0;
            _gamepadHelpHoldTriggered = false;
            CancelAllHoldShortcutTimers();
            _rtssOsdConsumedForBackHold = false;
        }

        private void LogBackRelatedButtonState(ushort buttons, ushort pressedThisFrame, ushort releasedThisFrame, long now)
        {
            bool backInvolved =
                (buttons & NativeMethods.XINPUT_GAMEPAD_BACK) != 0 ||
                (_prevButtons & NativeMethods.XINPUT_GAMEPAD_BACK) != 0 ||
                (pressedThisFrame & NativeMethods.XINPUT_GAMEPAD_BACK) != 0 ||
                (releasedThisFrame & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;

            if (!backInvolved)
            {
                return;
            }

            if (buttons == _lastLoggedBackMask && now - _lastBackMaskLogAt < 500)
            {
                return;
            }

            _lastLoggedBackMask = buttons;
            _lastBackMaskLogAt = now;

            _sysControl.LogDebug(
                "GamepadMonitor: estado BACK/combos " +
                $"mask=0x{buttons:X4}({FormatButtons(buttons)}), " +
                $"pressed=0x{pressedThisFrame:X4}({FormatButtons(pressedThisFrame)}), " +
                $"released=0x{releasedThisFrame:X4}({FormatButtons(releasedThisFrame)}).");
        }

        private static string FormatButtons(ushort buttons)
        {
            if (buttons == 0)
            {
                return "none";
            }

            var names = new List<string>();
            Add(NativeMethods.XINPUT_GAMEPAD_DPAD_UP, "DPAD_UP");
            Add(NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN, "DPAD_DOWN");
            Add(NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT, "DPAD_LEFT");
            Add(NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT, "DPAD_RIGHT");
            Add(NativeMethods.XINPUT_GAMEPAD_START, "START");
            Add(NativeMethods.XINPUT_GAMEPAD_BACK, "BACK");
            Add(NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB, "LS");
            Add(NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB, "RS");
            Add(NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER, "LB");
            Add(NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER, "RB");
            Add(NativeMethods.XINPUT_GAMEPAD_A, "A");
            Add(NativeMethods.XINPUT_GAMEPAD_B, "B");
            Add(NativeMethods.XINPUT_GAMEPAD_X, "X");
            Add(NativeMethods.XINPUT_GAMEPAD_Y, "Y");
            return string.Join("+", names);

            void Add(ushort mask, string name)
            {
                if ((buttons & mask) != 0)
                {
                    names.Add(name);
                }
            }
        }

        private ushort GetComboMask(List<string> buttons)
        {
            if (buttons == null) return 0;
            ushort mask = 0;
            foreach (var btn in buttons)
            {
                switch (btn.ToUpperInvariant())
                {
                    case "DPAD_UP":
                    case "DPADUP":
                    case "UP":
                        mask |= 0x0001;
                        break;
                    case "DPAD_DOWN":
                    case "DPADDOWN":
                    case "DOWN":
                        mask |= 0x0002;
                        break;
                    case "DPAD_LEFT":
                    case "DPADLEFT":
                    case "LEFT":
                        mask |= 0x0004;
                        break;
                    case "DPAD_RIGHT":
                    case "DPADRIGHT":
                    case "RIGHT":
                        mask |= 0x0008;
                        break;
                    case "START":
                    case "START_MENU":
                    case "MENU":
                    case "OPTIONS":
                    case "OPTION":
                    case "PS_OPTIONS":
                        mask |= 0x0010;
                        break;
                    case "BACK":
                    case "SELECT":
                        mask |= 0x0020;
                        break;
                    case "L-THUMB":
                    case "LTHUMB":
                    case "LS":
                    case "LEFT_THUMB":
                        mask |= 0x0040;
                        break;
                    case "R-THUMB":
                    case "RTHUMB":
                    case "RS":
                    case "RIGHT_THUMB":
                        mask |= 0x0080;
                        break;
                    case "LB":
                    case "LEFT_SHOULDER":
                        mask |= 0x0100;
                        break;
                    case "RB":
                    case "RIGHT_SHOULDER":
                        mask |= 0x0200;
                        break;
                    case "A":
                        mask |= 0x1000;
                        break;
                    case "B":
                        mask |= 0x2000;
                        break;
                    case "X":
                        mask |= 0x4000;
                        break;
                    case "Y":
                        mask |= 0x8000;
                        break;
                }
            }
            return mask;
        }

        private void ExecuteShortcutAction(RuntimeShortcut runtimeShortcut)
        {
            ThreadPool.QueueUserWorkItem(_ => ExecuteShortcutActionCore(runtimeShortcut));
        }

        private void ExecuteShortcutActionCore(RuntimeShortcut runtimeShortcut)
        {
            var shortcut = runtimeShortcut.Shortcut;
            _sysControl.LogDebug($"GamepadMonitor: Executando atalho '{shortcut.Name}' ({runtimeShortcut.ActionType}:{shortcut.ActionValue})");

            switch (runtimeShortcut.ActionType)
            {
                case GamepadActionType.System:
                    ExecuteSystemAction(shortcut.ActionValue);
                    break;
                case GamepadActionType.RunExe:
                    _sysControl.RunExecutable(shortcut.ActionValue);
                    break;
                case GamepadActionType.SendKeys:
                    if (IsShortcutText(shortcut.ActionValue, "Alt+R"))
                    {
                        ExecuteAltRGamepadAction();
                    }
                    else if (IsShortcutText(shortcut.ActionValue, "Win+G"))
                    {
                        OpenXboxGameBar();
                    }
                    else
                    {
                        _sysControl.SimulateKeyboardShortcut(shortcut.ActionValue);
                    }
                    break;
            }
        }

        private void ExecuteSystemAction(string actionValue)
        {
            switch (actionValue.ToLowerInvariant())
            {
                case "virtualkeyboard":
                    ToggleVirtualKeyboard();
                    break;
                case "togglekeyboardprofile":
                    _sysControl.ToggleKeyboardProfile();
                    break;
                case "poweroffmonitor":
                    _sysControl.PowerOffMonitor();
                    break;
                case "toggledisplaytarget":
                    _sysControl.ToggleDisplayTarget();
                    break;
                case "togglegamepadhelp":
                    if (!TrySendCommandToMainApp("toggle-gamepad-help"))
                    {
                        _sysControl.LogDebug(
                            "GamepadMonitor: não foi possível encaminhar o overlay ao processo principal.");
                    }
                    break;
                case "volumedown":
                    SendKey(0xAE); // VK_VOLUME_DOWN
                    break;
                case "volumeup":
                    SendKey(0xAF); // VK_VOLUME_UP
                    break;
                case "showgamebar":
                    OpenXboxGameBar();
                    break;
                case "screenshot":
                    SendWinKey(0x2C); // VK_SNAPSHOT (Win+PrintScreen)
                    break;
                case "forcekillforegroundapp":
                    _sysControl.ForceKillForegroundApp();
                    break;
                case "togglegamepadmousemode":
                    SetMouseModeEnabled(!_mouseModeEnabled);
                    break;
            }
        }

        private bool TrySendCommandToMainApp(string command)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    "TutzApp.CommandPipe",
                    PipeDirection.InOut,
                    PipeOptions.None);
                client.Connect(900);

                using var writer = new StreamWriter(
                    client,
                    System.Text.Encoding.UTF8,
                    1024,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };
                using var reader = new StreamReader(
                    client,
                    System.Text.Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);

                writer.WriteLine(command);
                string? response = reader.ReadLine();
                return string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug(
                    $"GamepadMonitor: falha ao enviar '{command}' ao processo principal: {ex.Message}");
                return false;
            }
        }

        private void SetMouseModeEnabled(bool enabled)
        {
            if (_mouseModeEnabled == enabled)
            {
                return;
            }

            _mouseModeEnabled = enabled;
            _previousMouseModeButtons = 0;
            _previousLeftTriggerDown = false;
            _previousRightTriggerDown = false;
            _mouseResidualX = 0;
            _mouseResidualY = 0;
            _wheelResidual = 0;
            ResetMouseTimedAcceleration();

            if (enabled)
            {
                _previousLeftTriggerDown = _latestInputState.LeftTrigger >= 0.35;
                _previousRightTriggerDown = _latestInputState.RightTrigger >= 0.35;
                _mouseModeLoopCancellation?.Cancel();
                _mouseModeLoopCancellation?.Dispose();
                _mouseModeLoopCancellation = new CancellationTokenSource();
                _ = Task.Run(() => RunMouseModeLoopAsync(_mouseModeLoopCancellation.Token));
                _sysControl.LogDebug("GamepadMouseMode: ativado no helper admin.");
                _sysControl.SetStatusMessage("Modo mouse do gamepad ativo.");
                return;
            }

            _mouseModeLoopCancellation?.Cancel();
            _mouseModeLoopCancellation?.Dispose();
            _mouseModeLoopCancellation = null;
            ReleaseMouseModeButtons();
            _sysControl.LogDebug("GamepadMouseMode: desativado.");
            _sysControl.SetStatusMessage("Modo mouse do gamepad desativado.");
        }

        private async Task RunMouseModeLoopAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            long lastElapsed = stopwatch.ElapsedTicks;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    long elapsed = stopwatch.ElapsedTicks;
                    double dt = Math.Clamp((elapsed - lastElapsed) / (double)Stopwatch.Frequency, 0.001, 0.012);
                    lastElapsed = elapsed;

                    GamepadInputState state;
                    lock (_inputStateLock)
                    {
                        state = _latestInputState;
                    }

                    ApplyMouseMovement(state, dt);
                    ApplyMouseWheel(state, dt);

                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMouseMode: loop encerrado por erro: {ex}");
            }
        }

        private void ApplyMouseMovement(GamepadInputState state, double dt)
        {
            double maxPixelsPerSecond;
            lock (_settingsLock)
            {
                maxPixelsPerSecond = _mouseModeMaxPixelsPerSecond;
            }

            var (x, y) = ApplyRadialStickCurve(state.LeftX, state.LeftY, deadZone: 0.0, exponent: 1.0);
            (x, y) = ApplyMouseTimedAcceleration(x, y, dt);
            if (x == 0 && y == 0)
            {
                _mouseResidualX = 0;
                _mouseResidualY = 0;
                return;
            }

            // Base linear preservada; a aceleração temporal só entra acima de 95% do analógico.
            if (x != 0 && _mouseResidualX != 0 && Math.Sign(x) != Math.Sign(_mouseResidualX))
            {
                _mouseResidualX = 0;
            }

            if (y != 0 && _mouseResidualY != 0 && Math.Sign(y) != Math.Sign(_mouseResidualY))
            {
                _mouseResidualY = 0;
            }

            double moveX = x * maxPixelsPerSecond * dt + _mouseResidualX;
            double moveY = y * maxPixelsPerSecond * dt + _mouseResidualY;

            int dx = (int)Math.Truncate(moveX);
            int dy = (int)Math.Truncate(moveY);
            _mouseResidualX = moveX - dx;
            _mouseResidualY = moveY - dy;

            if (dx != 0 || dy != 0)
            {
                SendMouseInput(NativeMethods.MOUSEEVENTF_MOVE, dx, dy, 0);
            }
        }

        private (double x, double y) ApplyMouseTimedAcceleration(double x, double y, double dt)
        {
            double magnitude = Math.Sqrt(x * x + y * y);
            if (magnitude < MouseModeTimedAccelerationThreshold || magnitude == 0)
            {
                ResetMouseTimedAcceleration();
                return (x, y);
            }

            double directionX = x / magnitude;
            double directionY = y / magnitude;
            if (_mouseHighZoneElapsed > 0 &&
                directionX * _mouseHighZoneDirectionX + directionY * _mouseHighZoneDirectionY <= 0)
            {
                _mouseHighZoneElapsed = 0;
            }

            _mouseHighZoneDirectionX = directionX;
            _mouseHighZoneDirectionY = directionY;
            _mouseHighZoneElapsed = Math.Min(
                MouseModeTimedAccelerationDurationSeconds,
                _mouseHighZoneElapsed + dt);

            double progress = Math.Clamp(
                _mouseHighZoneElapsed / MouseModeTimedAccelerationDurationSeconds,
                0,
                1);
            double eased = progress * progress * progress;
            double multiplier = 1 +
                (MouseModeTimedAccelerationMaxMultiplier - 1) * eased;
            return (x * multiplier, y * multiplier);
        }

        private void ResetMouseTimedAcceleration()
        {
            _mouseHighZoneElapsed = 0;
            _mouseHighZoneDirectionX = 0;
            _mouseHighZoneDirectionY = 0;
        }

        private void ApplyMouseWheel(GamepadInputState state, double dt)
        {
            var (_, y) = ApplyRadialStickCurve(0, state.RightY, deadZone: 0.25, exponent: 3.0);
            if (y == 0)
            {
                _wheelResidual = 0;
                return;
            }

            double wheel = y * 18.0 * dt * 120.0 + _wheelResidual;
            int wheelDelta = (int)Math.Truncate(wheel);
            _wheelResidual = wheel - wheelDelta;

            if (Math.Abs(wheelDelta) >= 1)
            {
                SendMouseInput(NativeMethods.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)wheelDelta));
            }
        }

        private static (double x, double y) ApplyRadialStickCurve(double x, double y, double deadZone, double exponent)
        {
            double magnitude = Math.Sqrt(x * x + y * y);
            if (magnitude <= deadZone)
            {
                return (0, 0);
            }

            double normalized = Math.Clamp((magnitude - deadZone) / (1 - deadZone), 0, 1);
            double curved = Math.Pow(normalized, exponent);
            double scale = curved / magnitude;
            return (x * scale, y * scale);
        }

        private void ProcessMouseModeButtons(GamepadInputState state)
        {
            ushort buttons = state.Buttons;
            ushort pressed = (ushort)(buttons & ~_previousMouseModeButtons);

            bool ctrlModifierDown = (buttons & NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
            bool shiftModifierDown = (buttons & NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;

            // Press modifiers before a simultaneous mouse-button press so the
            // click is delivered with Ctrl/Shift already held.
            if (ctrlModifierDown && !_ctrlModifierDown)
            {
                SendKeyboardKeyState(true, VirtualKeyLeftControl);
                _ctrlModifierDown = true;
            }

            if (shiftModifierDown && !_shiftModifierDown)
            {
                SendKeyboardKeyState(true, VirtualKeyLeftShift);
                _shiftModifierDown = true;
            }

            UpdateMouseButton(
                (buttons & NativeMethods.XINPUT_GAMEPAD_A) != 0,
                ref _leftMouseDown,
                NativeMethods.MOUSEEVENTF_LEFTDOWN,
                NativeMethods.MOUSEEVENTF_LEFTUP,
                0);

            UpdateMouseButton(
                (buttons & NativeMethods.XINPUT_GAMEPAD_Y) != 0,
                ref _rightMouseDown,
                NativeMethods.MOUSEEVENTF_RIGHTDOWN,
                NativeMethods.MOUSEEVENTF_RIGHTUP,
                0);

            // Release modifiers after the mouse button so the click keeps its
            // modifier for the full down/up pair.
            if (!ctrlModifierDown && _ctrlModifierDown)
            {
                SendKeyboardKeyState(false, VirtualKeyLeftControl);
                _ctrlModifierDown = false;
            }

            if (!shiftModifierDown && _shiftModifierDown)
            {
                SendKeyboardKeyState(false, VirtualKeyLeftShift);
                _shiftModifierDown = false;
            }

            bool leftTriggerDown = state.LeftTrigger >= 0.35;
            bool rightTriggerDown = state.RightTrigger >= 0.35;
            if (leftTriggerDown && !_previousLeftTriggerDown)
            {
                SendMouseClick(NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON1);
            }

            if (rightTriggerDown && !_previousRightTriggerDown)
            {
                SendMouseClick(NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON2);
            }

            if ((pressed & NativeMethods.XINPUT_GAMEPAD_X) != 0)
            {
                _sysControl.LogDebug("GamepadMouseMode: X -> teclado virtual.");
                _sysControl.InvokeTouchKeyboard();
            }

            if ((pressed & NativeMethods.XINPUT_GAMEPAD_B) != 0)
            {
                SendKey(0x1B); // VK_ESCAPE
            }

            if ((pressed & NativeMethods.XINPUT_GAMEPAD_START) != 0)
            {
                SendKey(0x0D); // VK_RETURN
            }

            _previousMouseModeButtons = buttons;
            _previousLeftTriggerDown = leftTriggerDown;
            _previousRightTriggerDown = rightTriggerDown;
        }

        private void UpdateMouseButton(bool down, ref bool previous, uint downFlag, uint upFlag, uint mouseData)
        {
            if (down == previous)
            {
                return;
            }

            previous = down;
            SendMouseInput(down ? downFlag : upFlag, 0, 0, mouseData);
        }

        private void ReleaseMouseModeButtons()
        {
            if (_leftMouseDown)
            {
                SendMouseInput(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0);
                _leftMouseDown = false;
            }

            if (_rightMouseDown)
            {
                SendMouseInput(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0);
                _rightMouseDown = false;
            }

            if (_ctrlModifierDown)
            {
                SendKeyboardKeyState(false, VirtualKeyLeftControl);
                _ctrlModifierDown = false;
            }

            if (_shiftModifierDown)
            {
                SendKeyboardKeyState(false, VirtualKeyLeftShift);
                _shiftModifierDown = false;
            }
        }

        private void SendMouseClick(uint downFlag, uint upFlag, uint mouseData)
        {
            SendMouseInput(downFlag, 0, 0, mouseData);
            SendMouseInput(upFlag, 0, 0, mouseData);
        }

        private void SendMouseInput(uint flags, int dx, int dy, uint mouseData)
        {
            try
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[1];
                inputs[0].type = NativeMethods.INPUT_MOUSE;
                inputs[0].U.mi.dx = dx;
                inputs[0].U.mi.dy = dy;
                inputs[0].U.mi.mouseData = mouseData;
                inputs[0].U.mi.dwFlags = flags;
                inputs[0].U.mi.dwExtraInfo = SimulatedKeySignature;

                uint sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != 1)
                {
                    _sysControl.LogDebug($"GamepadMouseMode: SendInput mouse falhou. flags=0x{flags:X}, sent={sent}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMouseMode: erro ao enviar mouse input: {ex.Message}");
            }
        }

        private static bool IsShortcutText(string? actual, string expected)
        {
            static string Normalize(string? value) => (value ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("-", "+")
                .ToLowerInvariant();

            return Normalize(actual) == Normalize(expected);
        }

        private void ExecuteAltRGamepadAction()
        {
            long now = Environment.TickCount64;
            if (now < _rtssOsdRearmAt)
            {
                _sysControl.LogDebug($"GamepadMonitor: RTSS OSD ignorado; aguardando rearm pós-BACK+A ({_rtssOsdRearmAt - now}ms restantes).");
                return;
            }

            if (_rtssOsdConsumedForBackHold)
            {
                _sysControl.LogDebug("GamepadMonitor: RTSS OSD ignorado; BACK+A já foi consumido neste hold de BACK.");
                return;
            }

            _rtssOsdConsumedForBackHold = true;

            long elapsed = _lastRtssOsdToggleAt == 0 ? long.MaxValue : now - _lastRtssOsdToggleAt;
            if (elapsed < 600)
            {
                _sysControl.LogDebug($"GamepadMonitor: RTSS OSD ignorado por debounce ({elapsed}ms desde o último toggle).");
                return;
            }

            _lastRtssOsdToggleAt = now;

            // Se o jogo/processo ativo em primeiro plano for de 32 bits, o hooking via RTSSHooks64.dll não se aplica.
            // Se o jogo for de 64 bits mas ainda não foi interceptado pelo RTSS (ex: InjectionDelay=15000 / 15s),
            // RTSSHooks64.dll não tem a instância hookada ainda e SetFlags não surtirá efeito na OSD.
            // Em ambos os casos, acionamos via hotkey RTSS com dwell time compatível com o DirectInput do HotkeyHandler.dll.
            bool is32Bit = _rtssOsdController.IsForeground32BitApp(out string? proc32Name);
            bool isHooked = _rtssOsdController.IsForegroundAppHookedByRtss(out uint hookedPid, out string? hookedName);

            if (is32Bit || !isHooked)
            {
                _sysControl.LogDebug($"GamepadMonitor: Processo em primeiro plano não hookado diretamente por RTSSHooks64 (32bit={is32Bit}, Hooked={isHooked}, App='{hookedName ?? proc32Name ?? "desconhecido"}'). Enviando hotkey RTSS com dwell time DirectInput.");
                _ = Task.Run(SendRtssToggleHotkeyWithHoldAsync);
                return;
            }

            if (_rtssOsdController.TryToggle(out bool visible, out string? error))
            {
                _sysControl.LogDebug($"GamepadMonitor: RTSS OSD alternado diretamente via RTSSHooks64. visible={visible}");
                return;
            }

            _sysControl.LogDebug($"GamepadMonitor: RTSS OSD direto falhou ({error}); fallback hotkey RTSS com dwell time DirectInput.");
            _ = Task.Run(SendRtssToggleHotkeyWithHoldAsync);
        }

        private async Task SendRtssToggleHotkeyWithHoldAsync()
        {
            try
            {
                var (modifiers, keyScanCode, keyExtended) = _rtssOsdController.GetConfiguredOsdToggleHotkey();

                var downList = new List<NativeMethods.INPUT>();
                foreach (var (modScan, modExt) in modifiers)
                {
                    downList.Add(CreateScanKeyInput(modScan, up: false, extended: modExt));
                }
                downList.Add(CreateScanKeyInput(keyScanCode, up: false, extended: keyExtended));

                int inputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));
                uint sentDown = NativeMethods.SendInput((uint)downList.Count, downList.ToArray(), inputSize);

                // Dwell time de 50ms para que o loop DirectInput do HotkeyHandler.dll amostre ambos os botões pressionados simultaneamente
                await Task.Delay(50).ConfigureAwait(false);

                var upList = new List<NativeMethods.INPUT>();
                upList.Add(CreateScanKeyInput(keyScanCode, up: true, extended: keyExtended));
                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    upList.Add(CreateScanKeyInput(modifiers[i].scancode, up: true, extended: modifiers[i].extended));
                }

                uint sentUp = NativeMethods.SendInput((uint)upList.Count, upList.ToArray(), inputSize);
                _sysControl.LogDebug($"GamepadMonitor: Hotkey RTSS enviada com dwell time (50ms). sentDown={sentDown}/{downList.Count}, sentUp={sentUp}/{upList.Count}");
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitor: Exceção ao enviar hotkey RTSS: {ex.Message}");
            }
        }

        private static NativeMethods.INPUT CreateScanKeyInput(ushort scanCode, bool up, bool extended)
        {
            NativeMethods.INPUT input = default;
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.U.ki.wScan = scanCode;
            input.U.ki.dwFlags = NativeMethods.KEYEVENTF_SCANCODE
                | (up ? NativeMethods.KEYEVENTF_KEYUP : 0)
                | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            input.U.ki.dwExtraInfo = SimulatedKeySignature;
            return input;
        }

        private void OpenXboxGameBar()
        {
            // Win+G is the toggle understood by Game Bar. Do not also launch the
            // URI after a successful chord: on current Windows builds that second
            // request can toggle the overlay back and immediately clear
            // IsInputRedirected.
            bool sent = SendScanCodeChord("Win+G", (0x5B, true), (0x22, false));
            _sysControl.LogDebug($"GamepadMonitor: Win+G enviado para Xbox Game Bar. SendInputSuccess={sent}");

            if (sent)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-gamebar:",
                    UseShellExecute = true
                });
                _sysControl.LogDebug("GamepadMonitor: fallback ms-gamebar iniciado após falha de Win+G.");
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitor: fallback ms-gamebar falhou: {ex.Message}");
            }
        }

        private void OpenClipboardHistory()
        {
            bool sent = SendScanCodeChord("Win+V", (0x5B, true), (0x2F, false));
            _sysControl.LogDebug($"GamepadMouseMode: R3 -> histórico da área de transferência. SendInputSuccess={sent}");
        }

        private bool SendScanCodeChord(string label, params (ushort scanCode, bool extended)[] keys)
        {
            if (keys.Length == 0)
            {
                return false;
            }

            try
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[keys.Length * 2];
                int idx = 0;

                for (int i = 0; i < keys.Length; i++)
                {
                    FillScanInput(inputs, idx++, keys[i].scanCode, false, keys[i].extended);
                }

                for (int i = keys.Length - 1; i >= 0; i--)
                {
                    FillScanInput(inputs, idx++, keys[i].scanCode, true, keys[i].extended);
                }

                int inputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));
                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, inputSize);
                if (sent != inputs.Length)
                {
                    _sysControl.LogDebug($"GamepadMonitor: SendInput {label} incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMonitor: Exception ao simular {label} por scancode: {ex.Message}");
                return false;
            }
        }

        private static void FillScanInput(NativeMethods.INPUT[] inputs, int index, ushort scanCode, bool up, bool extended)
        {
            inputs[index].type = NativeMethods.INPUT_KEYBOARD;
            inputs[index].U.ki.wScan = scanCode;
            inputs[index].U.ki.dwFlags = NativeMethods.KEYEVENTF_SCANCODE
                | (up ? NativeMethods.KEYEVENTF_KEYUP : 0)
                | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            inputs[index].U.ki.dwExtraInfo = SimulatedKeySignature;
        }

        private void SendWinKey(ushort vkCode)
        {
            try
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[4];

                // Win Down
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = 0x5B; // VK_LWIN
                inputs[0].U.ki.dwFlags = 0;
                inputs[0].U.ki.dwExtraInfo = SimulatedKeySignature;

                // Key Down
                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = vkCode;
                inputs[1].U.ki.dwFlags = 0;
                inputs[1].U.ki.dwExtraInfo = SimulatedKeySignature;

                // Key Up
                inputs[2].type = NativeMethods.INPUT_KEYBOARD;
                inputs[2].U.ki.wVk = vkCode;
                inputs[2].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[2].U.ki.dwExtraInfo = SimulatedKeySignature;

                // Win Up
                inputs[3].type = NativeMethods.INPUT_KEYBOARD;
                inputs[3].U.ki.wVk = 0x5B; // VK_LWIN
                inputs[3].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[3].U.ki.dwExtraInfo = SimulatedKeySignature;

                NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Exception ao simular entrada WinKey: {ex.Message}");
            }
        }

        private void SendKey(ushort vkCode)
        {
            try
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];

                // Key Down
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = vkCode;
                inputs[0].U.ki.dwFlags = 0;
                inputs[0].U.ki.dwExtraInfo = SimulatedKeySignature;

                // Key Up
                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = vkCode;
                inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.dwExtraInfo = SimulatedKeySignature;

                NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Exception ao simular entrada de tecla: {ex.Message}");
            }
        }

        private void SendKeyboardKeyState(bool down, ushort vkCode)
        {
            try
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[1];
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = vkCode;
                inputs[0].U.ki.dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP;
                inputs[0].U.ki.dwExtraInfo = SimulatedKeySignature;

                uint sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != 1)
                {
                    _sysControl.LogDebug($"GamepadMouseMode: SendInput teclado falhou. vk=0x{vkCode:X2}, down={down}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"GamepadMouseMode: erro ao alterar modificador de teclado: {ex.Message}");
            }
        }
    }
}
