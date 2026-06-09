// Services/GamepadMonitorService.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TutzApp.Common;

namespace TutzApp.Services
{
    public class GamepadMonitorService : IGamepadMonitorService
    {
        private static readonly IntPtr SimulatedKeySignature = new IntPtr(0x12345);
        private readonly ISystemControlService _sysControl;

        // Assinatura do XInputGetState
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint XInputGetStateDelegate(uint dwUserIndex, out NativeMethods.XINPUT_STATE pState);

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

        private XInputGetStateDelegate? _xInputGetState;
        private uint _activeControllerIndex = 0;
        private bool _isDllLoaded = false;
        private int _monitorStarted;

        // Estados dos botões (Constantes oficiais do XInput)
        private const ushort XINPUT_GAMEPAD_B = 0x2000;
        private ushort _prevButtons = 0;
        private bool _backWasUsedAsModifier = false;

        // Timestamps para duplo clique e hold genérico (usando Environment.TickCount64)
        private long _lastBackPress = 0;
        private readonly Dictionary<ushort, long> _lastButtonPressTimes = new();
        private readonly Dictionary<ushort, long> _buttonHoldStartTimes = new();
        private readonly Dictionary<ushort, bool> _buttonHoldTriggered = new();
        private readonly object _settingsLock = new();
        private int _doublePressIntervalMs;
        private List<RuntimeShortcut> _runtimeShortcuts = new();

        private sealed class RuntimeShortcut
        {
            public required Models.GamepadShortcut Shortcut { get; init; }
            public ushort ComboMask { get; init; }
            public GamepadTriggerMode TriggerMode { get; init; }
            public GamepadActionType ActionType { get; init; }
        }

        public GamepadMonitorService(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
            RefreshSettings();
            InitializeXInput();
        }

