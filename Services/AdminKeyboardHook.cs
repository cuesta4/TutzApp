// Services/AdminKeyboardHook.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TutzApp.Common;

namespace TutzApp.Services
{
    public class AdminKeyboardHook : IDisposable
    {
        private readonly ISystemControlService _sysControl;
        private static readonly IntPtr SimulatedKeySignature = new IntPtr(0x12345);

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _hookProc;
        private Thread? _hookThread;
        private uint _hookThreadId = 0;
        private volatile bool _hookInstalled = false;

        private bool _winKeyPressed = false;
        private bool _otherKeyPressedDuringWin = false;
        private bool _winKeyDownSuppressed = false;
        private volatile bool _winAltSpaceInProgress = false;
        private readonly object _winAltSpaceLock = new();
        private uint _winVkCode = 0;

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public AdminKeyboardHook(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
        }

        public bool IsHookInstalled => _hookInstalled && _hookId != IntPtr.Zero;

        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            ResetState();
            _hookProc = HookCallback;
            var sem = new SemaphoreSlim(0);

            _hookThread = new Thread(() =>
            {
                try
                {
                    _hookThreadId = GetCurrentThreadId();
                    _hookId = SetHook(_hookProc);
                    _hookInstalled = _hookId != IntPtr.Zero;
                    if (_hookInstalled)
                    {
                        _sysControl.LogDebug("AdminKeyboardHook: Hook instalado com sucesso na thread STA.");
                    }
                    else
                    {
                        _sysControl.LogDebug($"AdminKeyboardHook: Falha ao instalar hook. Win32Error={Marshal.GetLastWin32Error()}");
                    }
                    sem.Release();

                    var msg = new NativeMethods.MSG();
                    while (_hookInstalled && NativeMethods.GetMessage(ref msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        NativeMethods.TranslateMessage(ref msg);
                        NativeMethods.DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: Erro na thread do hook: {ex.Message}");
                }
                finally
                {
                    Unhook();
                }
            });

            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.IsBackground = true;
            _hookThread.Start();

            sem.Wait(1000);
        }

        public void Stop()
        {
            _sysControl.LogDebug("AdminKeyboardHook: Desinstalando hook...");
            if (_hookThreadId != 0)
            {
                NativeMethods.PostThreadMessage(_hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT = 0x0012
            }

            if (_hookThread != null && _hookThread.IsAlive)
            {
                _hookThread.Join(1000);
            }

            ResetState();
            _hookThread = null;
            _hookThreadId = 0;
        }

        private IntPtr SetHook(NativeMethods.LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if (curModule != null)
                {
                    return NativeMethods.SetWindowsHookEx(
                        NativeMethods.WH_KEYBOARD_LL,
                        proc,
                        NativeMethods.GetModuleHandle(curModule.ModuleName),
                        0
                    );
                }
                return IntPtr.Zero;
            }
        }

        private void Unhook()
        {
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _hookProc = null;
                _hookInstalled = false;
                _sysControl.LogDebug("AdminKeyboardHook: Hook desinstalado.");
            }
            else
            {
                _hookInstalled = false;
            }
        }

        private bool IsCs2Active()
        {
            string? name = _sysControl.GetForegroundProcessName();
            return name != null && name.Equals("cs2", StringComparison.OrdinalIgnoreCase);
        }

