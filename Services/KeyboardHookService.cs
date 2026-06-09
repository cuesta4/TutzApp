// Services/KeyboardHookService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TutzApp.Common;

namespace TutzApp.Services
{
    public class KeyboardHookService : IKeyboardHookService, IDisposable
    {
        private readonly ISystemControlService _sysControl;
        private static readonly IntPtr SimulatedKeySignature = new IntPtr(0x12345);

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _hookProc;
        private Thread? _hookThread;
        private uint _hookThreadId = 0;
        private volatile bool _hookInstalled = false;
        private bool _physicalAltDown = false;
        private bool _interceptedAltDown = false;
        private bool _replayedAltDown = false;
        private uint _suppressedTextShortcutKey = 0;
        private readonly HashSet<uint> _consumedAltShortcutKeys = new();
        private readonly HashSet<uint> _replayedAltKeys = new();

        public event Action? HelpRequested;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        public KeyboardHookService(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
        }

        public void StartHook()
        {
            if (_hookId != IntPtr.Zero) return;

            ResetState();
            _hookProc = HookCallback; // Mantém o delegate vivo na memória do GC
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
                        _sysControl.LogDebug("KeyboardHookService: Hook de teclado instalado com sucesso na thread STA.");
                    }
                    else
                    {
                        _sysControl.LogDebug($"KeyboardHookService: Falha ao instalar hook de teclado. Win32Error={Marshal.GetLastWin32Error()}");
                    }
                    sem.Release();

