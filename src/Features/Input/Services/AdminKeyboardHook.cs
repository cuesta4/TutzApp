using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
        private bool _forceKillShortcutDown = false;
        private bool _adminTerminalShortcutDown = false;
        private readonly object _winAltSpaceLock = new();
        private uint _winVkCode = 0;

        private const uint VK_CONTROL = 0x11;
        private const uint VK_SHIFT = 0x10;
        private const uint VK_MENU = 0x12;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;
        private const uint VK_LMENU = 0xA4;
        private const uint VK_RMENU = 0xA5;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_T = 0x54;
        private const uint VK_F4 = 0x73;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public AdminKeyboardHook(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
        }

        public bool IsHookInstalled => _hookInstalled && _hookId != IntPtr.Zero;

        public event Action? EscapePressed;

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
            _forceKillShortcutDown = false;
            _adminTerminalShortcutDown = false;
            _winVkCode = 0;
        }

        private static bool IsKeyDown(uint vKey)
        {
            return (GetAsyncKeyState((int)vKey) & 0x8000) != 0;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return HookCallbackCore(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                // O helper também usa um callback nativo: nunca deixar uma exceção atravessar o Win32.
                _sysControl.LogDebug($"AdminKeyboardHook: Exception protegida no callback nativo: {ex}");
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }
        }

        private IntPtr HookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
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

                if (isKeyDown && vk == VK_ESCAPE)
                {
                    _sysControl.LogDebug("AdminKeyboardHook: Escape observado; notificando encerramento potencial do OSK.");
                    try
                    {
                        EscapePressed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"AdminKeyboardHook: erro ao notificar Escape: {ex.Message}");
                    }
                }

                if (isKeyUp && vk == VK_F4)
                {
                    _forceKillShortcutDown = false;
                }

                if (isKeyUp && vk == VK_T && _adminTerminalShortcutDown)
                {
                    _adminTerminalShortcutDown = false;
                    _sysControl.LogDebug("AdminKeyboardHook: KeyUp Win+T consumido após abrir terminal");
                    return new IntPtr(1);
                }

                if (isKeyDown && vk == VK_T)
                {
                    bool ctrlDownForTerminal = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
                    bool shiftDownForTerminal = IsKeyDown(VK_SHIFT);
                    bool altDownForTerminal = IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);
                    bool winDownForTerminal = _winKeyPressed || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);

                    if (winDownForTerminal && !shiftDownForTerminal && !altDownForTerminal)
                    {
                        _otherKeyPressedDuringWin = true;
                        if (!_adminTerminalShortcutDown)
                        {
                            _adminTerminalShortcutDown = true;
                            if (ctrlDownForTerminal)
                            {
                                QueueOpenTerminalAdmin("AdminKeyboardHook: Win+Ctrl+T -> Abrir Windows Terminal admin");
                            }
                            else
                            {
                                QueueOpenTerminalNormalViaMainApp("AdminKeyboardHook: Win+T -> Abrir Windows Terminal via processo principal");
                            }
                        }
                        return new IntPtr(1);
                    }
                }

                if (isKeyDown && vk == VK_F4 && !_forceKillShortcutDown)
                {
                    bool ctrlDown = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
                    bool leftAltDown = IsKeyDown(VK_LMENU);

                    if (ctrlDown && leftAltDown)
                    {
                        _forceKillShortcutDown = true;
                        QueueForceKillForegroundAppDirect("AdminKeyboardHook: Ctrl+Alt+F4 -> ForceKillForegroundAppDirect");
                        return new IntPtr(1);
                    }
                }
                else if (isKeyDown && vk == VK_F4 && _forceKillShortcutDown)
                {
                    return new IntPtr(1);
                }

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

        private void QueueForceKillForegroundAppDirect(string logMessage)
        {
            _sysControl.LogDebug(logMessage);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _sysControl.ForceKillForegroundAppDirect();
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: erro ao executar ForceKillForegroundAppDirect: {ex}");
                }
            });
        }

        private void QueueOpenTerminalNormalViaMainApp(string logMessage)
        {
            _sysControl.LogDebug(logMessage);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!TrySendCommandToMainApp("open-terminal|"))
                    {
                        _sysControl.LogDebug("AdminKeyboardHook: falha ao enviar Win+T ao processo principal; terminal normal não foi aberto para evitar herança de elevação.");
                    }
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: erro ao solicitar terminal normal ao processo principal: {ex}");
                }
            });
        }

        private static bool TrySendCommandToMainApp(string command)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "TutzApp.CommandPipe", PipeDirection.InOut);
                client.Connect(700);
                using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(command);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void QueueOpenTerminalAdmin(string logMessage)
        {
            _sysControl.LogDebug(logMessage);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _sysControl.OpenTerminalAdmin();
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: erro ao abrir terminal admin: {ex}");
                }
            });
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
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"AdminKeyboardHook: Exception protegida ao executar Win+Alt+Space em background: {ex}");
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