        private void ResetState()
        {
            _winKeyPressed = false;
            _otherKeyPressedDuringWin = false;
            _winKeyDownSuppressed = false;
            _winAltSpaceInProgress = false;
            _winVkCode = 0;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                // Evitar loops com nossas próprias teclas simuladas
                if (kbd.dwExtraInfo == SimulatedKeySignature)
                {
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                uint vk = kbd.vkCode;
                bool isKeyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;

                // Monitoramento da tecla Windows para soltar sozinho (Win+Alt+Space)
                if (vk == 0x5B || vk == 0x5C) // LWIN ou RWIN
                {
                    if (_winAltSpaceInProgress)
                    {
                        _winKeyPressed = false;
                        _otherKeyPressedDuringWin = false;
                        _winKeyDownSuppressed = false;
                        _winVkCode = 0;
                        return new IntPtr(1);
                    }

                    if (isKeyDown)
                    {
                        _winKeyPressed = true;
                        _winVkCode = vk;
                        _otherKeyPressedDuringWin = false;
                        _winKeyDownSuppressed = true;
                        return new IntPtr(1);
                    }
                    else if (isKeyUp)
                    {
                        if (vk == _winVkCode)
                        {
                            _winKeyPressed = false;
                            bool releasedAlone = !_otherKeyPressedDuringWin;
                            _winVkCode = 0; // reset

                            if (releasedAlone)
                            {
                                _sysControl.LogDebug($"AdminKeyboardHook: Tecla Windows solta sozinha (vk={vk:X2}) -> Enviando Win+Alt+Space a partir do helper administrativo");

                                _winKeyDownSuppressed = false;
                                _winVkCode = 0;
                                QueueWinAltSpaceByScanCode(vk == 0x5C);
                                return new IntPtr(1); // Absorve a liberação nativa do Win para não abrir o Iniciar.
                            }

                            if (_winKeyDownSuppressed)
                            {
                                _winKeyDownSuppressed = false;
                                _winVkCode = 0;
                                return new IntPtr(1);
                            }
                        }
                        else if (_winVkCode == 0)
                        {
                            _winKeyPressed = false;
                        }
                    }
                }
                else
                {
                    if (_winKeyPressed && isKeyDown)
                    {
                        _otherKeyPressedDuringWin = true;
                        if (_winKeyDownSuppressed && _winVkCode != 0)
                        {
                            SendKeysSimulated(((ushort)_winVkCode, false));
                            _winKeyDownSuppressed = false;
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void QueueWinAltSpaceByScanCode(bool rightWin)
        {
            _winAltSpaceInProgress = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (_winAltSpaceLock)
                    {
                        Thread.Sleep(15);
                        SendWinAltSpaceByScanCode(rightWin);
                        Thread.Sleep(180);
                    }
                }
                finally
                {
                    _winAltSpaceInProgress = false;
                }
            });
        }

        private void SendWinAltSpaceByScanCode(bool rightWin)
        {
            try
            {
                ushort winScan = rightWin ? (ushort)0x5C : (ushort)0x5B;
                var inputs = new NativeMethods.INPUT[6];
                FillScanInput(inputs, 0, winScan, false, true); // Win
                FillScanInput(inputs, 1, 0x38, false, false);   // Alt
                FillScanInput(inputs, 2, 0x39, false, false);   // Space
                FillScanInput(inputs, 3, 0x39, true, false);
                FillScanInput(inputs, 4, 0x38, true, false);
                FillScanInput(inputs, 5, winScan, true, true);

                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != inputs.Length)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: SendInput Win+Alt+Space incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"AdminKeyboardHook: Exception ao enviar Win+Alt+Space por scancode: {ex.Message}");
            }
        }

        private static void FillKeyInput(NativeMethods.INPUT[] inputs, int index, ushort vk, bool up, bool extended)
        {
            inputs[index].type = NativeMethods.INPUT_KEYBOARD;
            inputs[index].U.ki.wVk = vk;
            inputs[index].U.ki.dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0)
                | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            inputs[index].U.ki.dwExtraInfo = SimulatedKeySignature;
        }

        private static void FillScanInput(NativeMethods.INPUT[] inputs, int index, ushort scan, bool up, bool extended)
        {
            inputs[index].type = NativeMethods.INPUT_KEYBOARD;
            inputs[index].U.ki.wScan = scan;
            inputs[index].U.ki.dwFlags = NativeMethods.KEYEVENTF_SCANCODE
                | (up ? NativeMethods.KEYEVENTF_KEYUP : 0)
                | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            inputs[index].U.ki.dwExtraInfo = SimulatedKeySignature;
        }

        private void SendKeysSimulated(params (ushort vk, bool up)[] keyEvents)
        {
            try
            {
                var inputs = new NativeMethods.INPUT[keyEvents.Length];
                for (int i = 0; i < keyEvents.Length; i++)
                {
                    inputs[i].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[i].U.ki.wVk = keyEvents[i].vk;
                    inputs[i].U.ki.dwFlags = (keyEvents[i].up ? NativeMethods.KEYEVENTF_KEYUP : 0) |
                        (IsExtendedKey(keyEvents[i].vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
                    inputs[i].U.ki.dwExtraInfo = SimulatedKeySignature;
                }
                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != inputs.Length)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: SendInput simulado incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"AdminKeyboardHook: Exception ao simular teclas: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static bool IsExtendedKey(ushort vk)
        {
            return vk == 0x5B || vk == 0x5C || vk == 0x21 || vk == 0x22 ||
                vk == 0x23 || vk == 0x24 || vk == 0x25 || vk == 0x26 ||
                vk == 0x27 || vk == 0x28 || vk == 0x2D || vk == 0x2E;
        }
    }
}
