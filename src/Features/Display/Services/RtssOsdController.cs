using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TutzApp.Services
{
    internal sealed class RtssOsdController
    {
        private const uint AllBits = 0xFFFFFFFF;
        private const uint RtssHooksFlagOsdVisible = 1;
        private const uint WmRtssUpdateSettings = 0x8000 + 100;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint FileMapRead = 0x0004;

        private readonly Action<string> _log;
        private readonly object _syncRoot = new();
        private IntPtr _module;
        private string? _modulePath;
        private SetFlagsDelegate? _setFlags;
        private GetFlagsDelegate? _getFlags;

        public RtssOsdController(Action<string> log)
        {
            _log = log;
        }

        public bool TryToggle(out bool visible, out string? error)
        {
            visible = false;
            error = null;

            lock (_syncRoot)
            {
                if (!EnsureInitialized(out error))
                {
                    return false;
                }

                try
                {
                    if (_setFlags is null || _getFlags is null)
                    {
                        error = "controlador RTSS inicializado sem SetFlags/GetFlags.";
                        _log($"RTSS OSD: {error}");
                        return false;
                    }

                    uint flagsBefore = _getFlags();
                    uint flagsAfterSet = _setFlags(AllBits, RtssHooksFlagOsdVisible);
                    PostUpdateSettings();
                    uint flagsAfterRead = _getFlags();
                    visible = (flagsAfterRead & RtssHooksFlagOsdVisible) != 0;

                    _log(
                        "RTSS OSD: overlay:toggle OK. " +
                        $"dll='{_modulePath}', flagsBefore=0x{flagsBefore:X8}, " +
                        $"flagsAfterSet=0x{flagsAfterSet:X8}, flagsAfterRead=0x{flagsAfterRead:X8}, visible={visible}");
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"excecao ao chamar RTSSHooks: {ex.Message}";
                    _log($"RTSS OSD: {error}");
                    ResetLoadedModule();
                    return false;
                }
            }
        }

        public bool IsForeground32BitApp(out string? processName)
        {
            processName = null;
            try
            {
                // 1. Verificar a janela em primeiro plano no Windows
                IntPtr foregroundHwnd = GetForegroundWindow();
                if (foregroundHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(foregroundHwnd, out uint pid);
                    if (pid != 0 && pid != (uint)Environment.ProcessId)
                    {
                        if (CheckIs32BitProcess(pid, out processName))
                        {
                            _log($"RTSS OSD: janela em primeiro plano (PID {pid}, '{processName}') é processo de 32 bits.");
                            return true;
                        }
                    }
                }

                // 2. Verificar via RTSSSharedMemoryV2 se RTSS registrou um processo recente ativo
                if (TryGetRtssForegroundProcess(out uint rtssPid, out string? rtssName))
                {
                    if (rtssPid != 0 && rtssPid != (uint)Environment.ProcessId)
                    {
                        if (CheckIs32BitProcess(rtssPid, out string? name))
                        {
                            processName = name ?? rtssName;
                            _log($"RTSS OSD: processo RTSS ativo (PID {rtssPid}, '{processName}') é processo de 32 bits.");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"RTSS OSD: exceção ao verificar arquitetura do processo em primeiro plano: {ex.Message}");
            }

            return false;
        }

        public (List<(ushort scancode, bool extended)> modifiers, ushort keyScancode, bool keyExtended) GetConfiguredOsdToggleHotkey()
        {
            try
            {
                foreach (string installDir in GetCandidateInstallDirs())
                {
                    string cfgPath = Path.Combine(installDir, "Plugins", "Client", "HotkeyHandler.cfg");
                    if (!File.Exists(cfgPath))
                    {
                        cfgPath = Path.Combine(installDir, "PlugIns", "Client", "HotkeyHandler.cfg");
                    }

                    if (!File.Exists(cfgPath))
                    {
                        continue;
                    }

                    foreach (string line in File.ReadLines(cfgPath))
                    {
                        if (line.StartsWith("OSDToggleHotkey=", StringComparison.OrdinalIgnoreCase))
                        {
                            string hex = line.Substring("OSDToggleHotkey=".Length).Trim();
                            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hotkeyVal) && hotkeyVal != 0)
                            {
                                uint modFlags = (hotkeyVal >> 16) & 0xFFFF;
                                uint vkCode = hotkeyVal & 0xFFFF;

                                var mods = new List<(ushort scancode, bool extended)>();
                                // Win32 HOTKEYF: 1 = SHIFT, 2 = CONTROL, 4 = ALT, 8 = EXT (Win)
                                if ((modFlags & 0x0004) != 0) // Alt
                                {
                                    mods.Add((0x38, false));
                                }
                                if ((modFlags & 0x0002) != 0) // Ctrl
                                {
                                    mods.Add((0x1D, false));
                                }
                                if ((modFlags & 0x0001) != 0) // Shift
                                {
                                    mods.Add((0x2A, false));
                                }
                                if ((modFlags & 0x0008) != 0) // Win
                                {
                                    mods.Add((0x5B, true));
                                }

                                ushort keyScan = (ushort)MapVirtualKeyW(vkCode, 0 /* MAPVK_VK_TO_VSC */);
                                bool keyExt = vkCode is 0x2D or 0x2E or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x5B or 0x5C;

                                if (keyScan != 0)
                                {
                                    return (mods, keyScan, keyExt);
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"RTSS OSD: erro ao ler HotkeyHandler.cfg: {ex.Message}");
            }

            // Fallback padrão: Alt + R (Alt scancode 0x38, R scancode 0x13)
            return (new List<(ushort, bool)> { (0x38, false) }, 0x13, false);
        }

        private static bool CheckIs32BitProcess(uint pid, out string? name)
        {
            name = null;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                name = proc.ProcessName;
            }
            catch { }

            IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (hProc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (IsWow64Process(hProc, out bool isWow64))
                {
                    return isWow64;
                }
            }
            finally
            {
                CloseHandle(hProc);
            }

            return false;
        }

        private static bool TryGetRtssForegroundProcess(out uint processId, out string? name)
        {
            processId = 0;
            name = null;

            IntPtr hMap = OpenFileMappingW(FileMapRead, false, "RTSSSharedMemoryV2");
            if (hMap == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                IntPtr pBuf = MapViewOfFile(hMap, FileMapRead, 0, 0, (UIntPtr)128);
                if (pBuf == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    uint sig = (uint)Marshal.ReadInt32(pBuf, 0);
                    // 0x52545353 = 'RTSS'
                    if (sig != 0x52545353)
                    {
                        return false;
                    }

                    processId = (uint)Marshal.ReadInt32(pBuf, 68); // dwLastForegroundAppProcessID
                    return processId != 0;
                }
                finally
                {
                    UnmapViewOfFile(pBuf);
                }
            }
            finally
            {
                CloseHandle(hMap);
            }
        }

        public bool IsForegroundAppHookedByRtss(out uint processId, out string? processName)
        {
            processId = 0;
            processName = null;

            IntPtr foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(foregroundHwnd, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)Environment.ProcessId)
            {
                return false;
            }

            processId = fgPid;
            try
            {
                using var proc = Process.GetProcessById((int)fgPid);
                processName = proc.ProcessName;
            }
            catch { }

            IntPtr hMap = OpenFileMappingW(FileMapRead, false, "RTSSSharedMemoryV2");
            if (hMap == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                IntPtr pBuf = MapViewOfFile(hMap, FileMapRead, 0, 0, UIntPtr.Zero);
                if (pBuf == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    uint sig = (uint)Marshal.ReadInt32(pBuf, 0);
                    if (sig != 0x52545353) // 'RTSS'
                    {
                        return false;
                    }

                    uint lastFgPid = (uint)Marshal.ReadInt32(pBuf, 68);
                    if (lastFgPid == fgPid)
                    {
                        return true;
                    }

                    uint appEntrySize = (uint)Marshal.ReadInt32(pBuf, 8);
                    uint appArrOffset = (uint)Marshal.ReadInt32(pBuf, 12);
                    uint appArrSize = (uint)Marshal.ReadInt32(pBuf, 16);

                    if (appEntrySize == 0 || appArrSize == 0 || appArrOffset == 0)
                    {
                        return false;
                    }

                    for (int i = 0; i < appArrSize; i++)
                    {
                        int entryOffset = (int)(appArrOffset + i * appEntrySize);
                        uint entryPid = (uint)Marshal.ReadInt32(pBuf, entryOffset);
                        if (entryPid == fgPid)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    UnmapViewOfFile(pBuf);
                }
            }
            finally
            {
                CloseHandle(hMap);
            }
        }

        private bool EnsureInitialized(out string? error)
        {
            error = null;
            if (_module != IntPtr.Zero && _setFlags is not null && _getFlags is not null)
            {
                return true;
            }

            ResetLoadedModule();

            foreach (string installDir in GetCandidateInstallDirs())
            {
                string dllPath = Path.Combine(installDir, Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
                _log($"RTSS OSD: tentando inicializar via '{dllPath}'.");

                if (!File.Exists(dllPath))
                {
                    error = $"DLL nao encontrada: {dllPath}";
                    _log($"RTSS OSD: {error}");
                    continue;
                }

                IntPtr module = LoadLibraryW(dllPath);
                if (module == IntPtr.Zero)
                {
                    error = $"LoadLibrary falhou para {dllPath}. Win32Error={Marshal.GetLastWin32Error()}";
                    _log($"RTSS OSD: {error}");
                    continue;
                }

                if (!ValidateRtssCliExports(module, dllPath, out error) ||
                    !TryGetDelegate(module, "SetFlags", out SetFlagsDelegate? setFlags, out error) ||
                    setFlags is null ||
                    !TryGetDelegate(module, "GetFlags", out GetFlagsDelegate? getFlags, out error) ||
                    getFlags is null)
                {
                    _log($"RTSS OSD: {error}");
                    FreeLibrary(module);
                    continue;
                }

                _module = module;
                _modulePath = dllPath;
                _setFlags = setFlags;
                _getFlags = getFlags;

                IntPtr setFlagsPtr = GetProcAddress(module, "SetFlags");
                IntPtr getFlagsPtr = GetProcAddress(module, "GetFlags");
                uint initialFlags = _getFlags();
                _log(
                    "RTSS OSD: interface inicializada no padrao rtss-cli. " +
                    $"dll='{dllPath}', module=0x{module.ToInt64():X}, " +
                    $"SetFlags=0x{setFlagsPtr.ToInt64():X}, GetFlags=0x{getFlagsPtr.ToInt64():X}, flags=0x{initialFlags:X8}");
                return true;
            }

            error ??= "nao foi possivel inicializar RTSSHooks pelo registro/fallback.";
            return false;
        }

        private static bool ValidateRtssCliExports(IntPtr module, string dllPath, out string? error)
        {
            string[] exports =
            {
                "EnumProfiles",
                "LoadProfile",
                "SaveProfile",
                "GetProfileProperty",
                "SetProfileProperty",
                "DeleteProfile",
                "ResetProfile",
                "UpdateProfiles",
                "SetFlags",
                "GetFlags"
            };

            foreach (string exportName in exports)
            {
                if (GetProcAddress(module, exportName) == IntPtr.Zero)
                {
                    error = $"export obrigatorio '{exportName}' ausente em {dllPath}. Win32Error={Marshal.GetLastWin32Error()}";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private void ResetLoadedModule()
        {
            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
            }

            _module = IntPtr.Zero;
            _modulePath = null;
            _setFlags = null;
            _getFlags = null;
        }

        private static bool TryGetDelegate<TDelegate>(
            IntPtr module,
            string exportName,
            out TDelegate? value,
            out string? error)
            where TDelegate : Delegate
        {
            value = null;
            error = null;

            IntPtr proc = GetProcAddress(module, exportName);
            if (proc == IntPtr.Zero)
            {
                error = $"export {exportName} nao encontrado. Win32Error={Marshal.GetLastWin32Error()}";
                return false;
            }

            value = Marshal.GetDelegateForFunctionPointer<TDelegate>(proc);
            return true;
        }

        private static IEnumerable<string> GetCandidateInstallDirs()
        {
            string? installDir = ReadInstallDirFromRegistry();
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                yield return installDir;
            }

            yield return @"C:\Program Files (x86)\RivaTuner Statistics Server";
        }

        private static string? ReadInstallDirFromRegistry()
        {
            try
            {
                object? value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Unwinder\RTSS",
                    "InstallDir",
                    null);

                return value as string;
            }
            catch
            {
                return null;
            }
        }

        private static void PostUpdateSettings()
        {
            IntPtr hwnd = FindWindowW("RTSSWndClass", null);
            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindWindowW(null, "RTSS");
            }

            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindWindowW(null, "RivaTuner Statistics Server");
            }

            if (hwnd == IntPtr.Zero)
            {
                IntPtr hDesk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_ENUMERATE */);
                if (hDesk != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr targetHwnd = IntPtr.Zero;
                        EnumDesktopWindows(hDesk, (wnd, _) =>
                        {
                            var sb = new StringBuilder(256);
                            GetWindowTextW(wnd, sb, 256);
                            string title = sb.ToString();
                            if (title == "RTSS" || title == "RivaTunerStatisticsServer")
                            {
                                targetHwnd = wnd;
                                return false;
                            }
                            return true;
                        }, IntPtr.Zero);
                        hwnd = targetHwnd;
                    }
                    finally
                    {
                        CloseDesktop(hDesk);
                    }
                }
            }

            if (hwnd != IntPtr.Zero)
            {
                PostMessageW(hwnd, WmRtssUpdateSettings, IntPtr.Zero, IntPtr.Zero);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SetFlagsDelegate(uint andMask, uint xorMask);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetFlagsDelegate();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    }
}