                    // Loop de mensagens nativo Win32
                    var msg = new NativeMethods.MSG();
                    while (_hookInstalled && NativeMethods.GetMessage(ref msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        NativeMethods.TranslateMessage(ref msg);
                        NativeMethods.DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"KeyboardHookService: Erro fatal na thread do Hook: {ex.Message}");
                }
                finally
                {
                    Unhook();
                }
            });

            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.IsBackground = true;
            _hookThread.Start();

            // Aguarda o hook ser instalado
            sem.Wait(1000);
        }

        public void StopHook()
        {
            _sysControl.LogDebug("KeyboardHookService: Parando hook de teclado...");
            if (_hookThreadId != 0)
            {
                // Envia mensagem WM_QUIT para encerrar o loop de mensagens da thread do hook
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
                _sysControl.LogDebug("KeyboardHookService: Hook de teclado desinstalado.");
            }
            else
            {
                _hookInstalled = false;
            }
        }

        private bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private void ResetState()
        {
            _physicalAltDown = false;
            _interceptedAltDown = false;
            _replayedAltDown = false;
            _suppressedTextShortcutKey = 0;
            _consumedAltShortcutKeys.Clear();
            _replayedAltKeys.Clear();
        }

        private bool IsCs2Active()
        {
            string? name = _sysControl.GetForegroundProcessName();
            return name != null && name.Equals("cs2", StringComparison.OrdinalIgnoreCase);
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
                bool cs2Active = IsCs2Active();

                if (IsAltKey(vk))
                {
                    if (cs2Active)
                    {
                        if (isKeyDown)
                        {
                            _physicalAltDown = true;
                            _suppressedTextShortcutKey = 0;
                        }

                        if (isKeyUp)
                        {
                            _physicalAltDown = false;
                            _suppressedTextShortcutKey = 0;
                            _interceptedAltDown = false;
                            _replayedAltDown = false;
                            _replayedAltKeys.Clear();
                        }

                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    if (isKeyDown)
                    {
                        _physicalAltDown = true;
                        _suppressedTextShortcutKey = 0;
                        // Fora do CS2 o Alt físico precisa passar para hooks globais como RTSS/RivaTuner.
                        // Os atalhos próprios do app consomem apenas a tecla seguinte (A/S/Q/E/N/D etc.).
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    if (isKeyUp)
                    {
                        _physicalAltDown = false;
                        _suppressedTextShortcutKey = 0;

                        if (_replayedAltDown)
                        {
                            SendKeysSimulated((0x12, true));
                        }

                        if (_interceptedAltDown)
                        {
                            _interceptedAltDown = false;
                            _replayedAltDown = false;
                            return new IntPtr(1);
                        }

                        _replayedAltDown = false;
                    }
                }

                if (isKeyUp)
                {
                    if (_suppressedTextShortcutKey != 0 && vk == _suppressedTextShortcutKey)
                    {
                        _sysControl.LogDebug($"Hook: KeyUp 0x{vk:X2} consumido após atalho de texto");
                        _suppressedTextShortcutKey = 0;
                        return new IntPtr(1);
                    }

                    if (_replayedAltKeys.Remove(vk))
                    {
                        SendKeysSimulated(((ushort)vk, true));
                        return new IntPtr(1);
                    }

                    if (_consumedAltShortcutKeys.Remove(vk))
                    {
                        _sysControl.LogDebug($"Hook: KeyUp 0x{vk:X2} consumido após atalho Alt");
                        return new IntPtr(1);
                    }
                }



                if (isKeyDown)
                {
                    bool alt = _physicalAltDown || (kbd.flags & 0x20) != 0 || IsKeyDown(0x12);
                    bool ctrl = IsKeyDown(0x11);
                    bool shift = IsKeyDown(0x10);
                    bool win = IsKeyDown(0x5B) || IsKeyDown(0x5C);

                    _sysControl.LogDebug($"Hook KeyDown: vk=0x{vk:X2}, alt={alt}, ctrl={ctrl}, shift={shift}, win={win}");

                    // Alt+Esc precisa sempre funcionar para sair do CS2 sem fechar o jogo.
                    if (alt && vk == 0x1B && !ctrl && !shift && !win)
                    {
                        if (_interceptedAltDown)
                        {
                            _sysControl.LogDebug("Hook: Alt+Esc -> reenviando combo e liberando interceptação de Alt");
                            QueueHookAction(() => SendKeyCombination(new ushort[] { 0x12 }, 0x1B));
                            _replayedAltDown = false;
                            _replayedAltKeys.Clear();
                            _consumedAltShortcutKeys.Add(vk);
                            return new IntPtr(1);
                        }

                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Se o overlay de teclado estiver aberto, intercepta F1 (0x70) e Esc (0x1B) para fechá-lo
                    if (_sysControl.IsKeyboardShortcutOverlayVisible)
                    {
                        if ((vk == 0x70 || vk == 0x1B) && !alt && !ctrl && !shift && !win)
                        {
                            _sysControl.LogDebug("Hook: F1 ou Esc pressionado enquanto overlay de teclado aberto -> Fechando");
                            ThreadPool.QueueUserWorkItem(_ => _sysControl.HideKeyboardShortcutOverlay());
                            return new IntPtr(1); // Absorve a tecla
                        }
                    }

                    // Se o overlay de gamepad estiver aberto, intercepta F1 (0x70) e Esc (0x1B) para fechá-lo
                    if (_sysControl.IsGamepadShortcutOverlayVisible)
                    {
                        if ((vk == 0x70 || vk == 0x1B) && !alt && !ctrl && !shift && !win)
                        {
                            _sysControl.LogDebug("Hook: F1 ou Esc pressionado enquanto overlay de gamepad aberto -> Fechando");
                            ThreadPool.QueueUserWorkItem(_ => _sysControl.HideGamepadShortcutOverlay());
                            return new IntPtr(1); // Absorve a tecla
                        }
                    }

                    if (cs2Active)
                    {
                        // No CS2, a maioria dos atalhos do app não deve rodar (incluindo Alt+D, Alt+N, etc.).
                        // Bloqueamos apenas as teclas que devem ser suprimidas ativamente: Alt+Tab e Alt+1..7.
                        if (alt && vk == 0x09) // Alt+Tab (VK_TAB = 0x09)
                        {
                            _sysControl.LogDebug("Hook: Alt+Tab bloqueado (CS2 ativo)");
                            _consumedAltShortcutKeys.Add(vk);
                            return new IntPtr(1); // Absorve
                        }

                        if (alt && vk >= 0x31 && vk <= 0x37) // Alt+1..7
                        {
                            _sysControl.LogDebug($"Hook: Alt+{vk - 0x30} bloqueado (CS2 ativo)");
                            _consumedAltShortcutKeys.Add(vk);
                            return new IntPtr(1); // Absorve
                        }

                        // Todo o resto passa direto para o jogo (incluindo Alt e D ao mesmo tempo)
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Alt+R precisa chegar ao RivaTuner/RTSS com o R físico. Recriamos só o Alt
                    // que foi suprimido no KeyDown e deixamos o R seguir pela cadeia de hooks.
                    if (alt && vk == 0x52 && !ctrl && !shift && !win) // Alt+R
                    {
                        if (_interceptedAltDown && !_replayedAltDown)
                        {
                            _sysControl.LogDebug("Hook: Alt+R -> passthrough físico para RivaTuner");
                            SendKeysSimulated((0x12, false));
                            _replayedAltDown = true;
                        }

                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    if (TryHandleTextShortcut(alt, ctrl, shift, win, vk))
                    {
                        return new IntPtr(1);
                    }

                    // --- FORA DO CS2 ---

                    // Alt+1..7 muda resolução
                    if (alt && !ctrl && !shift && !win && vk >= 0x31 && vk <= 0x37)
                    {
                        int index = (int)(vk - 0x30);
                        string indexStr = index.ToString();
                        _sysControl.LogDebug($"Hook: Resolvendo Alt+{indexStr} fora do jogo");

                        if (_sysControl.Config.Resolutions.TryGetValue(indexStr, out var resInfo))
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                _sysControl.ChangeResolution(resInfo.Width, resInfo.Height, resInfo.RefreshRate);
                            });
                        }
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1); // Absorve
                    }

                    // F1 abre atalhos
                    if (vk == 0x70 && !alt && !ctrl && !shift && !win) // F1
                    {
                        _sysControl.LogDebug("Hook: F1 pressionado -> Exibindo atalhos");
                        ThreadPool.QueueUserWorkItem(_ => HelpRequested?.Invoke());
                        return new IntPtr(1);
                    }

                    // Alt+D -> Fechar área virtual (Win+Ctrl+F4)
                    if (alt && vk == 0x44 && !ctrl && !shift && !win) // Alt+D
                    {
                        _sysControl.LogDebug("Hook: Alt+D -> Fechar desktop virtual");
                        QueueHookAction(() =>
                        {
                            RunWithAltTemporarilyNeutralized(() =>
                                SendKeyCombination(new ushort[] { 0x5B, 0x11 }, 0x73)); // Win + Ctrl + F4
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Atalhos Gerais

                    // Ctrl+Alt+K -> Teclado virtual
                    if (ctrl && alt && vk == 0x4B && !shift && !win) // Ctrl+Alt+K
                    {
                        _sysControl.LogDebug("Hook: Ctrl+Alt+K -> Chamando teclado virtual");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.InvokeTouchKeyboard());
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+Home -> Alternar perfil do teclado
                    if (alt && vk == 0x24 && !ctrl && !shift && !win) // Alt+Home
                    {
                        _sysControl.LogDebug("Hook: Alt+Home -> Trocar perfil");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.ToggleKeyboardProfile());
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+F10 -> Desligar Monitor
                    if (alt && vk == 0x79 && !ctrl && !shift && !win) // Alt+F10
                    {
                        _sysControl.LogDebug("Hook: Alt+F10 -> Desligar monitor");
                        QueueHookAction(_sysControl.PowerOffMonitor);
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+F11 -> Twinkle Tray Brilho -25
                    if (alt && vk == 0x7A && !ctrl && !shift && !win) // Alt+F11
                    {
                        _sysControl.LogDebug("Hook: Alt+F11 -> Brilho -25");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.AdjustBrightness(-25));
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+F12 -> Twinkle Tray Brilho +25
                    if (alt && vk == 0x7B && !ctrl && !shift && !win) // Alt+F12
                    {
                        _sysControl.LogDebug("Hook: Alt+F12 -> Brilho +25");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.AdjustBrightness(25));
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Win+Alt+1 -> Velocidade Mouse Rápida (14)
                    if (win && alt && vk == 0x31 && !ctrl && !shift) // Win+Alt+1
                    {
                        _sysControl.LogDebug("Hook: Win+Alt+1 -> Velocidade do mouse alta");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.SetMouseSpeed(_sysControl.Config.Mouse.HighSpeed));
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Win+Alt+2 -> Velocidade Mouse Lenta (3)
                    if (win && alt && vk == 0x32 && !ctrl && !shift) // Win+Alt+2
                    {
                        _sysControl.LogDebug("Hook: Win+Alt+2 -> Velocidade do mouse lenta");
                        ThreadPool.QueueUserWorkItem(_ => _sysControl.SetMouseSpeed(_sysControl.Config.Mouse.SlowSpeed));
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Edição de texto

                    // Alt+Left -> Home
                    if (alt && vk == 0x25 && !ctrl && !win)
                    {
                        if (shift)
                        {
                            QueueHookAction(() => SendKeysSimulated(
                                (0x10, false), (0x24, false), (0x24, true), (0x10, true) // Shift+Home
                            ));
                        }
                        else
                        {
                            QueueHookAction(() => SendKeysSimulated(
                                (0x24, false), (0x24, true) // Home
                            ));
                        }
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+Right -> End
                    if (alt && vk == 0x27 && !ctrl && !win)
                    {
                        if (shift)
                        {
                            QueueHookAction(() => SendKeysSimulated(
                                (0x10, false), (0x23, false), (0x23, true), (0x10, true) // Shift+End
                            ));
                        }
                        else
                        {
                            QueueHookAction(() => SendKeysSimulated(
                                (0x23, false), (0x23, true) // End
                            ));
                        }
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+Backspace -> Shift+Home + Del
                    if (alt && vk == 0x08 && !ctrl && !shift && !win)
                    {
                        QueueHookAction(() => {
                            SendKeysSimulated((0x10, false), (0x24, false), (0x24, true), (0x10, true)); // Shift+Home
                            Thread.Sleep(5);
                            SendKeysSimulated((0x2E, false), (0x2E, true)); // Del
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+Delete -> Shift+End + Del
                    if (alt && vk == 0x2E && !ctrl && !shift && !win)
                    {
                        QueueHookAction(() => {
                            SendKeysSimulated((0x10, false), (0x23, false), (0x23, true), (0x10, true)); // Shift+End
                            Thread.Sleep(5);
                            SendKeysSimulated((0x2E, false), (0x2E, true)); // Del
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Controle de desktops virtuais

                    // Alt+N -> Criar Desktop (Win+Ctrl+D)
                    if (alt && vk == 0x4E && !ctrl && !shift && !win)
                    {
                        QueueHookAction(() => {
                            RunWithAltTemporarilyNeutralized(() =>
                                SendKeyCombination(new ushort[] { 0x5B, 0x11 }, 0x44)); // Win+Ctrl+D
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+Q -> Mudar Desktop Esquerda (Win+Ctrl+Left)
                    if (alt && vk == 0x51 && !ctrl && !shift && !win)
                    {
                        QueueHookAction(() => {
                            RunWithAltTemporarilyNeutralized(() =>
                                SendKeyCombination(new ushort[] { 0x5B, 0x11 }, 0x25)); // Win+Ctrl+Left
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    // Alt+E -> Mudar Desktop Direita (Win+Ctrl+Right)
                    if (alt && vk == 0x45 && !ctrl && !shift && !win)
                    {
                        QueueHookAction(() => {
                            RunWithAltTemporarilyNeutralized(() =>
                                SendKeyCombination(new ushort[] { 0x5B, 0x11 }, 0x27)); // Win+Ctrl+Right
                        });
                        TrackConsumedAltShortcut(vk);
                        return new IntPtr(1);
                    }

                    if (_interceptedAltDown)
                    {
                        ReplayAltComboKeyDown(vk);
                        return new IntPtr(1);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void QueueHookAction(Action action)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _sysControl.LogDebug($"Hook: Exception ao executar ação em background: {ex.Message}");
                }
            });
        }

        private static bool IsAltKey(uint vk)
        {
            return vk == 0x12 || vk == 0xA4 || vk == 0xA5; // VK_MENU, VK_LMENU, VK_RMENU
        }

        private void TrackConsumedAltShortcut(uint vk)
        {
            _consumedAltShortcutKeys.Add(vk);
        }

        private void ReplayAltComboKeyDown(uint vk)
        {
            bool needsAltDown = !_replayedAltDown;
            _replayedAltDown = true;
            _replayedAltKeys.Add(vk);
            if (needsAltDown)
            {
                SendKeysSimulated((0x12, false));
            }
            SendKeysSimulated(((ushort)vk, false));
        }

        private bool TryHandleTextShortcut(bool alt, bool ctrl, bool shift, bool win, uint vk)
        {
            if (!alt || ctrl || shift || win)
            {
                return false;
            }

            if (vk == 0x41) // Alt+A
            {
                _sysControl.LogDebug("Hook: Alt+A -> Barra invertida (\\)");
                SendTextShortcut("\\", vk);
                return true;
            }

            if (vk == 0x53) // Alt+S
            {
                _sysControl.LogDebug("Hook: Alt+S -> Barra vertical (|)");
                SendTextShortcut("|", vk, preferActiveLayout: true);
                return true;
            }

            return false;
        }

        private void SendTextShortcut(string text, uint triggerVk, bool preferActiveLayout = false)
        {
            _suppressedTextShortcutKey = triggerVk;
            QueueHookAction(() => RunWithAltTemporarilyNeutralized(() =>
            {
                if (preferActiveLayout)
                {
                    SendTextUsingActiveLayout(text);
                }
                else
                {
                    SendUnicodeText(text);
                }
            }));
        }

        private void RunWithAltTemporarilyNeutralized(Action action)
        {
            SendKeysSimulated((0x12, true)); // Clear the foreground app's logical Alt state.
            _replayedAltDown = false;
            Thread.Sleep(2);
            action();
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
                    _sysControl.LogDebug($"Hook: SendInput simulado incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Hook: Exception ao simular teclas: {ex.Message}");
            }
        }

        private void SendTextUsingActiveLayout(string text)
        {
            foreach (char c in text)
            {
                if (!TrySendCharUsingActiveLayout(c))
                {
                    SendUnicodeText(c.ToString());
                }
            }
        }

        private bool TrySendCharUsingActiveLayout(char c)
        {
            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                uint threadId = hwnd != IntPtr.Zero
                    ? NativeMethods.GetWindowThreadProcessId(hwnd, out _)
                    : 0;
                IntPtr layout = GetKeyboardLayout(threadId);
                short keyScan = VkKeyScanEx(c, layout);
                if (keyScan == -1)
                {
                    return false;
                }

                ushort vk = (ushort)(keyScan & 0xFF);
                int shiftState = (keyScan >> 8) & 0xFF;
                var modifiers = new List<ushort>();
                if ((shiftState & 1) != 0) modifiers.Add(0x10); // Shift
                if ((shiftState & 2) != 0) modifiers.Add(0x11); // Ctrl
                if ((shiftState & 4) != 0) modifiers.Add(0x12); // Alt / AltGr

                SendKeyCombination(modifiers.ToArray(), vk);
                return true;
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Hook: Exception ao mapear caractere '{c}' pelo layout ativo: {ex.Message}");
                return false;
            }
        }

        private void SendUnicodeText(string text)
        {
            try
            {
                var inputs = new NativeMethods.INPUT[text.Length * 2];
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    inputs[i * 2].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[i * 2].U.ki.wScan = c;
                    inputs[i * 2].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;
                    inputs[i * 2].U.ki.dwExtraInfo = SimulatedKeySignature;

                    inputs[i * 2 + 1].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[i * 2 + 1].U.ki.wScan = c;
                    inputs[i * 2 + 1].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
                    inputs[i * 2 + 1].U.ki.dwExtraInfo = SimulatedKeySignature;
                }
                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != inputs.Length)
                {
                    _sysControl.LogDebug($"Hook: SendInput unicode incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Hook: Exception ao simular texto unicode: {ex.Message}");
            }
        }

        private void SendKeyCombination(ushort[] modifiers, ushort key)
        {
            try
            {
                int eventCount = (modifiers.Length + 1) * 2;
                var inputs = new NativeMethods.INPUT[eventCount];

                for (int i = 0; i < modifiers.Length; i++)
                {
                    inputs[i].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[i].U.ki.wVk = modifiers[i];
                    inputs[i].U.ki.dwFlags = IsExtendedKey(modifiers[i]) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
                    inputs[i].U.ki.dwExtraInfo = SimulatedKeySignature;
                }

                inputs[modifiers.Length].type = NativeMethods.INPUT_KEYBOARD;
                inputs[modifiers.Length].U.ki.wVk = key;
                inputs[modifiers.Length].U.ki.dwFlags = IsExtendedKey(key) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
                inputs[modifiers.Length].U.ki.dwExtraInfo = SimulatedKeySignature;

                inputs[modifiers.Length + 1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[modifiers.Length + 1].U.ki.wVk = key;
                inputs[modifiers.Length + 1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP |
                    (IsExtendedKey(key) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
                inputs[modifiers.Length + 1].U.ki.dwExtraInfo = SimulatedKeySignature;

                for (int i = 0; i < modifiers.Length; i++)
                {
                    inputs[modifiers.Length + 2 + i].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[modifiers.Length + 2 + i].U.ki.wVk = modifiers[modifiers.Length - 1 - i];
                    inputs[modifiers.Length + 2 + i].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP |
                        (IsExtendedKey(modifiers[modifiers.Length - 1 - i]) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
                    inputs[modifiers.Length + 2 + i].U.ki.dwExtraInfo = SimulatedKeySignature;
                }

                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                if (sent != inputs.Length)
                {
                    _sysControl.LogDebug($"Hook: SendInput combinação incompleto. sent={sent}/{inputs.Length}, Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"Hook: Exception ao simular combinação de teclas: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopHook();
        }

        private static bool IsExtendedKey(ushort vk)
        {
            return vk == 0x5B || vk == 0x5C || vk == 0x21 || vk == 0x22 ||
                vk == 0x23 || vk == 0x24 || vk == 0x25 || vk == 0x26 ||
                vk == 0x27 || vk == 0x28 || vk == 0x2D || vk == 0x2E;
        }
    }
}
