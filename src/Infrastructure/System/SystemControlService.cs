using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    public partial class SystemControlService : ISystemControlService
    {
        private static readonly IntPtr SimulatedKeySignature = new IntPtr(0x12345);

        public AppConfig Config { get; }
        public AppStatus Status { get; } = new();
        public event Action? StatusChanged;
        public event Action<string>? LogAdded;
        public event Action<string, string>? NotificationRequested;

        private readonly record struct LogWriteEntry(string Path, string Line);

        private static readonly object MouseHidSyncRoot = new();
        private static readonly object KeyboardHidSyncRoot = new();
        private static readonly object PeripheralControlSyncRoot = new();
        private static readonly object DisplaySettingsSyncRoot = new();
        private static readonly object AppSettingsWriteSyncRoot = new();
        private static int ForceKillInProgress;
        private static long LastForceKillStartedAt;
        private const int ForceKillCooldownMs = 900;
        private static readonly BlockingCollection<LogWriteEntry> LogWriteQueue = new(new ConcurrentQueue<LogWriteEntry>());
        private static readonly ManualResetEventSlim LogQueueDrained = new(true);
        private static readonly Task LogWriterTask = Task.Run(ProcessLogWriteQueue);
        private static int PendingLogWrites;
        private static long ProfileTransitionSequence;
        private readonly object _foregroundProcessCacheLock = new();
        private string? _cachedForegroundProcessName;
        private long _cachedForegroundProcessTimestamp;
        private DISPLAYCONFIG_PATH_INFO[]? _displayTogglePaths;
        private DISPLAYCONFIG_MODE_INFO[]? _displayToggleModes;
        private bool _displayToggleUsesTopologyDatabase;

        private Views.KeyboardShortcutOverlayWindow? _keyboardShortcutOverlay;
        private volatile bool _isKeyboardShortcutOverlayVisible;
        public bool IsKeyboardShortcutOverlayVisible => _isKeyboardShortcutOverlayVisible;

        private Views.GamepadShortcutOverlayWindow? _gamepadShortcutOverlay;
        private volatile bool _isGamepadShortcutOverlayVisible;
        public bool IsGamepadShortcutOverlayVisible => _isGamepadShortcutOverlayVisible;

        public LaunchFocusAssist? LaunchFocusAssist { get; private set; }
        public PlayniteBridge? PlayniteBridge { get; private set; }

        public SystemControlService(IConfiguration configuration)
        {
            Config = new AppConfig();
            configuration.Bind(Config);
            Config.InitializeDefaults();
            Config.DebugLogPath = GetDebugLogPath();

            // Determinar a resolução atual do sistema no início
            if (GetCurrentResolution(out uint w, out uint h, out uint r))
            {
                Status.CurrentResolution = $"{w}x{h}@{r}Hz";
            }
            
            // Inicializar somente o status com a velocidade atual do Windows.
            // Não aplicar perfil de mouse no construtor: isso causava um salto
            // desnecessário para SlowSpeed no startup do app e do helper admin.
            InitializeMouseSpeedStatusFromSystem();

            bool isAdminHelper = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--admin-helper", StringComparison.OrdinalIgnoreCase));
            if (!isAdminHelper)
            {
                LaunchFocusAssist = new LaunchFocusAssist(LogDebug);
                if (Config.GameConsole?.PlayniteBridgeEnabled == true)
                {
                    PlayniteBridge = new PlayniteBridge(LaunchFocusAssist, LogDebug);
                    PlayniteBridge.Start();
                }
            }
        }

        private void OnStatusChanged()
        {
            StatusChanged?.Invoke();
        }

        private void InitializeMouseSpeedStatusFromSystem()
        {
            int? currentSpeed = GetCurrentMouseSpeed();
            if (currentSpeed.HasValue)
            {
                Status.MouseSpeed = currentSpeed.Value;
                LogDebug($"SystemControlService: velocidade atual do mouse detectada: {currentSpeed.Value}/20. Nenhuma alteração aplicada no construtor.");
            }
            else
            {
                Status.MouseSpeed = Config.Mouse.HighSpeed;
                LogDebug($"SystemControlService: falha ao ler velocidade atual do mouse. Mantendo status como perfil normal ({Config.Mouse.HighSpeed}/20). LastWin32Error={Marshal.GetLastWin32Error()}.");
            }
        }

        public int? GetCurrentMouseSpeed()
        {
            try
            {
                int currentSpeed = 0;
                bool success = NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_GETMOUSESPEED,
                    0,
                    ref currentSpeed,
                    0);

                if (success && currentSpeed >= 1 && currentSpeed <= 20)
                {
                    return currentSpeed;
                }

                LogDebug($"GetCurrentMouseSpeed: falha ao ler SPI_GETMOUSESPEED. LastWin32Error={Marshal.GetLastWin32Error()}.");
            }
            catch (Exception ex)
            {
                LogDebug($"GetCurrentMouseSpeed: exceção ao ler velocidade atual do mouse: {ex.Message}");
            }

            return null;
        }


        public void NotifyStatusChanged()
        {
            OnStatusChanged();
        }

        public void SetStatusMessage(string message)
        {
            Status.StatusMessage = message;
            OnStatusChanged();
        }

        public static string GetDebugLogPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "DebugLog.txt");
        }

        public void LogDebug(string msg)
        {
            if (!Config.DebugEnabled) return;

            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID:{Environment.ProcessId}] [TID:{Environment.CurrentManagedThreadId}] {msg}";
            bool pendingRegistered = false;
            try
            {
                Interlocked.Increment(ref PendingLogWrites);
                pendingRegistered = true;
                LogQueueDrained.Reset();
                if (!LogWriteQueue.TryAdd(new LogWriteEntry(GetDebugLogPath(), logLine)))
                {
                    MarkLogEntryProcessed();
                    pendingRegistered = false;
                    Debug.WriteLine("Falha ao enfileirar log assíncrono.");
                }
            }
            catch (Exception ex)
            {
                if (pendingRegistered)
                {
                    MarkLogEntryProcessed();
                }
                Debug.WriteLine($"Falha ao enfileirar log: {ex.Message}");
            }

            try
            {
                LogAdded?.Invoke(logLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Falha ao notificar observador de log: {ex.Message}");
            }
        }

        private static void ProcessLogWriteQueue()
        {
            foreach (var firstEntry in LogWriteQueue.GetConsumingEnumerable())
            {
                var batch = new List<LogWriteEntry> { firstEntry };
                while (batch.Count < 64 && LogWriteQueue.TryTake(out var queuedEntry))
                {
                    batch.Add(queuedEntry);
                }

                foreach (var group in batch.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream, Encoding.UTF8);
                        foreach (var entry in group)
                        {
                            writer.WriteLine(entry.Line);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Falha ao escrever lote de logs async: {ex.Message}");
                    }
                }

                foreach (var _ in batch)
                {
                    MarkLogEntryProcessed();
                }
            }
        }

        private static void MarkLogEntryProcessed()
        {
            if (Interlocked.Decrement(ref PendingLogWrites) == 0)
            {
                LogQueueDrained.Set();
            }
        }

        public static void FlushLogQueue(int timeoutMs = 2000)
        {
            LogQueueDrained.Wait(Math.Max(0, timeoutMs));
        }

        public bool GetCurrentResolution(out uint width, out uint height, out uint refreshRate)
        {
            width = 0;
            height = 0;
            refreshRate = 0;

            try
            {
                string? deviceName = GetPrimaryDisplayDeviceName();
                var devMode = new NativeMethods.DEVMODEW();
                devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));

                if (NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    width = devMode.dmPelsWidth;
                    height = devMode.dmPelsHeight;
                    refreshRate = devMode.dmDisplayFrequency;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Erro ao obter resolução atual: {ex.Message}");
            }

            return false;
        }

        private string? GetPrimaryDisplayDeviceName()
        {
            var displayDevice = new NativeMethods.DISPLAY_DEVICEW();
            displayDevice.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICEW));

            for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref displayDevice, 0); i++)
            {
                bool isPrimary = (displayDevice.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                bool isActive = (displayDevice.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0;
                if (isPrimary && isActive)
                {
                    return displayDevice.DeviceName;
                }

                displayDevice = new NativeMethods.DISPLAY_DEVICEW();
                displayDevice.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICEW));
            }

            return null;
        }

        public bool ChangeResolution(uint width, uint height, uint refreshRate)
        {
            lock (DisplaySettingsSyncRoot)
            {
                return ChangeResolutionCore(width, height, refreshRate);
            }
        }

        private bool ChangeResolutionCore(uint width, uint height, uint refreshRate)
        {
            LogDebug($"ChangeResolution: Solicitando {width}x{height}@{refreshRate}Hz");

            try
            {
                string? deviceName = GetPrimaryDisplayDeviceName();
                LogDebug($"ChangeResolution: Display alvo: {deviceName ?? "primário padrão"}");

                // Se já estiver na resolução alvo, ignorar
                if (GetCurrentResolution(out uint curW, out uint curH, out uint curR))
                {
                    if (curW == width && curH == height && curR == refreshRate)
                    {
                        LogDebug("ChangeResolution: Resolução atual já coincide. Ignorando.");
                        ApplyDpiScalingForResolution(height);
                        return true;
                    }
                }

                var devMode = new NativeMethods.DEVMODEW();
                devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));

                // Preferir um modo já enumerado pelo driver; se não aparecer, CDS_TEST ainda valida o modo especial.
                bool found = false;
                int modeIdx = 0;
                var candidateMode = new NativeMethods.DEVMODEW();
                candidateMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                while (NativeMethods.EnumDisplaySettings(deviceName, modeIdx, ref candidateMode))
                {
                    if (candidateMode.dmPelsWidth == width &&
                        candidateMode.dmPelsHeight == height &&
                        candidateMode.dmDisplayFrequency == refreshRate)
                    {
                        devMode = candidateMode;
                        found = true;
                        LogDebug($"ChangeResolution: Modo correspondente encontrado no índice {modeIdx}");
                        break;
                    }
                    modeIdx++;
                    candidateMode = new NativeMethods.DEVMODEW();
                    candidateMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                }

                if (!found)
                {
                    LogDebug("ChangeResolution: Modo não enumerado; testando como resolução especial.");
                    devMode = new NativeMethods.DEVMODEW();
                    devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                    if (!NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
                    {
                        LogDebug("ChangeResolution: ERRO - Não foi possível obter o modo atual para montar a resolução especial.");
                        return false;
                    }

                    devMode.dmPelsWidth = width;
                    devMode.dmPelsHeight = height;
                    devMode.dmDisplayFrequency = refreshRate;
                    if (devMode.dmBitsPerPel == 0)
                    {
                        devMode.dmBitsPerPel = 32;
                    }
                }

                devMode.dmFields = NativeMethods.DM_BITSPERPEL
                    | NativeMethods.DM_PELSWIDTH
                    | NativeMethods.DM_PELSHEIGHT
                    | NativeMethods.DM_DISPLAYFREQUENCY;

                int testResult = NativeMethods.ChangeDisplaySettingsEx(
                    deviceName,
                    ref devMode,
                    IntPtr.Zero,
                    NativeMethods.CDS_TEST,
                    IntPtr.Zero);
                if (testResult != NativeMethods.DISP_CHANGE_SUCCESSFUL)
                {
                    LogDebug($"ChangeResolution: CDS_TEST falhou com erro {testResult}");
                    return false;
                }

                int res = NativeMethods.ChangeDisplaySettingsEx(
                    deviceName,
                    ref devMode,
                    IntPtr.Zero,
                    NativeMethods.CDS_UPDATEREGISTRY,
                    IntPtr.Zero);
                if (res == NativeMethods.DISP_CHANGE_SUCCESSFUL)
                {
                    LogDebug("ChangeResolution: SUCESSO");
                    Status.CurrentResolution = $"{width}x{height}@{refreshRate}Hz";
                    OnStatusChanged();
                    ApplyDpiScalingForResolution(height);
                    return true;
                }
                else
                {
                    LogDebug($"ChangeResolution: FALHOU com erro {res}");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao alterar resolução: {ex}");
            }

            return false;
        }

        public void SetMouseSpeed(int speed, bool updateStatus = true)
        {
            LogDebug($"SetMouseSpeed: Configurando velocidade para {speed}/20");
            try
            {
                bool success = NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_SETMOUSESPEED,
                    0,
                    new IntPtr(speed),
                    NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE
                );

                if (success)
                {
                    Status.MouseSpeed = speed;
                    if (updateStatus) OnStatusChanged();
                }
                else
                {
                    LogDebug($"SetMouseSpeed: Erro ao chamar SystemParametersInfo. LastWin32Error: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao alterar velocidade do mouse: {ex.Message}");
            }
        }

        private static readonly Dictionary<int, byte[]> PollingMap = new()
        {
            { 125, new byte[] { 0x08, 0x4d } },
            { 250, new byte[] { 0x04, 0x51 } },
            { 500, new byte[] { 0x02, 0x53 } },
            { 1000, new byte[] { 0x01, 0x54 } }
        };

        private static readonly Dictionary<int, (byte mainByte, byte auxByte)> DpiMap = new()
        {
            { 800, (0x0f, 0x37) },
            { 1600, (0x1f, 0x17) },
            { 3200, (0x3f, 0xd7) },
            { 8000, (0x9f, 0x17) }
        };

        public static byte[] BuildPollingPayload(int hz)
        {
            if (!PollingMap.TryGetValue(hz, out var bytes))
            {
                throw new ArgumentException($"Polling rate {hz}Hz não suportado.");
            }
            byte[] payload = new byte[17];
            payload[0] = 0x08;
            payload[1] = 0x07;
            payload[2] = 0x00;
            payload[3] = 0x00;
            payload[4] = 0x00;
            payload[5] = 0x02;
            payload[6] = bytes[0];
            payload[7] = bytes[1];
            payload[16] = CalculateChecksum(payload);
            return payload;
        }

        public static byte[] BuildDpiPayload(int dpi)
        {
            if (!DpiMap.TryGetValue(dpi, out var tuple))
            {
                throw new ArgumentException($"DPI {dpi} não suportado.");
            }
            byte[] payload = new byte[17];
            payload[0] = 0x08;
            payload[1] = 0x07;
            payload[2] = 0x00;
            payload[3] = 0x00;
            payload[4] = 0x0c;
            payload[5] = 0x04;
            payload[6] = tuple.mainByte;
            payload[7] = tuple.mainByte;
            payload[8] = 0x00;
            payload[9] = tuple.auxByte;
            payload[16] = CalculateChecksum(payload);
            return payload;
        }

        private static byte CalculateChecksum(byte[] report)
        {
            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += report[i];
            }
            return (byte)((0x55 - sum) & 0xFF);
        }

        private static List<string> EnumerateHidDevicePaths()
        {
            var paths = new List<string>();
            Guid hidGuid;
            NativeMethods.HidD_GetHidGuid(out hidGuid);

            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            if (deviceInfoSet == IntPtr.Zero)
            {
                return paths;
            }

            try
            {
                var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

                uint index = 0;
                while (NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    uint requiredSize = 0;
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                    if (requiredSize > 0)
                    {
                        IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            int cbSize = IntPtr.Size == 8 ? 8 : 5;
                            Marshal.WriteInt32(detailDataBuffer, cbSize);

                            if (NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, ref requiredSize, IntPtr.Zero))
                            {
                                IntPtr pathPtr = new IntPtr(detailDataBuffer.ToInt64() + 4);
                                string? path = Marshal.PtrToStringUni(pathPtr);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    paths.Add(path);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                        }
                    }
                    index++;
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return paths;
        }

        private static Dictionary<string, HidDeviceInfo> EnumerateHidDeviceMetadata()
        {
            var devices = new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            Guid hidGuid;
            NativeMethods.HidD_GetHidGuid(out hidGuid);

            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            if (deviceInfoSet == IntPtr.Zero)
            {
                return devices;
            }

            try
            {
                var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

                uint index = 0;
                while (NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    uint requiredSize = 0;
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                    if (requiredSize > 0)
                    {
                        IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            int cbSize = IntPtr.Size == 8 ? 8 : 5;
                            Marshal.WriteInt32(detailDataBuffer, cbSize);

                            var devInfo = new NativeMethods.SP_DEVINFO_DATA();
                            devInfo.cbSize = (uint)Marshal.SizeOf(devInfo);

                            if (NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, ref requiredSize, ref devInfo))
                            {
                                IntPtr pathPtr = new IntPtr(detailDataBuffer.ToInt64() + 4);
                                string? path = Marshal.PtrToStringUni(pathPtr);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    var info = new HidDeviceInfo
                                    {
                                        DevicePath = path,
                                        FriendlyName = GetDeviceRegistryProperty(deviceInfoSet, ref devInfo, NativeMethods.SPDRP_FRIENDLYNAME),
                                        DeviceDescription = GetDeviceRegistryProperty(deviceInfoSet, ref devInfo, NativeMethods.SPDRP_DEVICEDESC),
                                        Manufacturer = GetDeviceRegistryProperty(deviceInfoSet, ref devInfo, NativeMethods.SPDRP_MFG)
                                    };
                                    devices[path] = info;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                        }
                    }

                    index++;
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return devices;
        }

        private static string GetDeviceRegistryProperty(IntPtr deviceInfoSet, ref NativeMethods.SP_DEVINFO_DATA devInfo, uint property)
        {
            byte[] buffer = new byte[512];
            if (!NativeMethods.SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref devInfo,
                    property,
                    out _,
                    buffer,
                    (uint)buffer.Length,
                    out uint requiredSize) || requiredSize == 0)
            {
                return string.Empty;
            }

            string value = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(requiredSize, buffer.Length));
            return value.TrimEnd('\0').Trim();
        }

        private static string GetHidString(IntPtr handle, Func<IntPtr, byte[], uint, bool> readString)
        {
            byte[] buffer = new byte[256];
            if (!readString(handle, buffer, (uint)buffer.Length))
            {
                return string.Empty;
            }

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        }

        private static bool TryExtractHidIds(string path, out string vidHex, out string pidHex, out string interfaceId)
        {
            vidHex = string.Empty;
            pidHex = string.Empty;
            interfaceId = string.Empty;

            var match = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&(?<mi>mi_[0-9a-f]{2}))?", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            vidHex = match.Groups[1].Value.ToUpperInvariant();
            pidHex = match.Groups[2].Value.ToUpperInvariant();
            if (match.Groups["mi"].Success)
            {
                interfaceId = match.Groups["mi"].Value.ToLowerInvariant();
            }

            return true;
        }

        public IReadOnlyList<HidDeviceInfo> EnumerateHidDevices()
        {
            var devices = new List<HidDeviceInfo>();
            var metadata = EnumerateHidDeviceMetadata();
            foreach (var path in EnumerateHidDevicePaths())
            {
                TryExtractHidIds(path, out string vidHex, out string pidHex, out string interfaceId);
                metadata.TryGetValue(path, out var existingInfo);
                var info = existingInfo ?? new HidDeviceInfo();
                info.DevicePath = path;
                info.VidHex = vidHex;
                info.PidHex = pidHex;
                info.InterfaceId = interfaceId;

                if (string.IsNullOrWhiteSpace(info.FriendlyName) && string.IsNullOrWhiteSpace(info.DeviceDescription))
                {
                    info.DeviceDescription = IsLikelyKeyboardDevice(info)
                        ? "Teclado HID"
                        : IsLikelyMouseDevice(info)
                            ? "Mouse HID"
                            : "Dispositivo HID";
                }

                IntPtr queryHandle = NativeMethods.CreateFile(
                    path,
                    0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (queryHandle != IntPtr.Zero && queryHandle != new IntPtr(-1))
                {
                    try
                    {
                        string productString = GetHidString(queryHandle, NativeMethods.HidD_GetProductString);
                        string manufacturerString = GetHidString(queryHandle, NativeMethods.HidD_GetManufacturerString);
                        if (!string.IsNullOrWhiteSpace(productString))
                        {
                            info.FriendlyName = productString;
                        }
                        if (!string.IsNullOrWhiteSpace(manufacturerString))
                        {
                            info.Manufacturer = manufacturerString;
                        }

                        if (NativeMethods.HidD_GetPreparsedData(queryHandle, out IntPtr preparsedData))
                        {
                            try
                            {
                                int status = NativeMethods.HidP_GetCaps(preparsedData, out NativeMethods.HIDP_CAPS caps);
                                if (status == 0x00110000)
                                {
                                    info.UsagePage = caps.UsagePage;
                                    info.Usage = caps.Usage;
                                    info.FeatureReportByteLength = caps.FeatureReportByteLength;
                                }
                            }
                            finally
                            {
                                NativeMethods.HidD_FreePreparsedData(preparsedData);
                            }
                        }
                    }
                    catch
                    {
                        // Keep the device selectable even if capabilities cannot be queried.
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(queryHandle);
                    }
                }

                devices.Add(info);
            }

            return devices
                .OrderByDescending(IsLikelyKeyboardDevice)
                .ThenByDescending(IsLikelyMouseDevice)
                .ThenBy(d => d.VidHex)
                .ThenBy(d => d.PidHex)
                .ThenBy(d => d.InterfaceId)
                .ToList();
        }

        private static bool IsLikelyKeyboardDevice(HidDeviceInfo device)
        {
            return device.VidHex.Equals("3151", StringComparison.OrdinalIgnoreCase) &&
                device.PidHex.Equals("4015", StringComparison.OrdinalIgnoreCase) &&
                device.InterfaceId.Equals("mi_02", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyMouseDevice(HidDeviceInfo device)
        {
            return device.VidHex.Equals("25A7", StringComparison.OrdinalIgnoreCase) &&
                device.PidHex.Equals("FA7C", StringComparison.OrdinalIgnoreCase);
        }

        private static string EncodePipeArg(string value)
        {
            if (string.IsNullOrEmpty(value)) return "EMPTY_ARG";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        public static string DecodePipeArg(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("EMPTY_ARG", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool RunNativeHidDriver(int polling, int dpi)
        {
            if (IsCurrentProcessElevated())
            {
                return RunNativeHidDriverDirect(polling, dpi, Config.Mouse.DevicePath, Config.Mouse.VidHex, Config.Mouse.PidHex, Config.Mouse.InterfaceId);
            }
            else
            {
                if (!EnsureAdminHelperRunning())
                {
                    LogDebug("RunNativeHidDriver: Helper administrativo indisponível; comando de mouse abortado.");
                    return false;
                }
                LogDebug($"RunNativeHidDriver: Enviando comando de mouse ao Helper: Polling={polling}, DPI={dpi}");
                string response = SendAdminCommand($"SET_MOUSE {polling} {dpi} {EncodePipeArg(Config.Mouse.DevicePath)} {EncodePipeArg(Config.Mouse.VidHex)} {EncodePipeArg(Config.Mouse.PidHex)} {EncodePipeArg(Config.Mouse.InterfaceId)}");
                LogDebug($"RunNativeHidDriver: Resposta do Helper: {response}");
                return response == "SUCCESS";
            }
        }

        private bool RunKeyboardHidDriver(int profile, int? brightness)
        {
            if (IsCurrentProcessElevated())
            {
                return RunKeyboardHidDriverDirect(profile, brightness ?? -1, Config.Keyboard.DevicePath, Config.Keyboard.VidHex, Config.Keyboard.PidHex, Config.Keyboard.InterfaceId);
            }
            else
            {
                if (!EnsureAdminHelperRunning())
                {
                    LogDebug("RunKeyboardHidDriver: Helper administrativo indisponível; comando de teclado abortado.");
                    return false;
                }
                LogDebug($"RunKeyboardHidDriver: Enviando comando de teclado ao Helper: Perfil={profile}, Brilho={(brightness.HasValue ? brightness.Value.ToString() : "sem alteração")}");
                string response = SendAdminCommand($"SET_KEYBOARD {profile} {brightness ?? -1} {EncodePipeArg(Config.Keyboard.DevicePath)} {EncodePipeArg(Config.Keyboard.VidHex)} {EncodePipeArg(Config.Keyboard.PidHex)} {EncodePipeArg(Config.Keyboard.InterfaceId)}");
                LogDebug($"RunKeyboardHidDriver: Resposta do Helper: {response}");
                return response == "SUCCESS";
            }
        }

        public bool RunNativeHidDriverDirect(
            int polling,
            int dpi,
            string? devicePath = null,
            string? vidHexOverride = null,
            string? pidHexOverride = null,
            string? interfaceIdOverride = null)
        {
            lock (MouseHidSyncRoot)
            {
                return RunNativeHidDriverDirectCore(polling, dpi, devicePath, vidHexOverride, pidHexOverride, interfaceIdOverride);
            }
        }

        private bool RunNativeHidDriverDirectCore(
            int polling,
            int dpi,
            string? devicePath,
            string? vidHexOverride,
            string? pidHexOverride,
            string? interfaceIdOverride)
        {
            try
            {
                // Finaliza drivers conflitando se existirem
                var processes = Process.GetProcessesByName("OemDrv");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill();
                        LogDebug("Conflito: Processo OemDrv.exe finalizado.");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Erro ao finalizar OemDrv: {ex.Message}");
                    }
                }
                if (processes.Length > 0)
                {
                    System.Threading.Thread.Sleep(500);
                }

                var paths = EnumerateHidDevicePaths();
                string selectedPath = devicePath ?? string.Empty;
                string configVid = string.IsNullOrWhiteSpace(vidHexOverride) ? Config.Mouse.VidHex : vidHexOverride;
                string configPid = string.IsNullOrWhiteSpace(pidHexOverride) ? Config.Mouse.PidHex : pidHexOverride;
                string interfaceId = string.IsNullOrWhiteSpace(interfaceIdOverride) ? Config.Mouse.InterfaceId : interfaceIdOverride;
                string vidHex = $"vid_{configVid}";
                string pidHex = $"pid_{configPid}";

                var candidatePaths = paths.Where(p =>
                    p.Contains(vidHex, StringComparison.OrdinalIgnoreCase) &&
                    p.Contains(pidHex, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(interfaceId) || p.Contains(interfaceId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                string? targetPath = null;
                LogDebug($"HID Mouse: Buscando por interface de controle... Encontradas {candidatePaths.Count} candidatas.");

                foreach (var path in candidatePaths)
                {
                    IntPtr queryHandle = NativeMethods.CreateFile(
                        path,
                        0,
                        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        NativeMethods.OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (queryHandle != IntPtr.Zero && queryHandle != new IntPtr(-1))
                    {
                        try
                        {
                            if (NativeMethods.HidD_GetPreparsedData(queryHandle, out IntPtr preparsedData))
                            {
                                try
                                {
                                    int status = NativeMethods.HidP_GetCaps(preparsedData, out NativeMethods.HIDP_CAPS caps);
                                    LogDebug($"HID Enumerate: Path={path} | Status={status:x8} | UsagePage={caps.UsagePage:x4} | Usage={caps.Usage:x4} | FeatureLen={caps.FeatureReportByteLength}");

                                    if (status == 0x00110000)
                                    {
                                        if (caps.UsagePage >= 0xFF00 && caps.FeatureReportByteLength >= 17)
                                        {
                                            targetPath = path;
                                            LogDebug($"HID Mouse: Selecionada interface fabricante ideal: UsagePage={caps.UsagePage:x4}, FeatureLen={caps.FeatureReportByteLength} em {path}");
                                            break;
                                        }
                                    }
                                }
                                finally
                                {
                                    NativeMethods.HidD_FreePreparsedData(preparsedData);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"HID Enumerate: Erro ao obter dados do path {path}: {ex.Message}");
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(queryHandle);
                        }
                    }
                    else
                    {
                        LogDebug($"HID Enumerate: Falha ao abrir handle de consulta para {path}. ErrorCode={Marshal.GetLastWin32Error()}");
                    }
                }

                if (string.IsNullOrEmpty(targetPath) && !string.IsNullOrWhiteSpace(selectedPath))
                {
                    targetPath = paths.FirstOrDefault(p => p.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
                }

                if (string.IsNullOrEmpty(targetPath))
                {
                    LogDebug("HID Mouse: Interface de fabricante >= 0xFF00 não encontrada por atributos. Usando fallback...");
                    targetPath = candidatePaths.FirstOrDefault(p => !p.Contains("col01", StringComparison.OrdinalIgnoreCase))
                        ?? candidatePaths.FirstOrDefault();
                }

                if (string.IsNullOrEmpty(targetPath))
                {
                    LogDebug($"Erro HID Mouse: Dispositivo {configVid}:{configPid} ({interfaceId}) não encontrado.");
                    return false;
                }

                LogDebug($"HID Mouse: Conectando a {targetPath}");
                IntPtr handle = NativeMethods.CreateFile(
                    targetPath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
                {
                    LogDebug($"Erro HID Mouse: Falha ao abrir o dispositivo. ErrorCode: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                try
                {
                    // Handshake 1
                    byte[] handshake = new byte[17];
                    handshake[0] = 0x08;
                    handshake[1] = 0x03;
                    handshake[16] = CalculateChecksum(handshake);

                    if (!NativeMethods.HidD_SetFeature(handle, handshake, (uint)handshake.Length))
                    {
                        LogDebug("Erro HID Mouse: Falha ao enviar Handshake 1.");
                        return false;
                    }
                    System.Threading.Thread.Sleep(10);

                    // Polling Payload
                    byte[] pollingPayload = BuildPollingPayload(polling);
                    if (!NativeMethods.HidD_SetFeature(handle, pollingPayload, (uint)pollingPayload.Length))
                    {
                        LogDebug($"Erro HID Mouse: Falha ao enviar Polling Payload ({polling}Hz).");
                        return false;
                    }
                    System.Threading.Thread.Sleep(20);

                    // Commit 1
                    byte[] commit = new byte[17];
                    commit[0] = 0x08;
                    commit[1] = 0x04;
                    commit[16] = CalculateChecksum(commit);

                    if (!NativeMethods.HidD_SetFeature(handle, commit, (uint)commit.Length))
                    {
                        LogDebug("Erro HID Mouse: Falha ao enviar Commit 1.");
                        return false;
                    }
                    System.Threading.Thread.Sleep(50); // sleep between configs

                    // Handshake 2
                    if (!NativeMethods.HidD_SetFeature(handle, handshake, (uint)handshake.Length))
                    {
                        LogDebug("Erro HID Mouse: Falha ao enviar Handshake 2.");
                        return false;
                    }
                    System.Threading.Thread.Sleep(10);

                    // DPI Payload
                    byte[] dpiPayload = BuildDpiPayload(dpi);
                    if (!NativeMethods.HidD_SetFeature(handle, dpiPayload, (uint)dpiPayload.Length))
                    {
                        LogDebug($"Erro HID Mouse: Falha ao enviar DPI Payload ({dpi} DPI).");
                        return false;
                    }
                    System.Threading.Thread.Sleep(20);

                    // Commit 2
                    if (!NativeMethods.HidD_SetFeature(handle, commit, (uint)commit.Length))
                    {
                        LogDebug("Erro HID Mouse: Falha ao enviar Commit 2.");
                        return false;
                    }
                    System.Threading.Thread.Sleep(10);

                    LogDebug($"HID Mouse: Sucesso ao configurar Polling={polling}Hz e DPI={dpi} DPI.");
                    return true;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception HID Mouse: {ex.Message}");
                return false;
            }
        }

        public bool RunKeyboardHidDriverDirect(
            int profile,
            int brightness,
            string? devicePath = null,
            string? vidHexOverride = null,
            string? pidHexOverride = null,
            string? interfaceIdOverride = null)
        {
            lock (KeyboardHidSyncRoot)
            {
                return RunKeyboardHidDriverDirectCore(profile, brightness, devicePath, vidHexOverride, pidHexOverride, interfaceIdOverride);
            }
        }

        private bool RunKeyboardHidDriverDirectCore(
            int profile,
            int brightness,
            string? devicePath,
            string? vidHexOverride,
            string? pidHexOverride,
            string? interfaceIdOverride)
        {
            try
            {
                bool shouldApplyBrightness = brightness >= 0;
                int clampedBrightness = Math.Clamp(brightness, 0, 4);
                var paths = EnumerateHidDevicePaths();
                string selectedPath = devicePath ?? string.Empty;
                string configVid = string.IsNullOrWhiteSpace(vidHexOverride) ? Config.Keyboard.VidHex : vidHexOverride;
                string configPid = string.IsNullOrWhiteSpace(pidHexOverride) ? Config.Keyboard.PidHex : pidHexOverride;
                string interfaceId = string.IsNullOrWhiteSpace(interfaceIdOverride) ? Config.Keyboard.InterfaceId : interfaceIdOverride;
                string vidHex = $"vid_{configVid}";
                string pidHex = $"pid_{configPid}";

                var candidatePaths = paths.Where(p =>
                    p.Contains(vidHex, StringComparison.OrdinalIgnoreCase) &&
                    p.Contains(pidHex, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(interfaceId) || p.Contains(interfaceId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                string? targetPath = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(selectedPath) && p.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                    ?? candidatePaths.FirstOrDefault();
                if (string.IsNullOrEmpty(targetPath))
                {
                    LogDebug($"Erro HID Keyboard: Dispositivo {configVid}:{configPid} ({interfaceId}) não encontrado.");
                    return false;
                }

                LogDebug($"HID Keyboard: Conectando a {targetPath}");
                IntPtr handle = NativeMethods.CreateFile(
                    targetPath,
                    0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
                {
                    LogDebug($"Erro HID Keyboard: Falha ao abrir o dispositivo. ErrorCode: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                try
                {
                    byte[] profileReport = BuildKeyboardProfileReport(profile);
                    bool profileOk = NativeMethods.HidD_SetFeature(handle, profileReport, (uint)profileReport.Length);
                    int profileErr = Marshal.GetLastWin32Error();
                    string profileState = string.Join(" ", profileReport.Take(9).Select(b => b.ToString("X2")));
                    LogDebug($"HID Keyboard: SetFeature perfil {profile} -> ok={profileOk} err={profileErr} data=[{profileState}]");

                    if (!profileOk)
                    {
                        LogDebug($"Erro HID Keyboard: SetFeature falhou para perfil {profile}. ErrorCode={profileErr}");
                        return false;
                    }

                    if (shouldApplyBrightness)
                    {
                        System.Threading.Thread.Sleep(20);

                        byte[] brightnessReport = BuildKeyboardBrightnessReport(clampedBrightness);
                        bool brightnessOk = NativeMethods.HidD_SetFeature(handle, brightnessReport, (uint)brightnessReport.Length);
                        int brightnessErr = Marshal.GetLastWin32Error();
                        string brightnessState = string.Join(" ", brightnessReport.Take(10).Select(b => b.ToString("X2")));
                        LogDebug($"HID Keyboard: SetFeature brilho {clampedBrightness} -> ok={brightnessOk} err={brightnessErr} data=[{brightnessState}]");

                        if (!brightnessOk)
                        {
                            LogDebug($"Erro HID Keyboard: SetFeature falhou para brilho {clampedBrightness}. ErrorCode={brightnessErr}");
                            return false;
                        }
                    }

                    LogDebug($"HID Keyboard: Sucesso ao configurar Perfil {profile}{(shouldApplyBrightness ? $" e Brilho {clampedBrightness}" : string.Empty)}.");
                    return true;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception HID Keyboard: {ex.Message}");
                return false;
            }
        }

        public static byte[] BuildKeyboardBrightnessReport(int brightness)
        {
            int intensity = Math.Clamp(brightness, 0, 4);
            byte[] report = new byte[65];
            byte checksumByte = (byte)(0xAD - intensity);
            byte[] payload = new byte[] { 0x07, 0x01, 0x04, (byte)intensity, 0x07, 0x00, 0x0B, 0x34, checksumByte };
            Array.Copy(payload, 0, report, 1, payload.Length);
            return report;
        }

        public static byte[] BuildKeyboardProfileReport(int profile)
        {
            byte[] report = new byte[65];
            byte[] payload = profile == 1
                ? new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFA }
                : new byte[] { 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF6 };
            Array.Copy(payload, 0, report, 1, payload.Length);
            return report;
        }

        public string ScanKeyboardReportIds()
        {
            var hits = new List<string>();
            try
            {
                var paths = EnumerateHidDevicePaths();
                string? targetPath = paths.FirstOrDefault(p =>
                        !string.IsNullOrWhiteSpace(Config.Keyboard.DevicePath) &&
                        p.Equals(Config.Keyboard.DevicePath, StringComparison.OrdinalIgnoreCase))
                    ?? paths.FirstOrDefault(p =>
                        p.Contains($"vid_{Config.Keyboard.VidHex}", StringComparison.OrdinalIgnoreCase) &&
                        p.Contains($"pid_{Config.Keyboard.PidHex}", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(Config.Keyboard.InterfaceId) || p.Contains(Config.Keyboard.InterfaceId, StringComparison.OrdinalIgnoreCase)));

                if (string.IsNullOrEmpty(targetPath))
                    return "ERROR:NotFound";

                IntPtr handle = NativeMethods.CreateFile(
                    targetPath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
                    return $"ERROR:OpenFailed:{Marshal.GetLastWin32Error()}";

                try
                {
                    for (int id = 0; id <= 0xFF; id++)
                    {
                        byte[] buf = new byte[65];
                        buf[0] = (byte)id;
                        bool ok = NativeMethods.HidD_GetFeature(handle, buf, (uint)buf.Length);
                        if (ok)
                        {
                            string hex = string.Join("", buf.Take(12).Select(b => b.ToString("X2")));
                            bool hasData = buf.Skip(1).Any(b => b != 0x00);
                            string entry = $"0x{id:X2}:{hex}:{(hasData ? "D" : "Z")}";
                            hits.Add(entry);
                            LogDebug($"SCAN_KEYBOARD: ID=0x{id:X2} hasData={hasData} hex=[{hex}]");
                        }
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                return $"ERROR:{ex.Message}";
            }

            return hits.Count > 0 ? string.Join("|", hits) : "NO_REPORTS_FOUND";
        }

        public void SetDpiState(string state)
        {
            lock (PeripheralControlSyncRoot)
            {
                if (Status.DpiState == state) return;

                LogDebug($"SetDpiState: Alterando para '{state}'");
                int pollingVal = Status.PollingState == "high" ? Config.Mouse.PollingHigh : Config.Mouse.PollingLow;
                int dpiVal = state == "high" ? Config.Mouse.DpiHigh : Config.Mouse.DpiLow;

                if (RunNativeHidDriver(pollingVal, dpiVal))
                {
                    Status.DpiState = state;
                    if (state == "high")
                    {
                        SetMouseSpeed(Config.Mouse.SlowSpeed, false);
                    }
                    else
                    {
                        SetMouseSpeed(Config.Mouse.HighSpeed, false);
                    }
                    OnStatusChanged();
                    ShowNotification("TutzApp - Mouse", $"DPI: {dpiVal} | Polling: {pollingVal}Hz | Vel: {Status.MouseSpeed}/20");
                }
            }
        }

        public void SetPollingState(string state)
        {
            lock (PeripheralControlSyncRoot)
            {
                if (Status.PollingState == state) return;

                LogDebug($"SetPollingState: Alterando para '{state}'");
                int pollingVal = state == "high" ? Config.Mouse.PollingHigh : Config.Mouse.PollingLow;
                int dpiVal = Status.DpiState == "high" ? Config.Mouse.DpiHigh : Config.Mouse.DpiLow;

                if (RunNativeHidDriver(pollingVal, dpiVal))
                {
                    Status.PollingState = state;
                    OnStatusChanged();
                    ShowNotification("TutzApp - Mouse", $"DPI: {dpiVal} | Polling: {pollingVal}Hz | Vel: {Status.MouseSpeed}/20");
                }
            }
        }

        public bool ApplyMouseSettings(int polling, int dpi, int speed)
        {
            lock (PeripheralControlSyncRoot)
            {
                LogDebug($"ApplyMouseSettings: Polling={polling}Hz, DPI={dpi}, Speed={speed}/20");
                if (RunNativeHidDriver(polling, dpi))
                {
                    Status.MouseSpeed = speed;
                    Status.DpiState = (dpi == Config.Mouse.DpiHigh) ? "high" : "low";
                    Status.PollingState = (polling == Config.Mouse.PollingHigh) ? "high" : "low";
                    SetMouseSpeed(speed, false);
                    OnStatusChanged();
                    ShowNotification("TutzApp - Mouse", $"Configuração Manual: DPI: {dpi} | Polling: {polling}Hz | Vel: {speed}/20");
                    return true;
                }
                return false;
            }
        }

        public void ToggleKeyboardProfile(int? profile = null)
        {
            int targetProfile = profile ?? (Status.KeyboardProfile == 1 ? 2 : 1);
            LogDebug($"ToggleKeyboardProfile: Enfileirando alternância somente do perfil para {targetProfile}");

            // Dispara a chamada assincronamente em background para não travar a UI/Hooks.
            _ = Task.Run(() =>
            {
                try
                {
                    lock (PeripheralControlSyncRoot)
                    {
                        bool success = RunKeyboardHidDriver(targetProfile, null);
                        LogDebug($"ToggleKeyboardProfile: Resultado do driver HID para perfil {targetProfile}: {success}");
                        if (success)
                        {
                            Status.KeyboardProfile = targetProfile;
                            OnStatusChanged();
                            ShowNotification("TutzApp - Teclado", $"Perfil {targetProfile}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"ToggleKeyboardProfile: Exceção no driver HID: {ex}");
                }
            });
        }

        public bool ApplyKeyboardSettings(int profile, int brightness)
        {
            lock (PeripheralControlSyncRoot)
            {
                int targetProfile = profile == 2 ? 2 : 1;
                int targetBrightness = Math.Clamp(brightness, 0, 4);
                LogDebug($"ApplyKeyboardSettings: Perfil={targetProfile}, Brilho={targetBrightness}");

                if (RunKeyboardHidDriver(targetProfile, targetBrightness))
                {
                    Status.KeyboardProfile = targetProfile;
                    Config.Keyboard.ManualBrightness = targetBrightness;
                    OnStatusChanged();
                    ShowNotification("TutzApp - Teclado", $"Perfil {targetProfile} | Brilho {targetBrightness}");
                    return true;
                }

                return false;
            }
        }

        public bool EnsureNormalProfileIfMouseSpeedIsNotNormal(string reason)
        {
            long transitionId = Interlocked.Increment(ref ProfileTransitionSequence);
            var stopwatch = Stopwatch.StartNew();

            lock (PeripheralControlSyncRoot)
            {
                int? currentSpeed = GetCurrentMouseSpeed();
                if (currentSpeed.HasValue)
                {
                    Status.MouseSpeed = currentSpeed.Value;
                    if (currentSpeed.Value == Config.Mouse.HighSpeed)
                    {
                        Status.PollingState = "low";
                        Status.DpiState = "low";
                        SetStatusMessage("Perfil Normal Ativo");
                        LogDebug($"EnsureNormalProfile[{transitionId}]: {reason}: velocidade Windows já está no perfil normal ({currentSpeed.Value}/20). Nenhuma alteração de DPI/polling/teclado aplicada.");
                        OnStatusChanged();
                        return false;
                    }

                    LogDebug($"EnsureNormalProfile[{transitionId}]: {reason}: velocidade Windows {currentSpeed.Value}/20 difere do perfil normal ({Config.Mouse.HighSpeed}/20). Aplicando perfil normal completo.");
                }
                else
                {
                    LogDebug($"EnsureNormalProfile[{transitionId}]: {reason}: não foi possível ler velocidade Windows. Aplicando perfil normal por segurança.");
                }

                int pollingVal = Config.Mouse.PollingLow;
                int dpiVal = Config.Mouse.DpiLow;
                int speedVal = Config.Mouse.HighSpeed;
                int kbProfile = Config.Keyboard.AutoNormalProfile;
                int kbBrightness = Config.Keyboard.Profile2Brightness;

                bool mouseOk = false;
                bool kbOk = false;

                try
                {
                    mouseOk = RunNativeHidDriver(pollingVal, dpiVal);
                    if (mouseOk)
                    {
                        Status.PollingState = "low";
                        Status.DpiState = "low";
                    }

                    // O Windows mouse speed é o fator de verdade e deve ser normalizado mesmo se o HID falhar.
                    SetMouseSpeed(speedVal, false);
                }
                catch (Exception ex)
                {
                    LogDebug($"EnsureNormalProfile[{transitionId}]: erro ao aplicar mouse normal: {ex}");
                }

                try
                {
                    kbOk = RunKeyboardHidDriver(kbProfile, kbBrightness);
                    if (kbOk)
                    {
                        Status.KeyboardProfile = kbProfile;
                        Config.Keyboard.ManualBrightness = kbBrightness;
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"EnsureNormalProfile[{transitionId}]: erro ao aplicar teclado normal: {ex}");
                }

                SetStatusMessage("Perfil Normal Ativo");
                OnStatusChanged();
                LogDebug($"EnsureNormalProfile[{transitionId}]: concluído em {stopwatch.ElapsedMilliseconds}ms. Mouse={mouseOk}; Teclado={kbOk}.");
                return true;
            }
        }

        public void ApplyAutomationProfile(bool isGame)
        {
            long transitionId = Interlocked.Increment(ref ProfileTransitionSequence);
            var stopwatch = Stopwatch.StartNew();
            string targetProfileName = isGame ? "Jogo" : "Normal";
            LogDebug($"ApplyAutomationProfile[{transitionId}]: Solicitação recebida para perfil {targetProfileName}.");

            lock (PeripheralControlSyncRoot)
            {
                LogDebug($"ApplyAutomationProfile[{transitionId}]: Execução iniciada após {stopwatch.ElapsedMilliseconds}ms aguardando exclusividade.");
                SetStatusMessage(isGame ? "Perfil de Jogo Ativo" : "Perfil Normal Ativo");

                int pollingVal = isGame ? Config.Mouse.PollingHigh : Config.Mouse.PollingLow;
                int dpiVal = isGame ? Config.Mouse.DpiHigh : Config.Mouse.DpiLow;
                int speedVal = isGame ? Config.Mouse.SlowSpeed : Config.Mouse.HighSpeed;

                int kbProfile = isGame ? Config.Keyboard.AutoGameProfile : Config.Keyboard.AutoNormalProfile;
                int kbBrightness = isGame ? Config.Keyboard.Profile1Brightness : Config.Keyboard.Profile2Brightness;

                bool mouseOk = false;
                bool kbOk = false;

                try
                {
                    mouseOk = RunNativeHidDriver(pollingVal, dpiVal);
                    if (mouseOk)
                    {
                        Status.PollingState = isGame ? "high" : "low";
                        Status.DpiState = isGame ? "high" : "low";
                        SetMouseSpeed(speedVal, false);
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"ApplyAutomationProfile[{transitionId}]: Erro ao aplicar mouse: {ex}");
                }

                try
                {
                    kbOk = RunKeyboardHidDriver(kbProfile, kbBrightness);
                    if (kbOk)
                    {
                        Status.KeyboardProfile = kbProfile;
                        Config.Keyboard.ManualBrightness = kbBrightness;
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"ApplyAutomationProfile[{transitionId}]: Erro ao aplicar teclado: {ex}");
                }

                OnStatusChanged();

                string title = "TutzApp - Automação";
                string mouseMsg = mouseOk
                    ? $"Mouse: DPI {dpiVal} | Polling {pollingVal}Hz | Vel {speedVal}/20"
                    : "Mouse: Falha ao aplicar";
                string kbMsg = kbOk
                    ? $"Teclado: Perfil {kbProfile} | Brilho {kbBrightness}"
                    : "Teclado: Falha ao aplicar";

                string profileName = isGame ? "Perfil de Jogo Ativo" : "Perfil Normal Ativo";
                ShowNotification(title, $"{profileName}\n{mouseMsg}\n{kbMsg}");
                LogDebug($"ApplyAutomationProfile[{transitionId}]: Concluído em {stopwatch.ElapsedMilliseconds}ms. Mouse={mouseOk}; Teclado={kbOk}.");
            }
        }

        public bool SaveAppConfig(AppConfig newConfig)
        {
            lock (AppSettingsWriteSyncRoot)
            {
                try
                {
                    Config.DebugEnabled = newConfig.DebugEnabled;
                    Config.DebugLogPath = GetDebugLogPath();
                    Config.BatDir = newConfig.BatDir;
                    Config.TwinkleTrayPath = newConfig.TwinkleTrayPath;

                    Config.Mouse.HighSpeed = newConfig.Mouse.HighSpeed;
                    Config.Mouse.SlowSpeed = newConfig.Mouse.SlowSpeed;
                    Config.Mouse.DpiHigh = newConfig.Mouse.DpiHigh;
                    Config.Mouse.DpiLow = newConfig.Mouse.DpiLow;
                    Config.Mouse.PollingHigh = newConfig.Mouse.PollingHigh;
                    Config.Mouse.PollingLow = newConfig.Mouse.PollingLow;
                    Config.Mouse.VidHex = newConfig.Mouse.VidHex;
                    Config.Mouse.PidHex = newConfig.Mouse.PidHex;
                    Config.Mouse.InterfaceId = newConfig.Mouse.InterfaceId;
                    Config.Mouse.DevicePath = newConfig.Mouse.DevicePath;

                    Config.Keyboard.VidHex = newConfig.Keyboard.VidHex;
                    Config.Keyboard.PidHex = newConfig.Keyboard.PidHex;
                    Config.Keyboard.InterfaceId = newConfig.Keyboard.InterfaceId;
                    Config.Keyboard.DevicePath = newConfig.Keyboard.DevicePath;
                    Config.Keyboard.Profile1Brightness = Math.Clamp(newConfig.Keyboard.Profile1Brightness, 0, 4);
                    Config.Keyboard.Profile2Brightness = Math.Clamp(newConfig.Keyboard.Profile2Brightness, 0, 4);
                    Config.Keyboard.ManualBrightness = Math.Clamp(newConfig.Keyboard.ManualBrightness, 0, 4);
                    Config.Keyboard.AutoGameProfile = newConfig.Keyboard.AutoGameProfile;
                    Config.Keyboard.AutoNormalProfile = newConfig.Keyboard.AutoNormalProfile;

                    Config.Python.DriverDir = newConfig.Python.DriverDir;
                    Config.Python.DriverPath = newConfig.Python.DriverPath;
                    Config.Python.ExePath = newConfig.Python.ExePath;

                    Config.Monitoring.GamesCheckIntervalMs = newConfig.Monitoring.GamesCheckIntervalMs;
                    Config.Monitoring.GameProfileEnterDelayMs = newConfig.Monitoring.GameProfileEnterDelayMs;
                    Config.Monitoring.GameProfileExitDelayMs = newConfig.Monitoring.GameProfileExitDelayMs;
                    Config.Monitoring.GamepadPollIntervalMs = newConfig.Monitoring.GamepadPollIntervalMs;
                    Config.Monitoring.DoublePressIntervalMs = newConfig.Monitoring.DoublePressIntervalMs;
                    Config.Monitoring.AutoGameProfileSwitch = newConfig.Monitoring.AutoGameProfileSwitch;
                    Config.Monitoring.AutoResolutionSwitch = newConfig.Monitoring.AutoResolutionSwitch;
                    Config.Monitoring.Games = newConfig.Monitoring.Games;

                    Config.Vibrance.Enabled = newConfig.Vibrance.Enabled;
                    Config.Vibrance.FocusPollIntervalMs = newConfig.Vibrance.FocusPollIntervalMs;
                    Config.Vibrance.DefaultDriverLevel = newConfig.Vibrance.DefaultDriverLevel;
                    Config.Vibrance.AppRules = newConfig.Vibrance.AppRules;
                    Config.ForceKill.ProtectedProcesses = newConfig.ForceKill.ProtectedProcesses;
                    Config.Gamepad = newConfig.Gamepad;

                    Config.Resolutions = newConfig.Resolutions;
                    Config.GamepadShortcuts = newConfig.GamepadShortcuts;

                    bool oldPlaynite = Config.GameConsole?.PlayniteBridgeEnabled ?? false;

                    Config.GameConsole = newConfig.GameConsole ?? new GameConsoleSettings();

                    bool isAdminHelper = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--admin-helper", StringComparison.OrdinalIgnoreCase));
                    if (!isAdminHelper && Config.GameConsole.PlayniteBridgeEnabled != oldPlaynite)
                    {
                        if (Config.GameConsole.PlayniteBridgeEnabled)
                        {
                            PlayniteBridge ??= new PlayniteBridge(LaunchFocusAssist ?? new LaunchFocusAssist(LogDebug), LogDebug);
                            PlayniteBridge.Start();
                        }
                        else
                        {
                            PlayniteBridge?.Dispose();
                            PlayniteBridge = null;
                        }
                    }

                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    string json = System.Text.Json.JsonSerializer.Serialize(Config, options);
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                    WriteAppSettingsAtomically(path, json);

                    if (!isAdminHelper)
                    {
                        int? currentMapping = ControllerVkMapping.GetCurrentState();
                        bool wantsSuppression = Config.GameConsole.SuppressWindowsControllerVkMapping;
                        bool hasOriginalSnapshot = Config.GameConsole.WasVkMappingOriginallyMissing != null;

                        if (wantsSuppression && currentMapping != 0)
                        {
                            bool applied = SuppressControllerVkMapping(true);
                            LogDebug($"SaveAppConfig: ControllerToVKMapping suppression applied={applied}.");
                        }
                        else if (!wantsSuppression && currentMapping == 0 && hasOriginalSnapshot)
                        {
                            bool restored = RestoreControllerVkMapping();
                            LogDebug($"SaveAppConfig: ControllerToVKMapping restoration applied={restored}.");
                        }

                        string reloadResponse = SendAdminCommand("RELOAD_GAME_CONSOLE_SETTINGS");
                        LogDebug($"SaveAppConfig: helper GameConsole settings reload response: {reloadResponse}");
                    }

                    LogDebug("SaveAppConfig: Configurações salvas e aplicadas.");
                    return true;
                }
                catch (Exception ex)
                {
                    LogDebug($"SaveAppConfig: Erro ao salvar configurações: {ex.Message}");
                    return false;
                }
            }
        }

        public bool SuppressControllerVkMapping(bool suppress)
        {
            EnsureAdminHelperRunning();
            string cmd = suppress ? "SET_CONTROLLER_VK_MAPPING 0" : "RESTORE_CONTROLLER_VK_MAPPING";
            string response = SendAdminCommand(cmd);
            LogDebug($"SystemControlService: SuppressControllerVkMapping({suppress}) -> response: {response}");
            return response.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }

        public bool RestoreControllerVkMapping()
        {
            EnsureAdminHelperRunning();
            string response = SendAdminCommand("RESTORE_CONTROLLER_VK_MAPPING");
            LogDebug($"SystemControlService: RestoreControllerVkMapping() -> response: {response}");
            return response.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }

        public int? GetControllerVkMappingState()
        {
            return ControllerVkMapping.GetCurrentState();
        }


        private static void WriteAppSettingsAtomically(string path, string json)
        {
            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Não foi possível determinar o diretório do appsettings.json.");
            Directory.CreateDirectory(directory);

            string temporaryPath = Path.Combine(
                directory,
                $".appsettings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, utf8WithoutBom, bufferSize: 64 * 1024, leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(temporaryPath, path, overwrite: true);
                    }
                    catch (IOException)
                    {
                        File.Move(temporaryPath, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // A configuração principal já foi preservada; lixo temporário é best effort.
                }
            }
        }

        public void PowerOffMonitor()
        {
            LogDebug("PowerOffMonitor: Desligando monitor...");
            try
            {
                NativeMethods.PostMessage(
                    new IntPtr(NativeMethods.HWND_BROADCAST),
                    NativeMethods.WM_SYSCOMMAND,
                    new IntPtr(NativeMethods.SC_MONITORPOWER),
                    new IntPtr(NativeMethods.MONITOR_OFF)
                );
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao desligar monitor: {ex.Message}");
            }
        }

        public bool ToggleDisplayTarget()
        {
            lock (DisplaySettingsSyncRoot)
            {
                return ToggleDisplayTargetCore();
            }
        }

        public void AdjustBrightness(int offset)
        {
            LogDebug($"AdjustBrightness: Ajustando brilho por offset {offset}");
            string? twinkleTrayExecutable = ResolveTwinkleTrayExecutable();

            try
            {
                string sign = offset >= 0 ? "+" : "";

                var psi = new ProcessStartInfo
                {
                    FileName = twinkleTrayExecutable,
                    CreateNoWindow = true,
                    UseShellExecute = IsTwinkleTrayAlias(twinkleTrayExecutable)
                };
                psi.ArgumentList.Add("--All");
                psi.ArgumentList.Add($"--Offset={sign}{offset}");
                psi.ArgumentList.Add("--Overlay");

                LogDebug($"AdjustBrightness: Executando Twinkle Tray via '{twinkleTrayExecutable}' para todos os monitores com offset {sign}{offset}.");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao ajustar brilho via Twinkle Tray: {ex.Message}");
            }
        }

        private string ResolveTwinkleTrayExecutable()
        {
            foreach (string candidate in EnumerateTwinkleTrayExecutableCandidates())
            {
                if (IsTwinkleTrayAlias(candidate) || File.Exists(candidate))
                {
                    return candidate;
                }
            }

            LogDebug($"AdjustBrightness: Twinkle Tray não encontrado. Caminho configurado: '{Config.TwinkleTrayPath}'. Tentando alias 'Twinkle-Tray.exe'.");
            return "Twinkle-Tray.exe";
        }

        private IEnumerable<string> EnumerateTwinkleTrayExecutableCandidates()
        {
            if (!string.IsNullOrWhiteSpace(Config.TwinkleTrayPath))
            {
                yield return Config.TwinkleTrayPath;
            }

            string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Programs", "twinkle-tray", "Twinkle Tray.exe");
                yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "Twinkle-Tray.exe");
            }

            string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Twinkle Tray", "Twinkle Tray.exe");
            }

            string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Twinkle Tray", "Twinkle Tray.exe");
            }

            yield return "Twinkle-Tray.exe";
            yield return "Twinkle Tray.exe";
        }

        private static bool IsTwinkleTrayAlias(string executable)
        {
            return string.Equals(executable, "Twinkle-Tray.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "Twinkle Tray.exe", StringComparison.OrdinalIgnoreCase);
        }

        public bool ForceKillForegroundApp()
        {
            LogDebug("ForceKillForegroundApp: solicitação recebida.");
            string protectedProcessesText = GetForceKillProtectedProcessesTextFromConfig();

            if (IsCurrentProcessElevated())
            {
                bool directSuccess = ForceKillForegroundAppDirect();
                SetStatusMessage(directSuccess ? "Janela ativa encerrada." : "Force kill não executado.");
                return directSuccess;
            }

            if (!EnsureAdminHelperRunning())
            {
                LogDebug("ForceKillForegroundApp: helper administrativo indisponível.");
                SetStatusMessage("Helper administrativo indisponível.");
                return false;
            }

            string response = SendAdminCommand($"FORCE_KILL_FOREGROUND {EncodePipeArg(protectedProcessesText)}");
            bool success = response.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);
            LogDebug($"ForceKillForegroundApp: resposta do helper: {response}");
            SetStatusMessage(success ? "Janela ativa encerrada." : "Force kill não executado.");
            return success;
        }

        public bool ForceKillForegroundAppDirect(string? protectedProcessesText = null)
        {
            long now = Environment.TickCount64;
            long lastStarted = Interlocked.Read(ref LastForceKillStartedAt);
            if (lastStarted != 0 && now - lastStarted < ForceKillCooldownMs)
            {
                LogDebug($"ForceKillForegroundAppDirect: execução ignorada por debounce ({now - lastStarted}ms desde a última tentativa).");
                return true;
            }

            if (Interlocked.Exchange(ref ForceKillInProgress, 1) != 0)
            {
                LogDebug("ForceKillForegroundAppDirect: execução ignorada porque outra finalização já está em andamento.");
                return false;
            }

            Interlocked.Exchange(ref LastForceKillStartedAt, now);

            IntPtr debugPrivilegeToken = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;

            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    LogDebug("ForceKillForegroundAppDirect: nenhuma janela em foco encontrada.");
                    return false;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                {
                    LogDebug("ForceKillForegroundAppDirect: não foi possível obter o PID da janela em foco.");
                    return false;
                }

                string processName = GetProcessExecutableName(pid);
                var protectedProcesses = ParseForceKillProtectedProcesses(
                    string.IsNullOrWhiteSpace(protectedProcessesText)
                        ? GetForceKillProtectedProcessesTextFromDiskOrConfig()
                        : protectedProcessesText);

                if (!string.IsNullOrWhiteSpace(processName) && IsForceKillProtectedProcess(processName, protectedProcesses))
                {
                    LogDebug($"ForceKillForegroundAppDirect: '{processName}' está na whitelist de proteção. PID={pid} não será encerrado.");
                    return false;
                }

                debugPrivilegeToken = EnableDebugPrivilegeForForceKill();

                processHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
                if (processHandle == IntPtr.Zero)
                {
                    LogDebug($"ForceKillForegroundAppDirect: OpenProcess(PROCESS_TERMINATE) falhou. PID={pid}, Processo='{processName}', Win32Error={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                bool terminated = NativeMethods.TerminateProcess(processHandle, 1);
                int terminateError = Marshal.GetLastWin32Error();
                LogDebug($"ForceKillForegroundAppDirect: TerminateProcess PID={pid}, Processo='{processName}', sucesso={terminated}, Win32Error={terminateError}.");
                return terminated;
            }
            catch (Exception ex)
            {
                LogDebug($"ForceKillForegroundAppDirect: exceção ao finalizar janela ativa: {ex}");
                return false;
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processHandle);
                }

                DisableDebugPrivilegeForForceKill(debugPrivilegeToken);
                Interlocked.Exchange(ref ForceKillInProgress, 0);
            }
        }

        private string GetForceKillProtectedProcessesTextFromConfig()
        {
            var protectedProcesses = Config.ForceKill?.ProtectedProcesses ?? new List<string>();
            return string.Join(",", protectedProcesses);
        }

        private string GetForceKillProtectedProcessesTextFromDiskOrConfig()
        {
            string appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            try
            {
                if (File.Exists(appSettingsPath))
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appSettingsPath));
                    if (document.RootElement.TryGetProperty("ForceKill", out var forceKillElement) &&
                        forceKillElement.TryGetProperty("ProtectedProcesses", out var protectedElement) &&
                        protectedElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var names = protectedElement
                            .EnumerateArray()
                            .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(item => item.GetString())
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Select(name => name!.Trim())
                            .ToList();

                        if (names.Count > 0)
                        {
                            return string.Join(",", names);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"ForceKillForegroundAppDirect: falha ao ler whitelist de proteção do appsettings.json: {ex.Message}");
            }

            return GetForceKillProtectedProcessesTextFromConfig();
        }

        private static HashSet<string> ParseForceKillProtectedProcesses(string? protectedProcessesText)
        {
            var protectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            char[] separators = { ',', ';', '\r', '\n', '\t' };

            foreach (string rawPart in (protectedProcessesText ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string processName = Path.GetFileName(rawPart.Trim());
                if (!string.IsNullOrWhiteSpace(processName))
                {
                    protectedProcesses.Add(processName);
                }
            }

            if (protectedProcesses.Count == 0)
            {
                protectedProcesses.Add("explorer.exe");
            }

            return protectedProcesses;
        }

        private static bool IsForceKillProtectedProcess(string processName, HashSet<string> protectedProcesses)
        {
            string fileName = Path.GetFileName(processName);
            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);

            return protectedProcesses.Contains(fileName)
                || protectedProcesses.Contains(withoutExtension);
        }

        private string GetProcessExecutableName(uint pid)
        {
            IntPtr queryHandle = IntPtr.Zero;
            try
            {
                queryHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (queryHandle != IntPtr.Zero)
                {
                    var buffer = new StringBuilder(1024);
                    int size = buffer.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(queryHandle, 0, buffer, ref size))
                    {
                        return Path.GetFileName(buffer.ToString());
                    }

                    LogDebug($"ForceKillForegroundAppDirect: QueryFullProcessImageName falhou para PID={pid}. Win32Error={Marshal.GetLastWin32Error()}.");
                }

                using var process = Process.GetProcessById((int)pid);
                string fallbackName = process.ProcessName;
                return fallbackName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? fallbackName
                    : $"{fallbackName}.exe";
            }
            catch (Exception ex)
            {
                LogDebug($"ForceKillForegroundAppDirect: não foi possível identificar o processo PID={pid}: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                if (queryHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(queryHandle);
                }
            }
        }

        private IntPtr EnableDebugPrivilegeForForceKill()
        {
            if (!NativeMethods.OpenProcessToken(
                    Process.GetCurrentProcess().Handle,
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out IntPtr token))
            {
                LogDebug($"ForceKillForegroundAppDirect: OpenProcessToken falhou. Win32Error={Marshal.GetLastWin32Error()}.");
                return IntPtr.Zero;
            }

            if (!NativeMethods.LookupPrivilegeValue(null, NativeMethods.SE_DEBUG_NAME, out var luid))
            {
                LogDebug($"ForceKillForegroundAppDirect: LookupPrivilegeValue({NativeMethods.SE_DEBUG_NAME}) falhou. Win32Error={Marshal.GetLastWin32Error()}.");
                NativeMethods.CloseHandle(token);
                return IntPtr.Zero;
            }

            var privileges = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                }
            };

            bool adjusted = NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!adjusted || error == NativeMethods.ERROR_NOT_ALL_ASSIGNED)
            {
                LogDebug($"ForceKillForegroundAppDirect: não foi possível habilitar SeDebugPrivilege. Adjusted={adjusted}, Win32Error={error}.");
                NativeMethods.CloseHandle(token);
                return IntPtr.Zero;
            }

            return token;
        }

        private void DisableDebugPrivilegeForForceKill(IntPtr token)
        {
            if (token == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (NativeMethods.LookupPrivilegeValue(null, NativeMethods.SE_DEBUG_NAME, out var luid))
                {
                    var privileges = new NativeMethods.TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                        {
                            Luid = luid,
                            Attributes = 0
                        }
                    };

                    NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                if (key == null) return false;
                var value = key.GetValue("TutzApp");
                return value != null;
            }
            catch (Exception ex)
            {
                LogDebug($"Erro ao ler registro de inicialização: {ex.Message}");
                return false;
            }
        }

        public static void EnsureAutoStartRunKeyTargetsCurrentExecutable()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null || key.GetValue("TutzApp") == null)
                {
                    return;
                }

                string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return;
                }

                string desiredValue = $"\"{exePath}\" --background";
                string currentValue = key.GetValue("TutzApp")?.ToString() ?? string.Empty;
                if (!currentValue.Equals(desiredValue, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("TutzApp", desiredValue);
                }
            }
            catch { }
        }

        public void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                {
                    LogDebug("SetAutoStart: Não foi possível obter o caminho do executável.");
                    return;
                }

                if (enable)
                {
                    key.SetValue("TutzApp", $"\"{exePath}\" --background");
                    LogDebug("SetAutoStart: Inicialização do app principal ativada no registro.");

                    try
                    {
                        SendAdminCommand("SHUTDOWN_ADMIN");
                        WakeAdminPipeServer();

                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "--register-task",
                            Verb = "runas",
                            UseShellExecute = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit();
                        LogDebug("SetAutoStart: Chamada de registro de tarefa concluída.");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"SetAutoStart: Falha ao solicitar UAC para registrar tarefa: {ex.Message}");
                    }
                }
                else
                {
                    key.DeleteValue("TutzApp", false);
                    LogDebug("SetAutoStart: Inicialização do app principal desativada no registro.");

                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "--unregister-task",
                            Verb = "runas",
                            UseShellExecute = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit();
                        LogDebug("SetAutoStart: Chamada de remoção de tarefa concluída.");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"SetAutoStart: Falha ao solicitar UAC para remover tarefa: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao configurar inicialização automática: {ex.Message}");
            }
        }

        public static bool IsCurrentProcessElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static readonly object PipeLock = new();
        private static readonly object EnsureAdminHelperLock = new();

        public static string SendAdminCommand(string command)
        {
            lock (PipeLock)
            {
                try
                {
                    using var pipeClient = new System.IO.Pipes.NamedPipeClientStream(".", "TutzAppAdminPipe", System.IO.Pipes.PipeDirection.InOut);
                    pipeClient.Connect(2500);

                    using var writer = new StreamWriter(pipeClient, System.Text.Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(pipeClient, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);

                    writer.WriteLine(command);
                    return reader.ReadLine() ?? "ERROR: No response";
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.ToString()}";
                }
            }
        }

        public static bool EnsureAdminHelperRunning()
        {
            lock (EnsureAdminHelperLock)
            {
                if (IsCurrentExecutableHelperRunning())
                {
                    return true;
                }

                StopUnhealthyAdminHelperIfNeeded();

                TryWriteAdminHelperDiagnostic("PING failed or helper outdated. Starting direct elevated helper.");
                try
                {
                    string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = $"--admin-helper --parent-pid {System.Diagnostics.Process.GetCurrentProcess().Id}",
                            Verb = "runas",
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        System.Threading.Thread.Sleep(2000);
                    }
                }
                catch (Exception ex)
                {
                    TryWriteAdminHelperDiagnostic($"Direct elevated helper start failed: {ex}");
                }

                bool ready = IsCurrentExecutableHelperRunning();
                TryWriteAdminHelperDiagnostic(ready ? "Helper started via direct elevated process." : "Helper still unavailable after direct start.");
                return ready;
            }
        }

        private static void StopUnhealthyAdminHelperIfNeeded()
        {
            try
            {
                if (SendAdminCommand("PING") != "PONG")
                {
                    return;
                }

                string currentExePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                string helperExePath = SendAdminCommand("GET_EXE_PATH");
                bool sameExecutable = !string.IsNullOrWhiteSpace(currentExePath) &&
                    helperExePath.Equals(currentExePath, StringComparison.OrdinalIgnoreCase);
                string hookStatus = sameExecutable ? SendAdminCommand("HOOK_STATUS") : "SKIPPED_FOR_OUTDATED_HELPER";

                if (sameExecutable && hookStatus == "HOOK_OK")
                {
                    return;
                }

                string reason = sameExecutable
                    ? $"admin keyboard hook unhealthy: {hookStatus}"
                    : $"outdated helper detected at '{helperExePath}'";
                TryWriteAdminHelperDiagnostic($"{reason}. Requesting shutdown.");
                SendAdminCommand("SHUTDOWN_ADMIN");
                WakeAdminPipeServer();
                System.Threading.Thread.Sleep(1200);
            }
            catch (Exception ex)
            {
                TryWriteAdminHelperDiagnostic($"Stop unhealthy helper failed: {ex}");
            }
        }

        private static void WakeAdminPipeServer()
        {
            try
            {
                using var pipeClient = new System.IO.Pipes.NamedPipeClientStream(".", "TutzAppAdminPipe", System.IO.Pipes.PipeDirection.InOut);
                pipeClient.Connect(300);
            }
            catch { }
        }

        private static bool IsCurrentExecutableHelperRunning()
        {
            if (SendAdminCommand("PING") != "PONG")
            {
                return false;
            }

            string currentExePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                return true;
            }

            string helperExePath = SendAdminCommand("GET_EXE_PATH");
            if (!helperExePath.Equals(currentExePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string hookStatus = SendAdminCommand("HOOK_STATUS");
            if (hookStatus != "HOOK_OK")
            {
                TryWriteAdminHelperDiagnostic($"Helper responded but admin keyboard hook is unhealthy: {hookStatus}");
                return false;
            }

            return true;
        }

        private static void RunAdminHelperTask()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/run /tn \"TutzAppAdminHelper\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                TryWriteAdminHelperDiagnostic($"Run task failed: {ex}");
            }
        }

        private static void RegisterAdminHelperTaskForCurrentExecutableElevated()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }

                var registerPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--register-task",
                    Verb = "runas",
                    UseShellExecute = true
                };
                using var registerProc = System.Diagnostics.Process.Start(registerPsi);
                registerProc?.WaitForExit();
                if (registerProc?.ExitCode != 0)
                {
                    TryWriteAdminHelperDiagnostic($"Register task returned exit code {registerProc?.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                TryWriteAdminHelperDiagnostic($"Register task failed: {ex}");
            }
        }

        private static void TryWriteAdminHelperDiagnostic(string message)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin_helper_err.txt");
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        public void ShowNotification(string title, string message)
        {
            NotificationRequested?.Invoke(title, message);
        }

        public void PreloadTouchKeyboard()
        {
            LogDebug("PreloadTouchKeyboard: Pré-carregando o teclado de toque...");
            try
            {
                // 1. Invoca o teclado de toque
                try
                {
                    var tip = new NativeMethods.TipInvocation() as NativeMethods.ITipInvocation;
                    tip?.Toggle(NativeMethods.GetDesktopWindow());
                }
                catch
                {
                    // Fallback
                    Process.Start(new ProcessStartInfo { FileName = "TabTip.exe", UseShellExecute = true });
                }

                // 2. Aguarda um breve intervalo para inicializar
                System.Threading.Thread.Sleep(200);

                // 3. Finaliza os processos de exibição rápida para mantê-lo apenas pré-carregado
                KillProcessSilently("TextInputHost.exe");
                KillProcessSilently("TabTip.exe");
                LogDebug("PreloadTouchKeyboard: Processos finalizados. Teclado pré-carregado com sucesso.");
            }
            catch (Exception ex)
            {
                LogDebug($"PreloadTouchKeyboard: Erro ao pré-carregar teclado de toque: {ex.Message}");
            }
        }

        public void InvokeTouchKeyboard(bool manageControllerVkMapping = true)
        {
            if (manageControllerVkMapping)
            {
                try
                {
                    EnsureAdminHelperRunning();
                    string response = SendAdminCommand("TOGGLE_VIRTUAL_KEYBOARD_MAPPING");
                    LogDebug(
                        $"InvokeTouchKeyboard: estado temporário do ControllerToVKMapping alternado. Resposta: {response}");
                }
                catch (Exception ex)
                {
                    LogDebug($"InvokeTouchKeyboard: não foi possível ativar ControllerToVKMapping: {ex.Message}");
                }
            }

            try
            {
                var tip = new NativeMethods.TipInvocation() as NativeMethods.ITipInvocation;
                tip?.Toggle(NativeMethods.GetDesktopWindow());
            }
            catch (Exception ex)
            {
                LogDebug($"COM ITipInvocation falhou: {ex.Message}");
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = "TabTip.exe", UseShellExecute = true });
                }
                catch (Exception ex2)
                {
                    LogDebug($"Falha ao iniciar TabTip: {ex2.Message}");
                }
            }
        }

        private void KillProcessSilently(string processName)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(processName);
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    proc.Kill();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"KillProcessSilently ({processName}): {ex.Message}");
            }
        }

        public void ShowKeyboardShortcutOverlay()
        {
            RunOnUiAsync(() =>
            {
                try
                {
                    if (_keyboardShortcutOverlay == null)
                    {
                        _keyboardShortcutOverlay = new Views.KeyboardShortcutOverlayWindow();
                        _keyboardShortcutOverlay.Closed += (s, e) =>
                        {
                            _keyboardShortcutOverlay = null;
                            _isKeyboardShortcutOverlayVisible = false;
                        };
                    }
                    _keyboardShortcutOverlay.Show();
                    _isKeyboardShortcutOverlayVisible = true;
                    LogDebug("SystemControlService: Overlay de atalhos do teclado exibido.");
                }
                catch (Exception ex)
                {
                    _isKeyboardShortcutOverlayVisible = false;
                    LogDebug($"Erro ao exibir overlay de atalhos de teclado: {ex.Message}");
                }
            });
        }

        public void HideKeyboardShortcutOverlay()
        {
            RunOnUiAsync(() =>
            {
                try
                {
                    if (_keyboardShortcutOverlay != null)
                    {
                        _keyboardShortcutOverlay.Close();
                        _keyboardShortcutOverlay = null;
                        _isKeyboardShortcutOverlayVisible = false;
                        LogDebug("SystemControlService: Overlay de atalhos do teclado ocultado.");
                    }
                }
                catch (Exception ex)
                {
                    _isKeyboardShortcutOverlayVisible = false;
                    LogDebug($"Erro ao fechar overlay de atalhos de teclado: {ex.Message}");
                }
            });
        }

        public void ShowGamepadShortcutOverlay()
        {
            RunOnUiAsync(() =>
            {
                try
                {
                    if (_gamepadShortcutOverlay == null)
                    {
                        _gamepadShortcutOverlay = new Views.GamepadShortcutOverlayWindow(Config.GamepadShortcuts);
                        _gamepadShortcutOverlay.Closed += (s, e) =>
                        {
                            _gamepadShortcutOverlay = null;
                            _isGamepadShortcutOverlayVisible = false;
                        };
                    }
                    _gamepadShortcutOverlay.Show();
                    _isGamepadShortcutOverlayVisible = true;
                    LogDebug("SystemControlService: Overlay de atalhos do gamepad exibido.");
                }
                catch (Exception ex)
                {
                    _isGamepadShortcutOverlayVisible = false;
                    LogDebug($"Erro ao exibir overlay de atalhos de gamepad: {ex.Message}");
                }
            });
        }

        public void HideGamepadShortcutOverlay()
        {
            RunOnUiAsync(() =>
            {
                try
                {
                    if (_gamepadShortcutOverlay != null)
                    {
                        _gamepadShortcutOverlay.Close();
                        _gamepadShortcutOverlay = null;
                        _isGamepadShortcutOverlayVisible = false;
                        LogDebug("SystemControlService: Overlay de atalhos do gamepad ocultado.");
                    }
                }
                catch (Exception ex)
                {
                    _isGamepadShortcutOverlayVisible = false;
                    LogDebug($"Erro ao fechar overlay de atalhos de gamepad: {ex.Message}");
                }
            });
        }

        private void RunOnUiAsync(Action action)
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            }
        }

        public void SimulateKeyboardShortcut(string shortcutText)
        {
            LogDebug($"SimulateKeyboardShortcut: Simulando '{shortcutText}'");
            if (string.IsNullOrWhiteSpace(shortcutText)) return;

            try
            {
                var parts = shortcutText.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var modifiers = new List<ushort>();
                ushort primaryKey = 0;

                foreach (var part in parts)
                {
                    var lower = part.ToLowerInvariant();
                    if (lower == "ctrl" || lower == "control" || lower == "crtl")
                        modifiers.Add(0x11); // VK_CONTROL
                    else if (lower == "alt")
                        modifiers.Add(0x12); // VK_MENU
                    else if (lower == "shift")
                        modifiers.Add(0x10); // VK_SHIFT
                    else if (lower == "win" || lower == "windows" || lower == "super" || lower == "meta" || lower == "winkey")
                        modifiers.Add(0x5B); // VK_LWIN
                    else
                    {
                        primaryKey = ParseKeyName(lower);
                    }
                }

                if (primaryKey != 0 || modifiers.Count > 0)
                {
                    SendKeyCombination(modifiers, primaryKey);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Erro ao simular atalho '{shortcutText}': {ex.Message}");
            }
        }

        private static ushort ParseKeyName(string keyName)
        {
            if (keyName.Length == 1 && keyName[0] >= 'a' && keyName[0] <= 'z')
                return (ushort)(0x41 + (keyName[0] - 'a'));
            if (keyName.Length == 1 && keyName[0] >= '0' && keyName[0] <= '9')
                return (ushort)(0x30 + (keyName[0] - '0'));
            if (keyName.StartsWith("f") && keyName.Length > 1 && int.TryParse(keyName.Substring(1), out var fNum) && fNum >= 1 && fNum <= 12)
                return (ushort)(0x70 + (fNum - 1));

            switch (keyName)
            {
                case "enter": return 0x0D;
                case "esc":
                case "escape": return 0x1B;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "backspace":
                case "bs": return 0x08;
                case "delete":
                case "del": return 0x2E;
                case "printscreen":
                case "prtsc": return 0x2C;
                case "home": return 0x24;
                case "end": return 0x23;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "pageup":
                case "pgup": return 0x21;
                case "pagedown":
                case "pgdn": return 0x22;
                default: return 0;
            }
        }

        private void SendKeyCombination(List<ushort> modifiers, ushort key)
        {
            try
            {
                int eventCount = (modifiers.Count + (key != 0 ? 1 : 0)) * 2;
                var inputs = new NativeMethods.INPUT[eventCount];
                int idx = 0;

                // Modificadores Down
                for (int i = 0; i < modifiers.Count; i++)
                {
                    inputs[idx].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[idx].U.ki.wVk = modifiers[i];
                    inputs[idx].U.ki.dwFlags = 0;
                    inputs[idx].U.ki.dwExtraInfo = SimulatedKeySignature;
                    idx++;
                }

                // Tecla Principal Down
                if (key != 0)
                {
                    inputs[idx].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[idx].U.ki.wVk = key;
                    inputs[idx].U.ki.dwFlags = 0;
                    inputs[idx].U.ki.dwExtraInfo = SimulatedKeySignature;
                    idx++;

                    // Tecla Principal Up
                    inputs[idx].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[idx].U.ki.wVk = key;
                    inputs[idx].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                    inputs[idx].U.ki.dwExtraInfo = SimulatedKeySignature;
                    idx++;
                }

                // Modificadores Up (Ordem Inversa)
                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    inputs[idx].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[idx].U.ki.wVk = modifiers[i];
                    inputs[idx].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                    inputs[idx].U.ki.dwExtraInfo = SimulatedKeySignature;
                    idx++;
                }

                NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao simular combinação de teclas: {ex.Message}");
            }
        }

        public void RunExecutable(string exePath)
        {
            LogDebug($"RunExecutable: Executando '{exePath}'");
            if (string.IsNullOrWhiteSpace(exePath)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                LogDebug($"Erro ao executar '{exePath}': {ex.Message}");
            }
        }

        public void OpenTerminal(string? workingDirectory = null)
        {
            StartTerminal(workingDirectory, elevated: false);
        }

        public void OpenTerminalAdmin(string? workingDirectory = null)
        {
            StartTerminal(workingDirectory, elevated: true);
        }

        private void StartTerminal(string? workingDirectory, bool elevated)
        {
            if (TryStartTerminalProcess(
                    workingDirectory,
                    elevated,
                    out string directory,
                    out string? errorMessage))
            {
                LogDebug($"OpenTerminal: Windows Terminal {(elevated ? "admin" : "normal")} iniciado em '{directory}'.");
                return;
            }

            LogDebug($"OpenTerminal: falha ao abrir Windows Terminal {(elevated ? "admin" : "normal")} em '{directory}': {errorMessage}");
            SetStatusMessage(elevated ? "Falha ao abrir terminal admin" : "Falha ao abrir terminal");
        }

        internal static bool TryStartTerminalProcess(
            string? workingDirectory,
            bool elevated,
            out string resolvedDirectory,
            out string? errorMessage)
        {
            resolvedDirectory = ResolveTerminalWorkingDirectory(workingDirectory);
            errorMessage = null;

            string terminalArguments = $"-d {QuoteWindowsArgument(resolvedDirectory)}";

            // File Pilot may execute classic verbs from an elevated broker. In that
            // case a normal Process.Start would pass the elevated token to wt.exe.
            // Microsoft's Execute In Explorer pattern delegates creation to the
            // desktop Explorer process, preserving a normal user terminal.
            if (!elevated && IsCurrentProcessElevated())
            {
                return ExplorerProcessLauncher.TryShellExecute(
                    "wt.exe",
                    terminalArguments,
                    resolvedDirectory,
                    out errorMessage);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = terminalArguments,
                    WorkingDirectory = resolvedDirectory,
                    UseShellExecute = true
                };

                if (elevated)
                {
                    psi.Verb = "runas";
                }

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    errorMessage = "Process.Start retornou null.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string ResolveTerminalWorkingDirectory(string? workingDirectory)
        {
            string fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(fallback))
            {
                fallback = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            if (string.IsNullOrWhiteSpace(fallback))
            {
                fallback = AppContext.BaseDirectory;
            }

            try
            {
                string candidate = (workingDirectory ?? string.Empty).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return Directory.Exists(fallback) ? fallback : AppContext.BaseDirectory;
                }

                candidate = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(candidate))
                {
                    string? parent = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    {
                        return parent;
                    }
                }

                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Fall through to the same stable fallback used by the interactive app.
            }

            return Directory.Exists(fallback) ? fallback : AppContext.BaseDirectory;
        }

        private static string QuoteWindowsArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }
                builder.Append(c);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        #region DPI Scaling CCD API Integration
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const uint QDC_ALL_PATHS = 1;
        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const uint QDC_VIRTUAL_MODE_AWARE = 0x00000010;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
        private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
        private const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;
        private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
        private const uint SDC_APPLY = 0x00000080;
        private const uint SDC_ALLOW_CHANGES = 0x00000400;
        private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;
        private const uint SDC_VIRTUAL_MODE_AWARE = 0x00008000;

        [StructLayout(LayoutKind.Sequential)]
        public struct CCD_LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public CCD_LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public int minScaleRel;
            public int curScaleRel;
            public int maxScaleRel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public int scaleRel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public CCD_LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public CCD_LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)]
            public uint infoType;
            [FieldOffset(4)]
            public uint id;
            [FieldOffset(8)]
            public CCD_LUID adapterId;
            [FieldOffset(16)]
            public ulong mode0;
            [FieldOffset(24)]
            public ulong mode1;
            [FieldOffset(32)]
            public ulong mode2;
            [FieldOffset(40)]
            public ulong mode3;
            [FieldOffset(48)]
            public ulong mode4;
            [FieldOffset(56)]
            public ulong mode5;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(
            uint numPathArrayElements,
            [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements,
            [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
            uint flags);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET setPacket);

        private static readonly int[] DpiVals = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

        private bool ToggleDisplayTargetCore()
        {
            try
            {
                if (!TryQueryActiveDisplayConfiguration(
                        out DISPLAYCONFIG_PATH_INFO[] currentPaths,
                        out DISPLAYCONFIG_MODE_INFO[] currentModes))
                {
                    return false;
                }

                if (_displayTogglePaths == null || _displayToggleModes == null)
                {
                    if (currentPaths.Length == 2 && currentModes.Length > 0)
                    {
                        _displayTogglePaths = currentPaths;
                        _displayToggleModes = currentModes;
                        _displayToggleUsesTopologyDatabase = false;
                        LogDisplayToggleTargets(_displayTogglePaths);
                    }
                    else if (!TryDiscoverDisplayToggleTopology(
                                 currentPaths,
                                 out DISPLAYCONFIG_PATH_INFO[] topologyPaths))
                    {
                        LogDebug(
                            $"ToggleDisplayTarget: não foi possível detectar duas telas conectadas; " +
                            $"paths ativos={currentPaths.Length}, modes={currentModes.Length}.");
                        SetStatusMessage("Alternância requer exatamente duas telas conectadas");
                        return false;
                    }
                    else
                    {
                        _displayTogglePaths = topologyPaths;
                        _displayToggleModes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
                        _displayToggleUsesTopologyDatabase = true;
                        LogDisplayToggleTargets(_displayTogglePaths);
                    }
                }

                var cachedKeys = _displayTogglePaths
                    .Select(GetDisplayTargetKey)
                    .ToArray();
                var activeKeys = currentPaths
                    .Where(path => (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                    .Select(GetDisplayTargetKey)
                    .ToArray();

                if (activeKeys.Length == 0 || activeKeys.Any(key => !cachedKeys.Contains(key)))
                {
                    LogDebug("ToggleDisplayTarget: o estado atual contém uma saída fora do par detectado.");
                    SetStatusMessage("As telas conectadas mudaram; reinicie o app para alternar");
                    return false;
                }

                if (activeKeys.Length != 1 && activeKeys.Length != cachedKeys.Length)
                {
                    LogDebug($"ToggleDisplayTarget: estado de telas não exclusivo. active={activeKeys.Length}.");
                    SetStatusMessage("Não foi possível identificar a tela ativa");
                    return false;
                }

                var currentKey = activeKeys[0];
                var targetKey = cachedKeys.First(key => key != currentKey);
                var targetIndex = Array.FindIndex(_displayTogglePaths, path => GetDisplayTargetKey(path) == targetKey);
                if (targetIndex < 0)
                {
                    LogDebug("ToggleDisplayTarget: destino da alternância não encontrado no snapshot.");
                    return false;
                }

                var pathsToApply = (DISPLAYCONFIG_PATH_INFO[])_displayTogglePaths.Clone();
                for (int i = 0; i < pathsToApply.Length; i++)
                {
                    pathsToApply[i].flags &= ~DISPLAYCONFIG_PATH_ACTIVE;
                }
                pathsToApply[targetIndex].flags |= DISPLAYCONFIG_PATH_ACTIVE;

                int result;
                if (_displayToggleUsesTopologyDatabase)
                {
                    for (int i = 0; i < pathsToApply.Length; i++)
                    {
                        pathsToApply[i].sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                        pathsToApply[i].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    }

                    result = SetDisplayConfig(
                        (uint)pathsToApply.Length,
                        pathsToApply,
                        0,
                        null,
                        SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
                }
                else
                {
                    var modesToApply = (DISPLAYCONFIG_MODE_INFO[])_displayToggleModes!.Clone();
                    uint flags = SDC_APPLY
                        | SDC_USE_SUPPLIED_DISPLAY_CONFIG
                        | SDC_ALLOW_CHANGES
                        | SDC_VIRTUAL_MODE_AWARE;
                    result = SetDisplayConfig(
                        (uint)pathsToApply.Length,
                        pathsToApply,
                        (uint)modesToApply.Length,
                        modesToApply,
                        flags);
                }
                if (result != 0)
                {
                    LogDebug($"ToggleDisplayTarget: SetDisplayConfig falhou com erro {result}.");
                    SetStatusMessage("Falha ao alternar as telas");
                    return false;
                }

                string targetName = GetDisplayTargetDescription(_displayTogglePaths[targetIndex]);
                LogDebug($"ToggleDisplayTarget: tela ativa alternada para {targetName}.");
                SetStatusMessage($"Tela ativa: {targetName}");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"ToggleDisplayTarget: exceção ao alternar telas: {ex}");
                SetStatusMessage("Falha ao alternar as telas");
                return false;
            }
        }

        private bool TryDiscoverDisplayToggleTopology(
            DISPLAYCONFIG_PATH_INFO[] currentPaths,
            out DISPLAYCONFIG_PATH_INFO[] paths)
        {
            paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            var activePaths = currentPaths
                .Where(path => (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                .ToArray();
            if (activePaths.Length != 1)
            {
                return false;
            }

            if (!TryQueryDisplayConfiguration(
                    QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE,
                    out DISPLAYCONFIG_PATH_INFO[] allPaths,
                    out _))
            {
                return false;
            }

            var availablePaths = allPaths
                .Where(path => path.targetInfo.targetAvailable)
                .GroupBy(GetDisplayTargetKey)
                .Select(group => group.First())
                .ToArray();
            var currentKey = GetDisplayTargetKey(activePaths[0]);
            var currentPath = activePaths[0];
            var otherPaths = availablePaths
                .Where(path => GetDisplayTargetKey(path) != currentKey)
                .ToArray();

            if (availablePaths.Length != 2 || otherPaths.Length != 1)
            {
                LogDebug(
                    $"ToggleDisplayTarget: saídas conectadas detectadas={availablePaths.Length}; " +
                    "a alternância exige exatamente duas.");
                return false;
            }

            paths = new[] { currentPath, otherPaths[0] };
            return true;
        }

        private bool TryQueryActiveDisplayConfiguration(
            out DISPLAYCONFIG_PATH_INFO[] paths,
            out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            return TryQueryDisplayConfiguration(
                QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE,
                out paths,
                out modes);
        }

        private bool TryQueryDisplayConfiguration(
            uint flags,
            out DISPLAYCONFIG_PATH_INFO[] paths,
            out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();

            for (int attempt = 0; attempt < 4; attempt++)
            {
                int result = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (result != 0)
                {
                    LogDebug($"ToggleDisplayTarget: GetDisplayConfigBufferSizes falhou com erro {result}.");
                    return false;
                }

                var pathArray = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modeArray = new DISPLAYCONFIG_MODE_INFO[modeCount];
                uint returnedPathCount = pathCount;
                uint returnedModeCount = modeCount;
                result = QueryDisplayConfig(
                    flags,
                    ref returnedPathCount,
                    pathArray,
                    ref returnedModeCount,
                    modeArray,
                    IntPtr.Zero);
                if (result == ERROR_INSUFFICIENT_BUFFER)
                {
                    continue;
                }

                if (result != 0)
                {
                    LogDebug($"ToggleDisplayTarget: QueryDisplayConfig falhou com erro {result}.");
                    return false;
                }

                paths = pathArray.Take((int)returnedPathCount).ToArray();
                modes = modeArray.Take((int)returnedModeCount).ToArray();
                return true;
            }

            LogDebug("ToggleDisplayTarget: a configuração mudou durante a consulta.");
            return false;
        }

        private void LogDisplayToggleTargets(IEnumerable<DISPLAYCONFIG_PATH_INFO> paths)
        {
            int index = 1;
            foreach (var path in paths)
            {
                LogDebug($"ToggleDisplayTarget: tela {index++} = {GetDisplayTargetDescription(path)}.");
            }
        }

        private string GetDisplayTargetDescription(DISPLAYCONFIG_PATH_INFO path)
        {
            string sourceName = string.Empty;
            var sourcePacket = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            sourcePacket.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            sourcePacket.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            sourcePacket.header.adapterId = path.sourceInfo.adapterId;
            sourcePacket.header.id = path.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref sourcePacket) == 0)
            {
                sourceName = sourcePacket.viewGdiDeviceName ?? string.Empty;
            }

            string friendlyName = string.Empty;
            var targetPacket = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            targetPacket.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            targetPacket.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            targetPacket.header.adapterId = path.targetInfo.adapterId;
            targetPacket.header.id = path.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(ref targetPacket) == 0)
            {
                friendlyName = targetPacket.monitorFriendlyDeviceName ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = "nome desconhecido";
            }
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                sourceName = "GDI desconhecido";
            }

            return $"{friendlyName} ({sourceName})";
        }

        private static (uint LowPart, int HighPart, uint TargetId) GetDisplayTargetKey(
            DISPLAYCONFIG_PATH_INFO path)
        {
            return (
                path.targetInfo.adapterId.LowPart,
                path.targetInfo.adapterId.HighPart,
                path.targetInfo.id);
        }

        public bool SetDisplayDpi(int targetPercent)
        {
            LogDebug($"SetDisplayDpi: Solicitando DPI de {targetPercent}%");
            try
            {
                string? primaryName = GetPrimaryDisplayDeviceName();
                if (string.IsNullOrEmpty(primaryName))
                {
                    LogDebug("SetDisplayDpi: ERRO - Monitor primário não encontrado.");
                    return false;
                }

                int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
                if (err != 0)
                {
                    LogDebug($"SetDisplayDpi: GetDisplayConfigBufferSizes falhou com erro {err}");
                    return false;
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (err != 0)
                {
                    LogDebug($"SetDisplayDpi: QueryDisplayConfig falhou com erro {err}");
                    return false;
                }

                for (int i = 0; i < numPaths; i++)
                {
                    var path = paths[i];
                    
                    var namePacket = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    namePacket.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    namePacket.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    namePacket.header.adapterId = path.sourceInfo.adapterId;
                    namePacket.header.id = path.sourceInfo.id;

                    int nameErr = DisplayConfigGetDeviceInfo(ref namePacket);
                    if (nameErr == 0)
                    {
                        if (namePacket.viewGdiDeviceName.Equals(primaryName, StringComparison.OrdinalIgnoreCase))
                        {
                            LogDebug($"SetDisplayDpi: Encontrada correspondência de monitor primário: {namePacket.viewGdiDeviceName}");

                            var dpiPacket = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET();
                            dpiPacket.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE;
                            dpiPacket.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DPI_SCALE_GET));
                            dpiPacket.header.adapterId = path.sourceInfo.adapterId;
                            dpiPacket.header.id = path.sourceInfo.id;

                            int dpiGetErr = DisplayConfigGetDeviceInfo(ref dpiPacket);
                            if (dpiGetErr != 0)
                            {
                                LogDebug($"SetDisplayDpi: DisplayConfigGetDeviceInfo (GET) falhou com erro {dpiGetErr}");
                                return false;
                            }

                            int targetIndex = Array.IndexOf(DpiVals, targetPercent);
                            if (targetIndex < 0)
                            {
                                LogDebug($"SetDisplayDpi: DPI alvo {targetPercent}% não suportado.");
                                return false;
                            }

                            int recIndex = -dpiPacket.minScaleRel;
                            int targetScaleRel = targetIndex - recIndex;

                            if (targetScaleRel < dpiPacket.minScaleRel || targetScaleRel > dpiPacket.maxScaleRel)
                            {
                                LogDebug($"SetDisplayDpi: Escala de DPI {targetPercent}% está fora do intervalo suportado para este monitor.");
                                targetScaleRel = Math.Clamp(targetScaleRel, dpiPacket.minScaleRel, dpiPacket.maxScaleRel);
                                LogDebug($"SetDisplayDpi: Clampado para {DpiVals[targetScaleRel + recIndex]}% (offset {targetScaleRel})");
                            }

                            if (targetScaleRel == dpiPacket.curScaleRel)
                            {
                                LogDebug("SetDisplayDpi: Escala DPI desejada já está aplicada.");
                                return true;
                            }

                            var setPacket = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET();
                            setPacket.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE;
                            setPacket.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DPI_SCALE_SET));
                            setPacket.header.adapterId = path.sourceInfo.adapterId;
                            setPacket.header.id = path.sourceInfo.id;
                            setPacket.scaleRel = targetScaleRel;

                            int dpiSetErr = DisplayConfigSetDeviceInfo(ref setPacket);
                            if (dpiSetErr != 0)
                            {
                                LogDebug($"SetDisplayDpi: DisplayConfigSetDeviceInfo (SET) falhou com erro {dpiSetErr}");
                                return false;
                            }

                            LogDebug($"SetDisplayDpi: SUCESSO. DPI alterado para {targetPercent}%");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"SetDisplayDpi: Exception: {ex.Message}");
            }
            return false;
        }

        private void ApplyDpiScalingForResolution(uint height)
        {
            if (height == 1440)
            {
                SetDisplayDpi(125);
            }
            else if (height == 1080)
            {
                SetDisplayDpi(100);
            }
        }
        #endregion

        #region Foreground Process Tracking
        public string? GetForegroundProcessName()
        {
            try
            {
                long now = Environment.TickCount64;
                lock (_foregroundProcessCacheLock)
                {
                    if (now - _cachedForegroundProcessTimestamp <= 50)
                    {
                        return _cachedForegroundProcessName;
                    }
                }

                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return CacheForegroundProcessName(null, now);

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return CacheForegroundProcessName(null, now);

                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return CacheForegroundProcessName(null, now);

                try
                {
                    var builder = new System.Text.StringBuilder(260);
                    int size = builder.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(hProcess, 0, builder, ref size))
                    {
                        string path = builder.ToString();
                        return CacheForegroundProcessName(Path.GetFileNameWithoutExtension(path), now);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Erro ao obter nome do processo em primeiro plano: {ex.Message}");
            }
            return CacheForegroundProcessName(null, Environment.TickCount64);
        }

        private string? CacheForegroundProcessName(string? processName, long timestamp)
        {
            lock (_foregroundProcessCacheLock)
            {
                _cachedForegroundProcessName = processName;
                _cachedForegroundProcessTimestamp = timestamp;
            }

            return processName;
        }
        #endregion
    }
}
