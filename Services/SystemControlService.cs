// Services/SystemControlService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    public class SystemControlService : ISystemControlService
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
        private static readonly BlockingCollection<LogWriteEntry> LogWriteQueue = new(new ConcurrentQueue<LogWriteEntry>());
        private static readonly ManualResetEventSlim LogQueueDrained = new(true);
        private static readonly Task LogWriterTask = Task.Run(ProcessLogWriteQueue);
        private static int PendingLogWrites;
        private static long ProfileTransitionSequence;
        private readonly object _foregroundProcessCacheLock = new();
        private string? _cachedForegroundProcessName;
        private long _cachedForegroundProcessTimestamp;

        private Views.KeyboardShortcutOverlayWindow? _keyboardShortcutOverlay;
        private volatile bool _isKeyboardShortcutOverlayVisible;
        public bool IsKeyboardShortcutOverlayVisible => _isKeyboardShortcutOverlayVisible;

        private Views.GamepadShortcutOverlayWindow? _gamepadShortcutOverlay;
        private volatile bool _isGamepadShortcutOverlayVisible;
        public bool IsGamepadShortcutOverlayVisible => _isGamepadShortcutOverlayVisible;

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
            
            // Inicializar fisicamente a velocidade do mouse e o status
            SetMouseSpeed(Config.Mouse.SlowSpeed, false);
        }

        private void OnStatusChanged()
        {
            StatusChanged?.Invoke();
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

                Config.Resolutions = newConfig.Resolutions;
                Config.GamepadShortcuts = newConfig.GamepadShortcuts;

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                string json = System.Text.Json.JsonSerializer.Serialize(Config, options);
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                File.WriteAllText(path, json);

                LogDebug("SaveAppConfig: Configurações salvas e aplicadas.");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"SaveAppConfig: Erro ao salvar configurações: {ex.Message}");
                return false;
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

        public void AdjustBrightness(int offset)
        {
            LogDebug($"AdjustBrightness: Ajustando brilho por offset {offset}");
            if (!File.Exists(Config.TwinkleTrayPath))
            {
                LogDebug($"AdjustBrightness: Twinkle Tray não encontrado em '{Config.TwinkleTrayPath}'");
                return;
            }

            try
            {
                string sign = offset >= 0 ? "+" : "";
                string args = $"--MonitorNum=1 --Offset={sign}{offset}";

                var psi = new ProcessStartInfo
                {
                    FileName = Config.TwinkleTrayPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                LogDebug($"Exception ao ajustar brilho via Twinkle Tray: {ex.Message}");
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

        public void InvokeTouchKeyboard()
        {
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

        #region DPI Scaling CCD API Integration
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint QDC_ONLY_ACTIVE_PATHS = 2;

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

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            // Placeholder matching 64 bytes
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
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET setPacket);

        private static readonly int[] DpiVals = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

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
