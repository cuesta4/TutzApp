using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    internal readonly struct GamepadInputState
    {
        private const double AxisDeadZone = 0.03;

        public GamepadInputState(ushort buttons, double leftX, double leftY, double rightX, double rightY, double leftTrigger, double rightTrigger)
        {
            Buttons = buttons;
            (LeftX, LeftY) = ApplyStickDeadZone(leftX, leftY);
            (RightX, RightY) = ApplyStickDeadZone(rightX, rightY);
            LeftTrigger = ApplyTriggerDeadZone(leftTrigger);
            RightTrigger = ApplyTriggerDeadZone(rightTrigger);
        }

        public ushort Buttons { get; }
        public double LeftX { get; }
        public double LeftY { get; }
        public double RightX { get; }
        public double RightY { get; }
        public double LeftTrigger { get; }
        public double RightTrigger { get; }

        public static GamepadInputState Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

        private static (double x, double y) ApplyStickDeadZone(double x, double y)
        {
            x = Math.Clamp(x, -1, 1);
            y = Math.Clamp(y, -1, 1);

            double magnitude = Math.Sqrt(x * x + y * y);
            if (magnitude <= AxisDeadZone)
            {
                return (0, 0);
            }

            if (magnitude > 1)
            {
                x /= magnitude;
                y /= magnitude;
                magnitude = 1;
            }

            double adjustedMagnitude = (magnitude - AxisDeadZone) / (1 - AxisDeadZone);
            double scale = adjustedMagnitude / magnitude;
            return (x * scale, y * scale);
        }

        private static double ApplyTriggerDeadZone(double value)
        {
            value = Math.Clamp(value, 0, 1);
            if (value <= AxisDeadZone)
            {
                return 0;
            }

            return (value - AxisDeadZone) / (1 - AxisDeadZone);
        }
    }


    internal sealed class RawGamepadReportSnapshot
    {
        public RawGamepadReportSnapshot(IntPtr hDevice, string devicePath, string displayName, string vidHex, string pidHex, ushort usagePage, ushort usage, byte[] report)
        {
            HDevice = hDevice;
            DevicePath = devicePath;
            DisplayName = displayName;
            VidHex = vidHex;
            PidHex = pidHex;
            UsagePage = usagePage;
            Usage = usage;
            Report = report;
        }

        public IntPtr HDevice { get; }
        public string DevicePath { get; }
        public string DisplayName { get; }
        public string VidHex { get; }
        public string PidHex { get; }
        public ushort UsagePage { get; }
        public ushort Usage { get; }
        public byte[] Report { get; }
        public int ReportLength => Report.Length;
    }

    internal sealed class RawInputGamepadStateProvider : IDisposable
    {
        private readonly ISystemControlService _sysControl;
        private readonly object _stateLock = new();
        private readonly Dictionary<IntPtr, GamepadInputState> _deviceStates = new();

        private Thread? _thread;
        private RawInputWindow? _window;
        private CancellationTokenRegistration _cancellationRegistration;
        private volatile bool _started;
        private GamepadInputState _state = GamepadInputState.Empty;
        private GamepadInputState _lastPublishedState = GamepadInputState.Empty;
        private bool _connected;
        private CalibrationSession? _calibration;
        private long _lastHidReportAt;

        public long LastHidReportAt => Interlocked.Read(ref _lastHidReportAt);

        internal void MarkHidReportReceived()
        {
            Interlocked.Exchange(ref _lastHidReportAt, Environment.TickCount64);
        }

        public event Action<ushort>? ButtonsChanged;
        public event Action<GamepadInputState>? StateChanged;
        public event Action<GamepadSemanticState>? SemanticStateChanged;
        public event Action? RecognizedGamepadsChanged;
        public event Action<RawGamepadReportSnapshot>? RawReportReceived;

        public RawInputGamepadStateProvider(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
        }

        public void Start(CancellationToken cancellationToken)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                try
                {
                    using var window = new RawInputWindow(this, _sysControl);
                    _window = window;
                    window.Register();
                    ready.Set();
                    Application.Run(window);
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"RawInputGamepad: erro fatal na thread Raw Input: {ex}");
                    ready.Set();
                }
                finally
                {
                    _window = null;
                    _started = false;
                }
            });

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "TutzApp RawInput Gamepad";
            _thread.Start();

            ready.Wait(1000);
            _cancellationRegistration = cancellationToken.Register(Stop);
        }

        public bool TryReadButtons(out ushort buttons)
        {
            lock (_stateLock)
            {
                buttons = _state.Buttons;
                return _connected;
            }
        }

        public bool TryReadState(out GamepadInputState state)
        {
            lock (_stateLock)
            {
                state = _state;
                return _connected;
            }
        }

        public bool TryReadSemanticState(out GamepadSemanticState state)
        {
            lock (_stateLock)
            {
                if (!_connected)
                {
                    state = GamepadSemanticState.Empty;
                    return false;
                }

                bool isGuide = (_state.Buttons & NativeMethods.XINPUT_GAMEPAD_GUIDE) != 0;
                bool isShare = (_state.Buttons & NativeMethods.GAMEPAD_BUTTON_SHARE) != 0;
                state = new GamepadSemanticState(
                    _state.Buttons,
                    _state.LeftX,
                    _state.LeftY,
                    _state.RightX,
                    _state.RightY,
                    _state.LeftTrigger,
                    _state.RightTrigger,
                    isGuide,
                    isShare,
                    Environment.TickCount64);
                return true;
            }
        }

        public bool StartCalibration(TimeSpan duration)
        {
            if (!_started)
            {
                _sysControl.SetStatusMessage("Listener HID do gamepad indisponível.");
                return false;
            }

            var session = new CalibrationSession(Environment.TickCount64 + (long)duration.TotalMilliseconds);
            lock (_stateLock)
            {
                _calibration = session;
            }

            _sysControl.LogDebug("Gamepad HID: calibração iniciada. Pressione BACK, A, X e demais botões do controle.");
            _sysControl.SetStatusMessage("Calibração HID do gamepad iniciada.");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(duration).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_calibration, session))
                        {
                            _calibration = null;
                            _sysControl.LogDebug("Gamepad HID: calibração encerrada por tempo.");
                            _sysControl.SetStatusMessage("Calibração HID encerrada.");
                        }
                    }
                }
                catch
                {
                }
            });

            return true;
        }

        internal void SaveGuidedMapping(RawGamepadReportSnapshot snapshot, RecognizedGamepad mapping)
        {
            _sysControl.Config.Gamepad ??= new GamepadSettings();
            _sysControl.Config.Gamepad.RecognizedDevices ??= new List<RecognizedGamepad>();
            var devices = _sysControl.Config.Gamepad.RecognizedDevices;
            var existing = devices.FirstOrDefault(device =>
                device.VidHex.Equals(snapshot.VidHex, StringComparison.OrdinalIgnoreCase) &&
                device.PidHex.Equals(snapshot.PidHex, StringComparison.OrdinalIgnoreCase) &&
                device.InputReportByteLength == snapshot.ReportLength &&
                device.UsagePage == snapshot.UsagePage &&
                device.Usage == snapshot.Usage);

            if (existing == null)
            {
                devices.Add(mapping);
            }
            else
            {
                int index = devices.IndexOf(existing);
                devices[index] = mapping;
            }

            if (_sysControl.SaveAppConfig(_sysControl.Config))
            {
                _sysControl.LogDebug($"Gamepad HID: mapeamento guiado salvo para VID_{snapshot.VidHex} PID_{snapshot.PidHex}, report={snapshot.ReportLength}, layout={mapping.Layout}.");
                _sysControl.SetStatusMessage("Mapeamento HID do gamepad salvo.");
                RecognizedGamepadsChanged?.Invoke();
            }
        }

        private void PublishRawReport(IntPtr hDevice, DeviceContext context, byte[] report)
        {
            var handler = RawReportReceived;
            if (handler == null)
            {
                return;
            }

            var copy = (byte[])report.Clone();
            try
            {
                handler(new RawGamepadReportSnapshot(
                    hDevice,
                    context.Name,
                    context.DisplayName,
                    context.VidHex,
                    context.PidHex,
                    context.UsagePage,
                    context.Usage,
                    copy));
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RawInputGamepad: erro em handler de relatório bruto: {ex.Message}");
            }
        }

        private void UpdateDeviceState(IntPtr device, GamepadInputState state)
        {
            GamepadInputState aggregate;
            bool stateChanged;
            bool buttonsChanged;
            int deviceCount;
            Action<ushort>? buttonHandler = null;
            Action<GamepadInputState>? stateHandler = null;

            lock (_stateLock)
            {
                _deviceStates[device] = state;

                ushort aggregateButtons = 0;
                GamepadInputState axisSource = GamepadInputState.Empty;
                foreach (GamepadInputState deviceState in _deviceStates.Values)
                {
                    aggregateButtons |= deviceState.Buttons;
                    if (HasAnalogActivity(deviceState))
                    {
                        axisSource = deviceState;
                    }
                }

                aggregate = new GamepadInputState(
                    aggregateButtons,
                    axisSource.LeftX,
                    axisSource.LeftY,
                    axisSource.RightX,
                    axisSource.RightY,
                    axisSource.LeftTrigger,
                    axisSource.RightTrigger);

                buttonsChanged = aggregate.Buttons != _state.Buttons;
                stateChanged = buttonsChanged || HasAnalogDelta(aggregate, _lastPublishedState);
                _state = aggregate;
                _connected = _deviceStates.Count > 0;
                deviceCount = _deviceStates.Count;
                if (buttonsChanged)
                {
                    buttonHandler = ButtonsChanged;
                }

                if (stateChanged)
                {
                    _lastPublishedState = aggregate;
                    stateHandler = StateChanged;
                }
            }

            if (buttonHandler != null)
            {
                _sysControl.LogDebug(
                    "RawInputGamepad: buttons changed " +
                    $"aggregate=0x{aggregate.Buttons:X4}({FormatButtons(aggregate.Buttons)}), " +
                    $"device=0x{state.Buttons:X4}({FormatButtons(state.Buttons)}), devices={deviceCount}.");

                try
                {
                    buttonHandler(aggregate.Buttons);
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"RawInputGamepad: erro em handler de botões HID: {ex.Message}");
                }
            }

            if (stateHandler != null)
            {
                try
                {
                    stateHandler(aggregate);
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"RawInputGamepad: erro em handler de estado HID: {ex.Message}");
                }

                try
                {
                    bool isGuide = (aggregate.Buttons & NativeMethods.XINPUT_GAMEPAD_GUIDE) != 0;
                    bool isShare = (aggregate.Buttons & NativeMethods.GAMEPAD_BUTTON_SHARE) != 0;
                    var semantic = new GamepadSemanticState(
                        aggregate.Buttons,
                        aggregate.LeftX,
                        aggregate.LeftY,
                        aggregate.RightX,
                        aggregate.RightY,
                        aggregate.LeftTrigger,
                        aggregate.RightTrigger,
                        isGuide,
                        isShare,
                        Environment.TickCount64);
                    SemanticStateChanged?.Invoke(semantic);
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"RawInputGamepad: erro em handler de estado semântico HID: {ex.Message}");
                }
            }
        }

        internal void RemoveDevice(IntPtr device)
        {
            GamepadInputState aggregate;
            bool stateChanged;
            bool buttonsChanged;
            Action<ushort>? buttonHandler = null;
            Action<GamepadInputState>? stateHandler = null;

            lock (_stateLock)
            {
                if (!_deviceStates.Remove(device))
                {
                    return;
                }

                ushort aggregateButtons = 0;
                GamepadInputState axisSource = GamepadInputState.Empty;
                foreach (GamepadInputState deviceState in _deviceStates.Values)
                {
                    aggregateButtons |= deviceState.Buttons;
                    if (HasAnalogActivity(deviceState))
                    {
                        axisSource = deviceState;
                    }
                }

                aggregate = _deviceStates.Count > 0
                    ? new GamepadInputState(
                        aggregateButtons,
                        axisSource.LeftX,
                        axisSource.LeftY,
                        axisSource.RightX,
                        axisSource.RightY,
                        axisSource.LeftTrigger,
                        axisSource.RightTrigger)
                    : GamepadInputState.Empty;

                buttonsChanged = aggregate.Buttons != _state.Buttons;
                stateChanged = buttonsChanged || HasAnalogDelta(aggregate, _lastPublishedState);
                _state = aggregate;
                _connected = _deviceStates.Count > 0;

                if (buttonsChanged)
                {
                    buttonHandler = ButtonsChanged;
                }

                if (stateChanged)
                {
                    _lastPublishedState = aggregate;
                    stateHandler = StateChanged;
                }
            }

            _sysControl.LogDebug($"RawInputGamepad: dispositivo 0x{device.ToInt64():X} removido do estado do provedor. Restantes={_deviceStates.Count}.");

            try
            {
                buttonHandler?.Invoke(aggregate.Buttons);
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RawInputGamepad: erro ao propagar desconexão de botões: {ex.Message}");
            }

            try
            {
                stateHandler?.Invoke(aggregate);
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RawInputGamepad: erro ao propagar desconexão de estado: {ex.Message}");
            }

            try
            {
                bool isGuide = (aggregate.Buttons & NativeMethods.XINPUT_GAMEPAD_GUIDE) != 0;
                bool isShare = (aggregate.Buttons & NativeMethods.GAMEPAD_BUTTON_SHARE) != 0;
                var semantic = new GamepadSemanticState(
                    aggregate.Buttons,
                    aggregate.LeftX,
                    aggregate.LeftY,
                    aggregate.RightX,
                    aggregate.RightY,
                    aggregate.LeftTrigger,
                    aggregate.RightTrigger,
                    isGuide,
                    isShare,
                    Environment.TickCount64);
                SemanticStateChanged?.Invoke(semantic);
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RawInputGamepad: erro ao propagar desconexão semântica: {ex.Message}");
            }
        }

        private static bool HasAnalogActivity(GamepadInputState state)
        {
            return Math.Abs(state.LeftX) > 0.01 ||
                Math.Abs(state.LeftY) > 0.01 ||
                Math.Abs(state.RightX) > 0.01 ||
                Math.Abs(state.RightY) > 0.01 ||
                state.LeftTrigger > 0.01 ||
                state.RightTrigger > 0.01;
        }

        private static bool HasAnalogDelta(GamepadInputState a, GamepadInputState b)
        {
            const double threshold = 0.01;
            return Math.Abs(a.LeftX - b.LeftX) > threshold ||
                Math.Abs(a.LeftY - b.LeftY) > threshold ||
                Math.Abs(a.RightX - b.RightX) > threshold ||
                Math.Abs(a.RightY - b.RightY) > threshold ||
                Math.Abs(a.LeftTrigger - b.LeftTrigger) > threshold ||
                Math.Abs(a.RightTrigger - b.RightTrigger) > threshold;
        }

        private CalibrationSession? GetActiveCalibration()
        {
            lock (_stateLock)
            {
                if (_calibration == null)
                {
                    return null;
                }

                if (Environment.TickCount64 > _calibration.ExpiresAt)
                {
                    _calibration = null;
                    return null;
                }

                return _calibration;
            }
        }

        private RecognizedGamepad? FindRecognizedLayout(DeviceContext context, int reportLength)
        {
            var devices = _sysControl.Config.Gamepad?.RecognizedDevices;
            if (devices == null || devices.Count == 0)
            {
                return null;
            }

            return devices.FirstOrDefault(device =>
                (string.Equals(device.Layout, "RawHidMappedV2", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(device.Layout, "XboxButtonByteV1", StringComparison.OrdinalIgnoreCase)) &&
                device.VidHex.Equals(context.VidHex, StringComparison.OrdinalIgnoreCase) &&
                device.PidHex.Equals(context.PidHex, StringComparison.OrdinalIgnoreCase) &&
                device.InputReportByteLength == reportLength &&
                (device.UsagePage == 0 || device.UsagePage == context.UsagePage) &&
                (device.Usage == 0 || device.Usage == context.Usage));
        }

        private void SaveRecognizedLayout(DeviceContext context, int reportLength, int buttonByteOffset)
        {
            _sysControl.Config.Gamepad ??= new GamepadSettings();
            _sysControl.Config.Gamepad.RecognizedDevices ??= new List<RecognizedGamepad>();

            var devices = _sysControl.Config.Gamepad.RecognizedDevices;
            var existing = devices.FirstOrDefault(device =>
                device.VidHex.Equals(context.VidHex, StringComparison.OrdinalIgnoreCase) &&
                device.PidHex.Equals(context.PidHex, StringComparison.OrdinalIgnoreCase) &&
                device.InputReportByteLength == reportLength &&
                device.UsagePage == context.UsagePage &&
                device.Usage == context.Usage);

            if (existing == null)
            {
                existing = new RecognizedGamepad();
                devices.Add(existing);
            }

            existing.Name = context.DisplayName;
            existing.DevicePath = context.Name;
            existing.VidHex = context.VidHex;
            existing.PidHex = context.PidHex;
            existing.UsagePage = context.UsagePage;
            existing.Usage = context.Usage;
            existing.InputReportByteLength = reportLength;
            existing.ButtonByteOffset = buttonByteOffset;
            existing.Layout = "XboxButtonByteV1";
            existing.LastSeenUtc = DateTime.UtcNow.ToString("O");

            if (_sysControl.SaveAppConfig(_sysControl.Config))
            {
                _sysControl.LogDebug($"Gamepad HID: layout reconhecido salvo para VID_{context.VidHex} PID_{context.PidHex}, report={reportLength}, offset={buttonByteOffset}.");
                _sysControl.SetStatusMessage("Gamepad HID calibrado.");
                RecognizedGamepadsChanged?.Invoke();
            }
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
            Add(NativeMethods.XINPUT_GAMEPAD_GUIDE, "GUIDE");
            Add(NativeMethods.GAMEPAD_BUTTON_SHARE, "SHARE");
            return string.Join("+", names);

            void Add(ushort mask, string name)
            {
                if ((buttons & mask) != 0)
                {
                    names.Add(name);
                }
            }
        }

        private static ushort MapXboxCompatibleButtonByte(byte buttons)
        {
            ushort mask = 0;

            if ((buttons & 0x01) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_A;
            if ((buttons & 0x02) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_B;
            if ((buttons & 0x04) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_X;
            if ((buttons & 0x08) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_Y;
            if ((buttons & 0x10) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER;
            if ((buttons & 0x20) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER;
            if ((buttons & 0x40) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_BACK;
            if ((buttons & 0x80) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_START;

            return mask;
        }

        private void Stop()
        {
            try
            {
                var window = _window;
                if (window != null && window.IsHandleCreated)
                {
                    window.BeginInvoke((Action)window.Close);
                }
            }
            catch
            {
                try { _window?.Close(); } catch { }
            }

            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(1000);
            }
        }

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
            Stop();
        }

        private sealed class RawInputWindow : Form
        {
            private static readonly IntPtr InvalidHandleValue = new(-1);

            private readonly RawInputGamepadStateProvider _owner;
            private readonly ISystemControlService _sysControl;
            private readonly Dictionary<IntPtr, DeviceContext> _devices = new();
            private long _lastRegistrationErrorLog;
            private long _lastParserErrorLog;
            private long _lastDecodeMismatchLog;

            public RawInputWindow(RawInputGamepadStateProvider owner, ISystemControlService sysControl)
            {
                _owner = owner;
                _sysControl = sysControl;
                ShowInTaskbar = false;
                Opacity = 0;
                Width = 1;
                Height = 1;
            }

            public void Register()
            {
                CreateHandle();

                uint preferredFlags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY;

                if (TryRegister(preferredFlags, "INPUTSINK|DEVNOTIFY"))
                {
                    return;
                }

                uint fallbackFlags = NativeMethods.RIDEV_INPUTSINK;
                TryRegister(fallbackFlags, "INPUTSINK");
            }

            private bool TryRegister(uint flags, string label)
            {
                var devices = new[]
                {
                    new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = flags, hwndTarget = Handle },
                    new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = flags, hwndTarget = Handle },
                    new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x08, dwFlags = flags, hwndTarget = Handle }
                };

                bool registered = NativeMethods.RegisterRawInputDevices(
                    devices,
                    (uint)devices.Length,
                    (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

                if (registered)
                {
                    _sysControl.LogDebug($"RawInputGamepad: registrado para joystick/gamepad/multi-axis. hwnd=0x{Handle.ToInt64():X}, flags={label} (0x{flags:X}).");
                    return true;
                }

                LogRegistrationError($"RegisterRawInputDevices falhou para flags={label} (0x{flags:X}). hwnd=0x{Handle.ToInt64():X}, Win32Error={Marshal.GetLastWin32Error()}");
                return false;
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_INPUT)
                {
                    HandleRawInput(m.LParam);
                }
                else if (m.Msg == NativeMethods.WM_INPUT_DEVICE_CHANGE)
                {
                    HandleDeviceChange(m.WParam, m.LParam);
                }

                base.WndProc(ref m);
            }

            private void HandleDeviceChange(IntPtr wParam, IntPtr lParam)
            {
                try
                {
                    int changeType = (int)wParam.ToInt64();
                    IntPtr hDevice = lParam;

                    if (changeType == NativeMethods.GIDC_REMOVAL)
                    {
                        if (_devices.Remove(hDevice, out var context))
                        {
                            _sysControl.LogDebug($"RawInputGamepad: dispositivo removido {context.DisplayName} (handle=0x{hDevice.ToInt64():X}).");
                            context.Dispose();
                        }
                        _owner.RemoveDevice(hDevice);
                    }
                    else if (changeType == NativeMethods.GIDC_ARRIVAL)
                    {
                        _sysControl.LogDebug($"RawInputGamepad: novo dispositivo conectado (handle=0x{hDevice.ToInt64():X}).");
                        GetDeviceContext(hDevice);
                    }
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"RawInputGamepad: erro em HandleDeviceChange: {ex.Message}");
                }
            }

            protected override void Dispose(bool disposing)
            {
                foreach (var device in _devices.Values)
                {
                    device.Dispose();
                }

                _devices.Clear();
                base.Dispose(disposing);
            }

            private void HandleRawInput(IntPtr hRawInput)
            {
                uint size = 0;
                uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
                uint result = NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize);
                if (result == uint.MaxValue || size == 0)
                {
                    LogParserError($"GetRawInputData(size) falhou. Win32Error={Marshal.GetLastWin32Error()}");
                    return;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    result = NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, buffer, ref size, headerSize);
                    if (result == uint.MaxValue)
                    {
                        LogParserError($"GetRawInputData(payload) falhou. Win32Error={Marshal.GetLastWin32Error()}");
                        return;
                    }

                    var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
                    if (header.dwType != NativeMethods.RIM_TYPEHID)
                    {
                        return;
                    }

                    var context = GetDeviceContext(header.hDevice);
                    if (context == null)
                    {
                        return;
                    }

                    int hidOffset = Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
                    uint sizeHid = (uint)Marshal.ReadInt32(buffer, hidOffset);
                    uint count = (uint)Marshal.ReadInt32(buffer, hidOffset + 4);
                    int payloadOffset = hidOffset + 8;

                    for (int i = 0; i < count; i++)
                    {
                        var report = new byte[sizeHid];
                        Marshal.Copy(IntPtr.Add(buffer, payloadOffset + i * (int)sizeHid), report, 0, report.Length);
                        _owner.PublishRawReport(header.hDevice, context, report);
                        var state = ParseHidGamepadState(context, report);
                        // Preserve press/release edges within a single WM_INPUT batch.
                        _owner.MarkHidReportReceived();
                        _owner.UpdateDeviceState(header.hDevice, state);
                    }
                }
                catch (Exception ex)
                {
                    LogParserError($"exceção ao processar RAW HID: {ex.Message}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            private GamepadInputState ParseHidGamepadState(DeviceContext context, byte[] report)
            {
                var recognized = _owner.FindRecognizedLayout(context, report.Length);
                if (recognized != null)
                {
                    return ParseMappedGamepadState(context, recognized, report);
                }

                var calibration = _owner.GetActiveCalibration();
                if (calibration != null)
                {
                    calibration.Decode(
                        context,
                        report,
                        message => _sysControl.LogDebug($"Gamepad HID calibração: {message}"),
                        _owner.SaveRecognizedLayout);
                    return GamepadInputState.Empty;
                }

                LogParserError($"dispositivo HID gamepad sem layout reconhecido ignorado: VID_{context.VidHex} PID_{context.PidHex} report={report.Length}. Use Calibrar.");
                return GamepadInputState.Empty;
            }

            private GamepadInputState ParseMappedGamepadState(DeviceContext context, RecognizedGamepad mapping, byte[] report)
            {
                if (!IsMappingCompatible(mapping, report.Length))
                {
                    LogParserError($"mapeamento HID incompatível para {context.DisplayName}: report recebido={report.Length}, esperado={mapping.InputReportByteLength}. Recalibre o gamepad.");
                    return GamepadInputState.Empty;
                }

                ushort buttons = ParseMappedButtons(mapping, report);
                double lx = ReadMappedAxis(report, mapping.LeftX);
                double ly = ReadMappedAxis(report, mapping.LeftY);
                double rx = ReadMappedAxis(report, mapping.RightX);
                double ry = ReadMappedAxis(report, mapping.RightY);
                var (lt, rt) = ReadMappedTriggers(report, mapping.Triggers);

                // Compatibilidade com layouts antigos que só tinham ButtonByteOffset.
                if ((mapping.Buttons == null || mapping.Buttons.Count == 0) && mapping.ButtonByteOffset >= 0 && mapping.ButtonByteOffset < report.Length)
                {
                    buttons |= MapXboxCompatibleButtonByte(report[mapping.ButtonByteOffset]);
                    buttons |= ParseSemanticHidButtons(context, report);
                }

                return new GamepadInputState(buttons, lx, ly, rx, ry, lt, rt);
            }

            private static bool IsMappingCompatible(RecognizedGamepad mapping, int reportLength)
            {
                if (mapping.InputReportByteLength > 0 && mapping.InputReportByteLength != reportLength)
                {
                    return false;
                }

                return IsAxisValid(mapping.LeftX, reportLength) &&
                    IsAxisValid(mapping.LeftY, reportLength) &&
                    IsAxisValid(mapping.RightX, reportLength, allowMissing: true) &&
                    IsAxisValid(mapping.RightY, reportLength, allowMissing: true) &&
                    (mapping.Triggers.Offset < 0 || mapping.Triggers.Offset + 1 < reportLength);
            }

            private static bool IsAxisValid(GamepadAxisMapping axis, int reportLength, bool allowMissing = false)
            {
                if (axis.Offset < 0)
                {
                    return allowMissing;
                }

                return axis.Offset + 1 < reportLength;
            }

            private static double ReadMappedAxis(byte[] report, GamepadAxisMapping axis)
            {
                if (axis.Offset < 0 || axis.Offset + 1 >= report.Length)
                {
                    return 0;
                }

                double raw = string.Equals(axis.Format, "Int16LE", StringComparison.OrdinalIgnoreCase)
                    ? unchecked((short)(report[axis.Offset] | (report[axis.Offset + 1] << 8)))
                    : (ushort)(report[axis.Offset] | (report[axis.Offset + 1] << 8));
                double range = Math.Abs(axis.Range) < 0.001 ? 32767.5 : axis.Range;
                double value = (raw - axis.Center) / range;
                if (axis.Invert)
                {
                    value = -value;
                }

                return Math.Clamp(value, -1, 1);
            }

            private static (double left, double right) ReadMappedTriggers(byte[] report, GamepadTriggerMapping triggers)
            {
                if (triggers.Offset < 0 || triggers.Offset + 1 >= report.Length)
                {
                    return (0, 0);
                }

                double raw = string.Equals(triggers.Format, "Int16LE", StringComparison.OrdinalIgnoreCase)
                    ? unchecked((short)(report[triggers.Offset] | (report[triggers.Offset + 1] << 8)))
                    : (ushort)(report[triggers.Offset] | (report[triggers.Offset + 1] << 8));
                double range = Math.Abs(triggers.Range) < 0.001 ? 32768.0 : triggers.Range;
                double axis = Math.Clamp((raw - triggers.Center) / range, -1, 1);
                if (!triggers.LeftPositive)
                {
                    axis = -axis;
                }

                return (Math.Max(0, axis), Math.Max(0, -axis));
            }

            private static ushort ParseMappedButtons(RecognizedGamepad mapping, byte[] report)
            {
                ushort mask = 0;
                foreach (var entry in mapping.Buttons ?? new Dictionary<string, GamepadButtonMapping>())
                {
                    if (!TryGetButtonMask(entry.Key, out ushort semanticMask))
                    {
                        continue;
                    }

                    var button = entry.Value;
                    if (button.ByteOffset >= 0 && button.ByteOffset < report.Length)
                    {
                        bool bitSet = (report[button.ByteOffset] & button.Mask) != 0;
                        if (bitSet != button.ActiveLow)
                        {
                            mask |= semanticMask;
                        }
                    }
                }

                if (mapping.Dpad != null && mapping.Dpad.ByteOffset >= 0 && mapping.Dpad.ByteOffset < report.Length)
                {
                    int dpad = (report[mapping.Dpad.ByteOffset] >> mapping.Dpad.Shift) & mapping.Dpad.Mask;
                    if (dpad == mapping.Dpad.Up) mask |= NativeMethods.XINPUT_GAMEPAD_DPAD_UP;
                    if (dpad == mapping.Dpad.Right) mask |= NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;
                    if (dpad == mapping.Dpad.Down) mask |= NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN;
                    if (dpad == mapping.Dpad.Left) mask |= NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT;
                }

                return mask;
            }

            private static bool TryGetButtonMask(string name, out ushort mask)
            {
                mask = name.ToUpperInvariant() switch
                {
                    "A" => NativeMethods.XINPUT_GAMEPAD_A,
                    "B" => NativeMethods.XINPUT_GAMEPAD_B,
                    "X" => NativeMethods.XINPUT_GAMEPAD_X,
                    "Y" => NativeMethods.XINPUT_GAMEPAD_Y,
                    "LB" => NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER,
                    "RB" => NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER,
                    "BACK" or "BACK_VIEW" or "VIEW" => NativeMethods.XINPUT_GAMEPAD_BACK,
                    "START" or "START_MENU" or "MENU" or "OPTIONS" or "OPTION" or "PS_OPTIONS" => NativeMethods.XINPUT_GAMEPAD_START,
                    "LS" or "L_STICK_CLICK" or "LEFT_THUMB" => NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB,
                    "RS" or "R_STICK_CLICK" or "RIGHT_THUMB" => NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB,
                    "GUIDE" or "XBOX" or "HOME" or "NEXUS" or "PS" => NativeMethods.XINPUT_GAMEPAD_GUIDE,
                    "SHARE" or "CAPTURE" or "MIC" or "TOUCHPAD" => NativeMethods.GAMEPAD_BUTTON_SHARE,
                    _ => 0
                };
                return mask != 0;
            }

            private ushort ParseSemanticHidButtons(DeviceContext context, byte[] report)
            {
                ushort mask = 0;

                if (context.PreparsedData != IntPtr.Zero)
                {
                    var usages = new ushort[64];
                    uint usageLength = (uint)usages.Length;
                    int status = NativeMethods.HidP_GetUsages(
                        NativeMethods.HIDP_INPUT_REPORT,
                        0x09,
                        0,
                        usages,
                        ref usageLength,
                        context.PreparsedData,
                        report,
                        (uint)report.Length);

                    if (status == NativeMethods.HIDP_STATUS_SUCCESS)
                    {
                        for (int i = 0; i < usageLength; i++)
                        {
                            mask |= MapButtonUsage(usages[i]);
                        }
                    }
                    else if (status != unchecked((int)0xC0110005)) // HIDP_STATUS_USAGE_NOT_FOUND when no button is pressed.
                    {
                        LogParserError($"HidP_GetUsages falhou para {context.Name}. Status=0x{status:X8}");
                    }

                    status = NativeMethods.HidP_GetUsageValue(
                        NativeMethods.HIDP_INPUT_REPORT,
                        0x01,
                        0,
                        0x39,
                        out uint hatValue,
                        context.PreparsedData,
                        report,
                        (uint)report.Length);

                    if (status == NativeMethods.HIDP_STATUS_SUCCESS)
                    {
                        mask |= MapHatSwitch(hatValue);
                    }
                }

                return mask;
            }

            private static double ReadSignedAxis16(byte[] report, int offset)
            {
                if (offset < 0 || offset + 1 >= report.Length)
                {
                    return 0;
                }

                short raw = unchecked((short)(report[offset] | (report[offset + 1] << 8)));
                return raw >= 0
                    ? raw / 32767.0
                    : raw / 32768.0;
            }

            private static double ReadTrigger8(byte[] report, int offset)
            {
                if (offset < 0 || offset >= report.Length)
                {
                    return 0;
                }

                return report[offset] / 255.0;
            }

            private static double ClampUnit(double value)
            {
                return Math.Clamp(value, -1, 1);
            }

            private DeviceContext? GetDeviceContext(IntPtr device)
            {
                if (_devices.TryGetValue(device, out var context))
                {
                    return context;
                }

                string name = GetDeviceName(device);
                context = new DeviceContext(name);

                IntPtr handle = NativeMethods.CreateFile(
                    name,
                    0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle != IntPtr.Zero && handle != InvalidHandleValue)
                {
                    context.Handle = handle;
                    if (NativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsedData))
                    {
                        context.PreparsedData = preparsedData;
                        if (NativeMethods.HidP_GetCaps(preparsedData, out var caps) == NativeMethods.HIDP_STATUS_SUCCESS)
                        {
                            context.UsagePage = caps.UsagePage;
                            context.Usage = caps.Usage;
                            context.InputReportByteLength = caps.InputReportByteLength;
                        }
                    }
                    else
                    {
                        LogParserError($"HidD_GetPreparsedData falhou para {name}. Win32Error={Marshal.GetLastWin32Error()}");
                    }
                }
                else
                {
                    LogParserError($"CreateFile HID falhou para {name}. Win32Error={Marshal.GetLastWin32Error()}");
                }

                _devices[device] = context;
                _sysControl.LogDebug($"RawInputGamepad: dispositivo HID detectado: {name}");
                return context;
            }

            private string GetDeviceName(IntPtr device)
            {
                uint size = 0;
                NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, null, ref size);
                if (size == 0)
                {
                    return $"0x{device.ToInt64():X}";
                }

                var sb = new StringBuilder((int)size);
                uint result = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, sb, ref size);
                return result == uint.MaxValue ? $"0x{device.ToInt64():X}" : sb.ToString();
            }

            private static ushort MapButtonUsage(ushort usage)
            {
                return usage switch
                {
                    1 => NativeMethods.XINPUT_GAMEPAD_A,
                    2 => NativeMethods.XINPUT_GAMEPAD_B,
                    3 => NativeMethods.XINPUT_GAMEPAD_X,
                    4 => NativeMethods.XINPUT_GAMEPAD_Y,
                    5 => NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER,
                    6 => NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER,
                    7 => NativeMethods.XINPUT_GAMEPAD_BACK,
                    8 => NativeMethods.XINPUT_GAMEPAD_START,
                    9 => NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB,
                    10 => NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB,
                    11 => NativeMethods.XINPUT_GAMEPAD_DPAD_UP,
                    12 => NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN,
                    13 => NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT,
                    14 => NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT,
                    _ => 0
                };
            }

            private static ushort MapHatSwitch(uint value)
            {
                return value switch
                {
                    0 => NativeMethods.XINPUT_GAMEPAD_DPAD_UP,
                    1 => (ushort)(NativeMethods.XINPUT_GAMEPAD_DPAD_UP | NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT),
                    2 => NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT,
                    3 => (ushort)(NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN | NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT),
                    4 => NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN,
                    5 => (ushort)(NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN | NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT),
                    6 => NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT,
                    7 => (ushort)(NativeMethods.XINPUT_GAMEPAD_DPAD_UP | NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT),
                    _ => 0
                };
            }

            private static ushort RemoveDirectionalPad(ushort buttons)
            {
                const ushort dpadMask =
                    NativeMethods.XINPUT_GAMEPAD_DPAD_UP |
                    NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN |
                    NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT |
                    NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;

                return (ushort)(buttons & ~dpadMask);
            }

            private ushort TryParseRawReportFallback(DeviceContext context, byte[] report)
            {
                if (report.Length < 4)
                {
                    return 0;
                }

                return TryParseInferredButtonByte(context, report);
            }

            private ushort TryParseInferredButtonByte(DeviceContext context, byte[] report)
            {
                if (context.InferredButtonByteOffset >= 0)
                {
                    if (context.InferredButtonByteOffset < report.Length)
                    {
                        RememberRecentReport(context, report);
                        return MapXboxCompatibleButtonByte(report[context.InferredButtonByteOffset]);
                    }

                    context.InferredButtonByteOffset = -1;
                    context.NeutralReport = null;
                    context.RecentReports.Clear();
                }

                if (context.NeutralReport == null || context.NeutralReport.Length != report.Length)
                {
                    context.NeutralReport = (byte[])report.Clone();
                    RememberRecentReport(context, report);
                    return 0;
                }

                int inferredOffset = TryInferButtonByteOffsetFromHistory(context, report);
                if (inferredOffset >= 0)
                {
                    context.InferredButtonByteOffset = inferredOffset;
                    RememberRecentReport(context, report);
                    _sysControl.LogDebug($"RawInputGamepad: byte bruto de botões inferido por transição em offset {inferredOffset} para {context.Name}");
                    return MapXboxCompatibleButtonByte(report[inferredOffset]);
                }

                int searchStart = Math.Max(0, report.Length / 2);
                for (int i = report.Length - 1; i >= searchStart; i--)
                {
                    byte neutral = context.NeutralReport[i];
                    byte value = report[i];

                    if (neutral != 0 || value == 0 || (value & 0x40) == 0)
                    {
                        continue;
                    }

                    context.InferredButtonByteOffset = i;
                    RememberRecentReport(context, report);
                    _sysControl.LogDebug($"RawInputGamepad: byte bruto de botões inferido em offset {i} para {context.Name}");
                    return MapXboxCompatibleButtonByte(value);
                }

                RememberRecentReport(context, report);
                return 0;
            }

            private static int TryInferButtonByteOffsetFromHistory(DeviceContext context, byte[] report)
            {
                int searchStart = Math.Max(0, report.Length / 2);
                foreach (byte[] previous in context.RecentReports)
                {
                    if (previous.Length != report.Length)
                    {
                        continue;
                    }

                    for (int i = report.Length - 1; i >= searchStart; i--)
                    {
                        byte before = previous[i];
                        byte after = report[i];
                        bool beforeLooksNeutral = before == 0 && (after & 0x40) != 0;
                        bool afterLooksNeutral = after == 0 && (before & 0x40) != 0;
                        if (beforeLooksNeutral || afterLooksNeutral)
                        {
                            return i;
                        }
                    }
                }

                return -1;
            }

            private static void RememberRecentReport(DeviceContext context, byte[] report)
            {
                context.RecentReports.Add((byte[])report.Clone());
                if (context.RecentReports.Count > 8)
                {
                    context.RecentReports.RemoveAt(0);
                }
            }

            private static ushort MapXboxCompatibleButtonByte(byte buttons)
            {
                ushort mask = 0;

                if ((buttons & 0x01) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_A;
                if ((buttons & 0x02) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_B;
                if ((buttons & 0x04) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_X;
                if ((buttons & 0x08) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_Y;
                if ((buttons & 0x10) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER;
                if ((buttons & 0x20) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER;
                if ((buttons & 0x40) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_BACK;
                if ((buttons & 0x80) != 0) mask |= NativeMethods.XINPUT_GAMEPAD_START;

                return mask;
            }

            private void LogRegistrationError(string message)
            {
                long now = Environment.TickCount64;
                if (now - _lastRegistrationErrorLog > 5000)
                {
                    _lastRegistrationErrorLog = now;
                    _sysControl.LogDebug($"RawInputGamepad: {message}");
                }
            }

            private void LogParserError(string message)
            {
                long now = Environment.TickCount64;
                if (now - _lastParserErrorLog > 5000)
                {
                    _lastParserErrorLog = now;
                    _sysControl.LogDebug($"RawInputGamepad: {message}");
                }
            }

            private void LogDecodeMismatch(string message)
            {
                long now = Environment.TickCount64;
                if (now - _lastDecodeMismatchLog > 1000)
                {
                    _lastDecodeMismatchLog = now;
                    _sysControl.LogDebug($"RawInputGamepad: {message}");
                }
            }
        }

        private sealed class CalibrationSession
        {
            private readonly Dictionary<string, CalibrationDeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);

            public CalibrationSession(long expiresAt)
            {
                ExpiresAt = expiresAt;
            }

            public long ExpiresAt { get; }

            public ushort Decode(DeviceContext context, byte[] report, Action<string> log, Action<DeviceContext, int, int> saveLayout)
            {
                if (!context.IsGamepadLike)
                {
                    return 0;
                }

                string key = $"{context.VidHex}:{context.PidHex}:{context.UsagePage:X4}:{context.Usage:X4}:{report.Length}";
                if (!_devices.TryGetValue(key, out var state))
                {
                    state = new CalibrationDeviceState();
                    _devices[key] = state;
                    log($"observando {context.DisplayName}; usage=0x{context.UsagePage:X4}/0x{context.Usage:X4}; report={report.Length}.");
                }

                ushort buttons = state.Decode(report, message => log($"{context.DisplayName}: {message}"));
                if (state.HasLayout && !state.LayoutSaved)
                {
                    state.LayoutSaved = true;
                    saveLayout(context, report.Length, state.ButtonByteOffset);
                }

                return buttons;
            }
        }

        private sealed class CalibrationDeviceState
        {
            private byte[]? _neutralReport;
            private readonly List<byte[]> _recentReports = new();
            private ushort _lastLoggedButtons;

            public int ButtonByteOffset { get; private set; } = -1;
            public bool HasLayout => ButtonByteOffset >= 0;
            public bool LayoutSaved { get; set; }

            public ushort Decode(byte[] report, Action<string> log)
            {
                if (report.Length < 4)
                {
                    return 0;
                }

                if (ButtonByteOffset >= 0)
                {
                    ushort buttons = MapXboxCompatibleButtonByte(report[ButtonByteOffset]);
                    LogPresses(buttons, log);
                    Remember(report);
                    return buttons;
                }

                if (_neutralReport == null || _neutralReport.Length != report.Length)
                {
                    _neutralReport = (byte[])report.Clone();
                    Remember(report);
                    log("primeiro pacote HID usado como baseline.");
                    return 0;
                }

                int offset = TryInferButtonByteOffset(report);
                if (offset >= 0)
                {
                    ButtonByteOffset = offset;
                    ushort buttons = MapXboxCompatibleButtonByte(report[offset]);
                    log($"layout XboxButtonByteV1 inferido: buttonByteOffset={offset}, valor=0x{report[offset]:X2}, botões={FormatButtons(buttons)}.");
                    LogPresses(buttons, log);
                    Remember(report);
                    return buttons;
                }

                Remember(report);
                return 0;
            }

            private int TryInferButtonByteOffset(byte[] report)
            {
                int searchStart = Math.Max(0, report.Length / 2);
                foreach (byte[] previous in _recentReports)
                {
                    if (previous.Length != report.Length)
                    {
                        continue;
                    }

                    for (int i = report.Length - 1; i >= searchStart; i--)
                    {
                        byte before = previous[i];
                        byte after = report[i];
                        if ((before == 0 && after != 0) || (after == 0 && before != 0))
                        {
                            return i;
                        }
                    }
                }

                return -1;
            }

            private void LogPresses(ushort buttons, Action<string> log)
            {
                if (buttons == _lastLoggedButtons)
                {
                    return;
                }

                ushort pressed = (ushort)(buttons & ~_lastLoggedButtons);
                ushort released = (ushort)(~buttons & _lastLoggedButtons);
                log($"HID buttons=0x{buttons:X4} [{FormatButtons(buttons)}] pressed=0x{pressed:X4} released=0x{released:X4}");
                _lastLoggedButtons = buttons;
            }

            private void Remember(byte[] report)
            {
                _recentReports.Add((byte[])report.Clone());
                if (_recentReports.Count > 12)
                {
                    _recentReports.RemoveAt(0);
                }
            }
        }

        private sealed class DeviceContext : IDisposable
        {
            public DeviceContext(string name)
            {
                Name = name;
                VidHex = ExtractId(name, "vid_");
                PidHex = ExtractId(name, "pid_");
            }

            public string Name { get; }
            public string DisplayName => string.IsNullOrWhiteSpace(VidHex) || string.IsNullOrWhiteSpace(PidHex)
                ? Name
                : $"VID_{VidHex} PID_{PidHex}";
            public string VidHex { get; }
            public string PidHex { get; }
            public ushort UsagePage { get; set; }
            public ushort Usage { get; set; }
            public ushort InputReportByteLength { get; set; }
            public bool IsGamepadLike => UsagePage == 0x01 && (Usage == 0x04 || Usage == 0x05 || Usage == 0x08);
            public IntPtr Handle { get; set; }
            public IntPtr PreparsedData { get; set; }
            public byte[]? NeutralReport { get; set; }
            public int InferredButtonByteOffset { get; set; } = -1;
            public List<byte[]> RecentReports { get; } = new();

            private static string ExtractId(string devicePath, string key)
            {
                int start = devicePath.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    return string.Empty;
                }

                start += key.Length;
                int end = start;
                while (end < devicePath.Length && Uri.IsHexDigit(devicePath[end]))
                {
                    end++;
                }

                return end > start ? devicePath.Substring(start, end - start).ToUpperInvariant() : string.Empty;
            }

            public void Dispose()
            {
                if (PreparsedData != IntPtr.Zero)
                {
                    NativeMethods.HidD_FreePreparsedData(PreparsedData);
                    PreparsedData = IntPtr.Zero;
                }

                if (Handle != IntPtr.Zero && Handle != new IntPtr(-1))
                {
                    NativeMethods.CloseHandle(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }
    }
}