        public void RefreshSettings()
        {
            var shortcuts = new List<RuntimeShortcut>();
            foreach (var shortcut in _sysControl.Config.GamepadShortcuts)
            {
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
                _runtimeShortcuts = shortcuts;
            }
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

        private void InitializeXInput()
        {
            string[] dlls = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
            foreach (var dll in dlls)
            {
                try
                {
                    IntPtr hMod = NativeMethods.LoadLibrary(dll);
                    if (hMod != IntPtr.Zero)
                    {
                        IntPtr procAddr = NativeMethods.GetProcAddress(hMod, "XInputGetState");
                        if (procAddr != IntPtr.Zero)
                        {
                            _xInputGetState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(procAddr);
                            _sysControl.LogDebug($"GamepadMonitor: XInput carregado com sucesso via '{dll}'");
                            _isDllLoaded = true;
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"Erro ao carregar '{dll}': {ex.Message}");
                }
            }

            _sysControl.LogDebug("GamepadMonitor: AVISO - Nenhuma DLL do XInput pôde ser carregada. Controles Xbox não serão detectados.");
        }

        public void StartMonitoring(CancellationToken cancellationToken)
        {
            if (!_isDllLoaded || _xInputGetState == null)
            {
                _sysControl.SetStatusMessage("Gamepad não disponível");
                return;
            }

            if (Interlocked.Exchange(ref _monitorStarted, 1) != 0)
            {
                _sysControl.LogDebug("GamepadMonitorService: tentativa duplicada de iniciar polling ignorada.");
                return;
            }

            _ = Task.Run(() => RunMonitoringAsync(cancellationToken));
        }

        private async Task RunMonitoringAsync(CancellationToken cancellationToken)
        {
            _sysControl.LogDebug("GamepadMonitorService: Iniciando loop de polling...");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int delayMs = Math.Clamp(_sysControl.Config.Monitoring.GamepadPollIntervalMs, 20, 5000);

                    try
                    {
                        bool connected = PollGamepad();
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
                _sysControl.LogDebug("GamepadMonitorService: loop de polling encerrado.");
                Interlocked.Exchange(ref _monitorStarted, 0);
            }
        }

        private bool PollGamepad()
        {
            if (_xInputGetState == null) return false;

            // Busca por controle conectado
            bool controllerFound = false;
            NativeMethods.XINPUT_STATE state = new NativeMethods.XINPUT_STATE();

            // Tenta encontrar o controle ativo
            uint result = _xInputGetState(_activeControllerIndex, out state);
            if (result == 0) // SUCCESS
            {
                controllerFound = true;
            }
            else
            {
                // Se o ativo desconectou, procura em outros slots (0 a 3)
                for (uint i = 0; i < 4; i++)
                {
                    if (i == _activeControllerIndex) continue;
                    result = _xInputGetState(i, out state);
                    if (result == 0)
                    {
                        _activeControllerIndex = i;
                        controllerFound = true;
                        break;
                    }
                }
            }

            if (!controllerFound)
            {
                return false;
            }

            ushort buttons = state.Gamepad.wButtons;

            // Idle short-circuit
            if (buttons == 0 && _prevButtons == 0)
            {
                return true;
            }

            // Gerenciamento dinâmico do modificador BACK/SELECT baseado no estado do hardware
            if ((buttons & NativeMethods.XINPUT_GAMEPAD_BACK) == 0)
            {
                _backWasUsedAsModifier = false;
            }
            else if (buttons != NativeMethods.XINPUT_GAMEPAD_BACK)
            {
                _backWasUsedAsModifier = true;
            }

            // Detectar transições (botões recém pressionados e soltos)
            ushort pressedThisFrame = (ushort)(buttons & ~_prevButtons);
            ushort releasedThisFrame = (ushort)(~buttons & _prevButtons);

            long now = Environment.TickCount64;

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

                bool allPressed = (buttons & comboMask) == comboMask;
                bool anyNewPress = (pressedThisFrame & comboMask) != 0;
                bool anyReleased = (releasedThisFrame & comboMask) != 0;

                if (runtimeShortcut.TriggerMode == GamepadTriggerMode.Hold3s)
                {
                    if (allPressed && anyNewPress)
                    {
                        _buttonHoldStartTimes[comboMask] = now;
                        _buttonHoldTriggered[comboMask] = false;
                    }
                    else if (allPressed)
                    {
                        if (_buttonHoldStartTimes.TryGetValue(comboMask, out var holdStart) &&
                            (!_buttonHoldTriggered.TryGetValue(comboMask, out var triggered) || !triggered))
                        {
                            if (now - holdStart >= 3000)
                            {
                                _buttonHoldTriggered[comboMask] = true;
                                ExecuteShortcutAction(runtimeShortcut);
                            }
                        }
                    }

                    if (anyReleased)
                    {
                        _buttonHoldStartTimes.Remove(comboMask);
                        _buttonHoldTriggered.Remove(comboMask);
                    }
                }
                else if (runtimeShortcut.TriggerMode == GamepadTriggerMode.DoublePress)
                {
                    if (allPressed && anyNewPress)
                    {
                        // Lógica especial de supressão para o BACK
                        if (comboMask == NativeMethods.XINPUT_GAMEPAD_BACK)
                        {
                            long msSinceLast = now - _lastBackPress;
                            if (msSinceLast <= doublePressIntervalMs && !_backWasUsedAsModifier && _lastBackPress != 0)
                            {
                                ExecuteShortcutAction(runtimeShortcut);
                                _lastBackPress = 0; // reset
                            }
                            else
                            {
                                _lastBackPress = now;
                            }
                        }
                        else
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

            // Fechamento manual do overlay se B for pressionado individualmente (não em combo)
            bool isBackHeld = (buttons & NativeMethods.XINPUT_GAMEPAD_BACK) != 0;
            if ((pressedThisFrame & XINPUT_GAMEPAD_B) != 0 && !isBackHeld)
            {
                if (_sysControl.IsGamepadShortcutOverlayVisible)
                {
                    _sysControl.LogDebug("GamepadMonitor: Botão B pressionado com overlay aberto -> Fechando overlay de gamepad");
                    _sysControl.HideGamepadShortcutOverlay();
                }
            }

            _prevButtons = buttons;
            return true;
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
                    _sysControl.SimulateKeyboardShortcut(shortcut.ActionValue);
                    break;
            }
        }

        private void ExecuteSystemAction(string actionValue)
        {
            switch (actionValue.ToLowerInvariant())
            {
                case "virtualkeyboard":
                    _sysControl.InvokeTouchKeyboard();
                    break;
                case "togglekeyboardprofile":
                    _sysControl.ToggleKeyboardProfile();
                    break;
                case "poweroffmonitor":
                    _sysControl.PowerOffMonitor();
                    break;
                case "togglegamepadhelp":
                    if (_sysControl.IsGamepadShortcutOverlayVisible)
                        _sysControl.HideGamepadShortcutOverlay();
                    else
                        _sysControl.ShowGamepadShortcutOverlay();
                    break;
                case "volumedown":
                    SendKey(0xAE); // VK_VOLUME_DOWN
                    break;
                case "volumeup":
                    SendKey(0xAF); // VK_VOLUME_UP
                    break;
                case "showgamebar":
                    SendWinKey(0x47); // 'G' (Win+G)
                    break;
                case "screenshot":
                    SendWinKey(0x2C); // VK_SNAPSHOT (Win+PrintScreen)
                    break;
            }
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
    }
}
