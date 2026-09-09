using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using WgiGamepad = Windows.Gaming.Input.Gamepad;
using WgiGamepadButtons = Windows.Gaming.Input.GamepadButtons;

namespace GamepadLiveTest;

internal static class Program
{
    private const ushort XInputDPadUp = 0x0001;
    private const ushort XInputDPadDown = 0x0002;
    private const ushort XInputDPadLeft = 0x0004;
    private const ushort XInputDPadRight = 0x0008;
    private const ushort XInputStart = 0x0010;
    private const ushort XInputBack = 0x0020;
    private const ushort XInputLeftThumb = 0x0040;
    private const ushort XInputRightThumb = 0x0080;
    private const ushort XInputLeftShoulder = 0x0100;
    private const ushort XInputRightShoulder = 0x0200;
    private const ushort XInputA = 0x1000;
    private const ushort XInputB = 0x2000;
    private const ushort XInputX = 0x4000;
    private const ushort XInputY = 0x8000;

    private readonly record struct AxisSnapshot(
        ushort Buttons,
        double LeftX,
        double LeftY,
        double RightX,
        double RightY,
        double LeftTrigger,
        double RightTrigger)
    {
        public bool DiffersFrom(AxisSnapshot other)
        {
            const double threshold = 0.015;
            return Buttons != other.Buttons ||
                Math.Abs(LeftX - other.LeftX) > threshold ||
                Math.Abs(LeftY - other.LeftY) > threshold ||
                Math.Abs(RightX - other.RightX) > threshold ||
                Math.Abs(RightY - other.RightY) > threshold ||
                Math.Abs(LeftTrigger - other.LeftTrigger) > threshold ||
                Math.Abs(RightTrigger - other.RightTrigger) > threshold;
        }

        public override string ToString()
        {
            return $"buttons=0x{Buttons:X4} [{FormatButtons(Buttons)}] " +
                $"LX={LeftX,7:0.000} LY={LeftY,7:0.000} RX={RightX,7:0.000} RY={RightY,7:0.000} LT={LeftTrigger,5:0.000} RT={RightTrigger,5:0.000}";
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        int seconds = ParseSeconds(args, 90);
        string logPath = Path.GetFullPath(args.FirstOrDefault(a => a.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) ?? "GamepadLiveTest.log");

        using var logger = new LiveLogger(logPath);
        logger.Write("START", $"Gamepad live test iniciado. duration={seconds}s log={logPath}");
        logger.Write("INFO", "Teste alvo: entrar no modo Xbox e pressionar BACK+X. Nao usa ForceKill nem executa atalhos reais.");
        logger.Write("INFO", "Canais: XInput/WGI axis polling, Raw Input/HID bruto + axis candidates, low-level keyboard hook.");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var cts = new CancellationTokenSource();
        using var keyboardHook = new KeyboardHookLogger(logger);
        keyboardHook.Start();

        using var rawWindow = new RawInputWindow(logger);
        rawWindow.Register();

        Task pollTask = Task.Run(() => PollLoop(logger, cts.Token), cts.Token);
        Task.Delay(TimeSpan.FromSeconds(seconds), CancellationToken.None).ContinueWith(_ =>
        {
            logger.Write("STOP", "Tempo de teste encerrado.");
            try
            {
                rawWindow.BeginInvoke((Action)rawWindow.Close);
            }
            catch
            {
                rawWindow.Close();
            }
        }, CancellationToken.None);

        Application.Run(rawWindow);

        cts.Cancel();
        try { pollTask.Wait(TimeSpan.FromSeconds(2)); } catch { }

        logger.Write("END", "Gamepad live test finalizado.");
        return 0;
    }

    private static int ParseSeconds(string[] args, int fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase) ||
                 args[i].Equals("-s", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int seconds))
            {
                return Math.Clamp(seconds, 5, 600);
            }
        }

        return fallback;
    }

    private static async Task PollLoop(LiveLogger logger, CancellationToken token)
    {
        using var xInput = new XInputReader(logger);
        ushort lastXInputButtons = 0;
        ushort lastWgiButtons = 0;
        bool lastXInputConnected = false;
        bool lastWgiConnected = false;
        var lastXInputAxes = new Dictionary<string, AxisSnapshot>();
        var lastWgiAxes = new Dictionary<string, AxisSnapshot>();
        long lastIdleLog = 0;

        while (!token.IsCancellationRequested)
        {
            bool xInputConnected = xInput.TryReadButtons(out ushort xInputButtons);
            if (xInputConnected != lastXInputConnected)
            {
                logger.Write("XINPUT", $"connected={xInputConnected}");
                lastXInputConnected = xInputConnected;
            }

            if (xInputConnected && xInputButtons != lastXInputButtons)
            {
                LogButtonState(logger, "XINPUT", xInputButtons);
                lastXInputButtons = xInputButtons;
            }

            foreach (var (source, snapshot) in xInput.ReadAxisSnapshots())
            {
                if (!lastXInputAxes.TryGetValue(source, out var previous) || snapshot.DiffersFrom(previous))
                {
                    lastXInputAxes[source] = snapshot;
                    logger.Write("XINPUT-AXIS", $"{source}: {snapshot}");
                }
            }

            bool wgiConnected = TryReadWgiButtons(logger, out ushort wgiButtons);
            if (wgiConnected != lastWgiConnected)
            {
                logger.Write("WGI", $"connected={wgiConnected}");
                lastWgiConnected = wgiConnected;
            }

            if (wgiConnected && wgiButtons != lastWgiButtons)
            {
                LogButtonState(logger, "WGI", wgiButtons);
                lastWgiButtons = wgiButtons;
            }

            foreach (var (source, snapshot) in ReadWgiAxisSnapshots(logger))
            {
                if (!lastWgiAxes.TryGetValue(source, out var previous) || snapshot.DiffersFrom(previous))
                {
                    lastWgiAxes[source] = snapshot;
                    logger.Write("WGI-AXIS", $"{source}: {snapshot}");
                }
            }

            long now = Environment.TickCount64;
            if (!xInputConnected && !wgiConnected && now - lastIdleLog > 3000)
            {
                lastIdleLog = now;
                logger.Write("IDLE", "Nenhum controle conectado por XInput/WGI; aguardando Raw Input/HID ou reconexao.");
            }

            await Task.Delay(16, token).ConfigureAwait(false);
        }
    }

    private static bool TryReadWgiButtons(LiveLogger logger, out ushort buttons)
    {
        buttons = 0;
        try
        {
            var gamepads = WgiGamepad.Gamepads;
            if (gamepads.Count == 0)
            {
                return false;
            }

            foreach (var gamepad in gamepads)
            {
                var reading = gamepad.GetCurrentReading();
                buttons |= MapWgiButtons(reading.Buttons);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.WriteThrottled("WGI-ERROR", ex.Message, TimeSpan.FromSeconds(5));
            return false;
        }
    }

    private static IEnumerable<(string source, AxisSnapshot snapshot)> ReadWgiAxisSnapshots(LiveLogger logger)
    {
        try
        {
            var gamepads = WgiGamepad.Gamepads;
            for (int i = 0; i < gamepads.Count; i++)
            {
                var reading = gamepads[i].GetCurrentReading();
                yield return ($"pad{i}", new AxisSnapshot(
                    MapWgiButtons(reading.Buttons),
                    reading.LeftThumbstickX,
                    reading.LeftThumbstickY,
                    reading.RightThumbstickX,
                    reading.RightThumbstickY,
                    reading.LeftTrigger,
                    reading.RightTrigger));
            }
        }
        finally
        {
        }
    }

    private static ushort MapWgiButtons(WgiGamepadButtons buttons)
    {
        ushort mask = 0;
        if ((buttons & WgiGamepadButtons.DPadUp) != 0) mask |= XInputDPadUp;
        if ((buttons & WgiGamepadButtons.DPadDown) != 0) mask |= XInputDPadDown;
        if ((buttons & WgiGamepadButtons.DPadLeft) != 0) mask |= XInputDPadLeft;
        if ((buttons & WgiGamepadButtons.DPadRight) != 0) mask |= XInputDPadRight;
        if ((buttons & WgiGamepadButtons.Menu) != 0) mask |= XInputStart;
        if ((buttons & WgiGamepadButtons.View) != 0) mask |= XInputBack;
        if ((buttons & WgiGamepadButtons.LeftThumbstick) != 0) mask |= XInputLeftThumb;
        if ((buttons & WgiGamepadButtons.RightThumbstick) != 0) mask |= XInputRightThumb;
        if ((buttons & WgiGamepadButtons.LeftShoulder) != 0) mask |= XInputLeftShoulder;
        if ((buttons & WgiGamepadButtons.RightShoulder) != 0) mask |= XInputRightShoulder;
        if ((buttons & WgiGamepadButtons.A) != 0) mask |= XInputA;
        if ((buttons & WgiGamepadButtons.B) != 0) mask |= XInputB;
        if ((buttons & WgiGamepadButtons.X) != 0) mask |= XInputX;
        if ((buttons & WgiGamepadButtons.Y) != 0) mask |= XInputY;
        return mask;
    }

    private static void LogButtonState(LiveLogger logger, string source, ushort buttons)
    {
        string names = FormatButtons(buttons);
        string backX = (buttons & (XInputBack | XInputX)) == (XInputBack | XInputX)
            ? " BACK+X_DETECTED"
            : string.Empty;
        logger.Write(source, $"buttons=0x{buttons:X4} [{names}]{backX}");
    }

    private static string FormatButtons(ushort buttons)
    {
        if (buttons == 0)
        {
            return "none";
        }

        var names = new List<string>();
        Add(XInputDPadUp, "UP");
        Add(XInputDPadDown, "DOWN");
        Add(XInputDPadLeft, "LEFT");
        Add(XInputDPadRight, "RIGHT");
        Add(XInputStart, "START/MENU");
        Add(XInputBack, "BACK/VIEW");
        Add(XInputLeftThumb, "L-THUMB");
        Add(XInputRightThumb, "R-THUMB");
        Add(XInputLeftShoulder, "LB");
        Add(XInputRightShoulder, "RB");
        Add(XInputA, "A");
        Add(XInputB, "B");
        Add(XInputX, "X");
        Add(XInputY, "Y");
        return string.Join("+", names);

        void Add(ushort mask, string name)
        {
            if ((buttons & mask) != 0)
            {
                names.Add(name);
            }
        }
    }

    internal sealed class LiveLogger : IDisposable
    {
        private readonly object _lock = new();
        private readonly StreamWriter _writer;
        private readonly ConcurrentDictionary<string, long> _lastThrottled = new();

        public LiveLogger(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public void Write(string source, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}";
            lock (_lock)
            {
                Console.WriteLine(line);
                _writer.WriteLine(line);
            }
        }

        public void WriteThrottled(string source, string message, TimeSpan interval)
        {
            long now = Environment.TickCount64;
            string key = $"{source}:{message}";
            long last = _lastThrottled.GetOrAdd(key, 0);
            if (last == 0 || now - last >= interval.TotalMilliseconds)
            {
                _lastThrottled[key] = now;
                Write(source, message);
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed class XInputReader : IDisposable
    {
        private readonly LiveLogger _logger;
        private readonly XInputGetStateDelegate? _getState;
        private readonly List<IntPtr> _loadedLibraries = new();

        public XInputReader(LiveLogger logger)
        {
            _logger = logger;

            foreach (string dll in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                IntPtr module = LoadLibrary(dll);
                if (module == IntPtr.Zero)
                {
                    continue;
                }

                _loadedLibraries.Add(module);
                IntPtr proc = GetProcAddress(module, "XInputGetState");
                if (proc != IntPtr.Zero)
                {
                    _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(proc);
                    _logger.Write("XINPUT", $"loaded={dll}");
                    break;
                }
            }

            if (_getState == null)
            {
                _logger.Write("XINPUT", "XInputGetState indisponivel.");
            }
        }

        public bool TryReadButtons(out ushort buttons)
        {
            buttons = 0;
            if (_getState == null)
            {
                return false;
            }

            bool connected = false;
            for (uint index = 0; index < 4; index++)
            {
                if (_getState(index, out XInputState state) == 0)
                {
                    buttons |= state.Gamepad.wButtons;
                    connected = true;
                }
            }

            return connected;
        }

        public IEnumerable<(string source, AxisSnapshot snapshot)> ReadAxisSnapshots()
        {
            if (_getState == null)
            {
                yield break;
            }

            for (uint index = 0; index < 4; index++)
            {
                if (_getState(index, out XInputState state) != 0)
                {
                    continue;
                }

                yield return ($"pad{index}", new AxisSnapshot(
                    state.Gamepad.wButtons,
                    NormalizeSignedThumb(state.Gamepad.sThumbLX),
                    NormalizeSignedThumb(state.Gamepad.sThumbLY),
                    NormalizeSignedThumb(state.Gamepad.sThumbRX),
                    NormalizeSignedThumb(state.Gamepad.sThumbRY),
                    state.Gamepad.bLeftTrigger / 255.0,
                    state.Gamepad.bRightTrigger / 255.0));
            }
        }

        private static double NormalizeSignedThumb(short value)
        {
            return value >= 0
                ? value / 32767.0
                : value / 32768.0;
        }

        public void Dispose()
        {
            foreach (IntPtr module in _loadedLibraries)
            {
                FreeLibrary(module);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint XInputGetStateDelegate(uint dwUserIndex, out XInputState pState);

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint dwPacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
    }

    private sealed class KeyboardHookLogger : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_INJECTED = 0x10;

        private readonly LiveLogger _logger;
        private LowLevelKeyboardProc? _proc;
        private IntPtr _hookId;

        public KeyboardHookLogger(LiveLogger logger)
        {
            _logger = logger;
        }

        public void Start()
        {
            _proc = HookCallback;
            using Process process = Process.GetCurrentProcess();
            using ProcessModule? module = process.MainModule;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero, 0);
            _logger.Write("KBD", _hookId != IntPtr.Zero
                ? "low-level keyboard hook instalado."
                : $"falha ao instalar hook. Win32Error={Marshal.GetLastWin32Error()}");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                {
                    var kbd = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                    bool injected = (kbd.flags & LLKHF_INJECTED) != 0;
                    bool interesting = IsInterestingVk(kbd.vkCode);
                    if (interesting)
                    {
                        _logger.Write("KBD", $"{(down ? "down" : "up")} vk=0x{kbd.vkCode:X2} scan=0x{kbd.scanCode:X2} flags=0x{kbd.flags:X2} injected={injected}");
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool IsInterestingVk(uint vk)
        {
            return vk is 0x08 or 0x09 or 0x11 or 0x12 or 0x47 or 0x52 or 0x58 or 0x59
                or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xA6 or 0x5B or 0x5C;
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }

    private sealed class RawInputWindow : Form
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDI_DEVICENAME = 0x20000007;

        private readonly LiveLogger _logger;
        private readonly Dictionary<IntPtr, string> _deviceNames = new();
        private readonly Dictionary<IntPtr, string> _lastPayloadByDevice = new();
        private readonly Dictionary<IntPtr, string> _lastAxisProbeByDevice = new();
        private readonly Dictionary<IntPtr, AppRawDeviceState> _appStates = new();

        public RawInputWindow(LiveLogger logger)
        {
            _logger = logger;
            ShowInTaskbar = false;
            Opacity = 0;
            Width = 1;
            Height = 1;
        }

        public void Register()
        {
            CreateHandle();

            var devices = new[]
            {
                new RawInputDevice { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle },
                new RawInputDevice { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle },
                new RawInputDevice { usUsagePage = 0x01, usUsage = 0x08, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle }
            };

            bool ok = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
            _logger.Write("RAW", ok
                ? "registrado para joystick/gamepad/multi-axis com RIDEV_INPUTSINK."
                : $"falha RegisterRawInputDevices. Win32Error={Marshal.GetLastWin32Error()}");
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }

            base.WndProc(ref m);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            uint result = GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (result == uint.MaxValue || size == 0)
            {
                _logger.WriteThrottled("RAW-ERROR", $"GetRawInputData size falhou. Win32Error={Marshal.GetLastWin32Error()}", TimeSpan.FromSeconds(3));
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                result = GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize);
                if (result == uint.MaxValue)
                {
                    _logger.WriteThrottled("RAW-ERROR", $"GetRawInputData payload falhou. Win32Error={Marshal.GetLastWin32Error()}", TimeSpan.FromSeconds(3));
                    return;
                }

                var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                if (header.dwType != RIM_TYPEHID)
                {
                    return;
                }

                int hidOffset = Marshal.SizeOf<RawInputHeader>();
                uint sizeHid = (uint)Marshal.ReadInt32(buffer, hidOffset);
                uint count = (uint)Marshal.ReadInt32(buffer, hidOffset + 4);
                int payloadOffset = hidOffset + 8;
                int payloadBytes = checked((int)(sizeHid * count));
                int bytesToCopy = Math.Min(payloadBytes, 96);
                var bytes = new byte[bytesToCopy];
                Marshal.Copy(IntPtr.Add(buffer, payloadOffset), bytes, 0, bytesToCopy);

                string hex = Convert.ToHexString(bytes);
                string key = $"{sizeHid}:{count}:{hex}";
                if (_lastPayloadByDevice.TryGetValue(header.hDevice, out string? last) && last == key)
                {
                    return;
                }

                _lastPayloadByDevice[header.hDevice] = key;
                string deviceName = GetDeviceName(header.hDevice);
                ushort xboxButtons = TryDecodeKnownXboxHidReport(deviceName, bytes);
                string decoded = xboxButtons != 0
                    ? $" decoded=0x{xboxButtons:X4} [{FormatButtons(xboxButtons)}]"
                    : string.Empty;
                string backX = (xboxButtons & (XInputBack | XInputX)) == (XInputBack | XInputX)
                    ? " BACK+X_DETECTED"
                    : string.Empty;
                string backA = (xboxButtons & (XInputBack | XInputA)) == (XInputBack | XInputA)
                    ? " BACK+A_DETECTED"
                    : string.Empty;
                _logger.Write("RAW-HID", $"device={deviceName} sizeHid={sizeHid} count={count} bytes={hex}{decoded}{backX}{backA}");

                string axisProbe = FormatAxisProbe(bytes, (int)sizeHid);
                if (!_lastAxisProbeByDevice.TryGetValue(header.hDevice, out string? lastAxisProbe) || lastAxisProbe != axisProbe)
                {
                    _lastAxisProbeByDevice[header.hDevice] = axisProbe;
                    _logger.Write("RAW-AXIS", $"device={deviceName} {axisProbe}");
                }

                ushort appButtons = 0;
                var appState = GetAppState(header.hDevice);
                for (int i = 0; i < count; i++)
                {
                    var report = new byte[sizeHid];
                    Marshal.Copy(IntPtr.Add(buffer, payloadOffset + i * (int)sizeHid), report, 0, report.Length);
                    appButtons = appState.Decode(report, message => _logger.Write("RAW-APP", $"{deviceName}: {message}"));
                }

                if (appState.Update(appButtons, out string appLine))
                {
                    _logger.Write("RAW-APP", $"{deviceName}: {appLine}");
                }
            }
            catch (Exception ex)
            {
                _logger.Write("RAW-ERROR", ex.ToString());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private AppRawDeviceState GetAppState(IntPtr device)
        {
            if (!_appStates.TryGetValue(device, out var state))
            {
                state = new AppRawDeviceState();
                _appStates[device] = state;
            }

            return state;
        }

        private string GetDeviceName(IntPtr device)
        {
            if (_deviceNames.TryGetValue(device, out string? cached))
            {
                return cached;
            }

            uint size = 0;
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref size);
            if (size == 0)
            {
                string fallback = $"0x{device.ToInt64():X}";
                _deviceNames[device] = fallback;
                return fallback;
            }

            var sb = new StringBuilder((int)size);
            uint result = GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref size);
            string name = result == uint.MaxValue ? $"0x{device.ToInt64():X}" : sb.ToString();
            _deviceNames[device] = name;
            return name;
        }

        private static string FormatAxisProbe(byte[] payload, int reportLength)
        {
            byte[] report = payload.Length > reportLength
                ? payload.Take(reportLength).ToArray()
                : payload;

            var parts = new List<string>
            {
                $"reportLen={report.Length}"
            };

            if (report.Length >= 9)
            {
                parts.Add("s16(o1,o3,o5,o7)=" +
                    $"{ReadS16(report, 1),7:0.000}," +
                    $"{ReadS16(report, 3),7:0.000}," +
                    $"{ReadS16(report, 5),7:0.000}," +
                    $"{ReadS16(report, 7),7:0.000}");
                parts.Add("u16(o1,o3,o5,o7)=" +
                    $"{ReadU16(report, 1),7:0.000}," +
                    $"{ReadU16(report, 3),7:0.000}," +
                    $"{ReadU16(report, 5),7:0.000}," +
                    $"{ReadU16(report, 7),7:0.000}");
            }

            if (report.Length >= 11)
            {
                parts.Add($"trig(o9,o10)={ReadU8(report, 9),5:0.000},{ReadU8(report, 10),5:0.000}");
            }

            if (report.Length >= 12)
            {
                parts.Add($"btn11=0x{report[11]:X2}");
            }

            parts.Add("bytes[0..15]=" + string.Join(" ", report.Take(Math.Min(16, report.Length)).Select((b, i) => $"{i}:{b:X2}")));
            return string.Join(" | ", parts);
        }

        private static double ReadS16(byte[] report, int offset)
        {
            if (offset < 0 || offset + 1 >= report.Length)
            {
                return 0;
            }

            short raw = unchecked((short)(report[offset] | (report[offset + 1] << 8)));
            return raw >= 0 ? raw / 32767.0 : raw / 32768.0;
        }

        private static double ReadU16(byte[] report, int offset)
        {
            if (offset < 0 || offset + 1 >= report.Length)
            {
                return 0;
            }

            ushort raw = (ushort)(report[offset] | (report[offset + 1] << 8));
            return raw <= 32767
                ? raw / 32767.0
                : -((65535 - raw) / 32768.0);
        }

        private static double ReadU8(byte[] report, int offset)
        {
            if (offset < 0 || offset >= report.Length)
            {
                return 0;
            }

            return report[offset] / 255.0;
        }

        private static ushort TryDecodeKnownXboxHidReport(string deviceName, byte[] report)
        {
            if (!deviceName.Contains("VID_045E", StringComparison.OrdinalIgnoreCase) || report.Length < 12)
            {
                return 0;
            }

            byte buttons = report[11];
            ushort mask = 0;
            if ((buttons & 0x01) != 0) mask |= XInputA;
            if ((buttons & 0x02) != 0) mask |= XInputB;
            if ((buttons & 0x04) != 0) mask |= XInputX;
            if ((buttons & 0x08) != 0) mask |= XInputY;
            if ((buttons & 0x10) != 0) mask |= XInputLeftShoulder;
            if ((buttons & 0x20) != 0) mask |= XInputRightShoulder;
            if ((buttons & 0x40) != 0) mask |= XInputBack;
            if ((buttons & 0x80) != 0) mask |= XInputStart;
            return mask;
        }

        private sealed class AppRawDeviceState
        {
            private byte[]? _neutralReport;
            private int _inferredButtonByteOffset = -1;
            private readonly List<byte[]> _recentReports = new();
            private ushort _prevButtons;
            private long _lastBackRelease;

            public ushort Decode(byte[] report, Action<string> log)
            {
                if (report.Length < 4)
                {
                    return 0;
                }

                if (_inferredButtonByteOffset >= 0)
                {
                    if (_inferredButtonByteOffset < report.Length)
                    {
                        RememberRecentReport(report);
                        return MapXboxCompatibleButtonByte(report[_inferredButtonByteOffset]);
                    }

                    _inferredButtonByteOffset = -1;
                    _neutralReport = null;
                    _recentReports.Clear();
                }

                if (_neutralReport == null || _neutralReport.Length != report.Length)
                {
                    _neutralReport = (byte[])report.Clone();
                    RememberRecentReport(report);
                    return 0;
                }

                int inferredOffset = TryInferButtonByteOffsetFromHistory(report);
                if (inferredOffset >= 0)
                {
                    _inferredButtonByteOffset = inferredOffset;
                    RememberRecentReport(report);
                    log($"byte bruto de botões inferido por transição em offset {inferredOffset}");
                    return MapXboxCompatibleButtonByte(report[inferredOffset]);
                }

                int searchStart = Math.Max(0, report.Length / 2);
                for (int i = report.Length - 1; i >= searchStart; i--)
                {
                    byte neutral = _neutralReport[i];
                    byte value = report[i];

                    if (neutral != 0 || value == 0 || (value & 0x40) == 0)
                    {
                        continue;
                    }

                    _inferredButtonByteOffset = i;
                    RememberRecentReport(report);
                    log($"byte bruto de botões inferido em offset {i}");
                    return MapXboxCompatibleButtonByte(value);
                }

                RememberRecentReport(report);
                return 0;
            }

            private int TryInferButtonByteOffsetFromHistory(byte[] report)
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

            private void RememberRecentReport(byte[] report)
            {
                _recentReports.Add((byte[])report.Clone());
                if (_recentReports.Count > 8)
                {
                    _recentReports.RemoveAt(0);
                }
            }

            public bool Update(ushort buttons, out string line)
            {
                line = string.Empty;
                if (buttons == _prevButtons)
                {
                    return false;
                }

                ushort pressed = (ushort)(buttons & ~_prevButtons);
                ushort released = (ushort)(~buttons & _prevButtons);
                long now = Environment.TickCount64;
                var triggers = new List<string>();

                AddPressTrigger(XInputBack | XInputA, "BACK+A");
                AddPressTrigger(XInputBack | XInputX, "BACK+X");
                AddPressTrigger(XInputBack | XInputY, "BACK+Y");
                AddPressTrigger(XInputBack | XInputLeftShoulder, "BACK+LB");
                AddPressTrigger(XInputBack | XInputRightShoulder, "BACK+RB");

                bool backReleased = (released & XInputBack) != 0;
                bool standaloneBackRelease = backReleased && (_prevButtons & ~XInputBack) == 0;
                if (standaloneBackRelease)
                {
                    long delta = _lastBackRelease == 0 ? long.MaxValue : now - _lastBackRelease;
                    if (delta <= 500)
                    {
                        triggers.Add("DOUBLE_BACK");
                        _lastBackRelease = 0;
                    }
                    else
                    {
                        _lastBackRelease = now;
                    }
                }
                else if ((buttons & XInputBack) != 0 && (buttons & ~XInputBack) != 0)
                {
                    _lastBackRelease = 0;
                }

                line = $"buttons=0x{buttons:X4} [{FormatButtons(buttons)}] pressed=0x{pressed:X4} released=0x{released:X4}";
                if (triggers.Count > 0)
                {
                    line += $" triggers={string.Join(",", triggers)}";
                }

                _prevButtons = buttons;
                return true;

                void AddPressTrigger(int combo, string name)
                {
                    ushort comboMask = (ushort)combo;
                    if ((buttons & comboMask) == comboMask && (pressed & comboMask) != 0)
                    {
                        triggers.Add(name);
                    }
                }
            }

            private static ushort MapXboxCompatibleButtonByte(byte buttons)
            {
                ushort mask = 0;
                if ((buttons & 0x01) != 0) mask |= XInputA;
                if ((buttons & 0x02) != 0) mask |= XInputB;
                if ((buttons & 0x04) != 0) mask |= XInputX;
                if ((buttons & 0x08) != 0) mask |= XInputY;
                if ((buttons & 0x10) != 0) mask |= XInputLeftShoulder;
                if ((buttons & 0x20) != 0) mask |= XInputRightShoulder;
                if ((buttons & 0x40) != 0) mask |= XInputBack;
                if ((buttons & 0x80) != 0) mask |= XInputStart;
                return mask;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RawInputDevice[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            StringBuilder? pData,
            ref uint pcbSize);
    }
}
