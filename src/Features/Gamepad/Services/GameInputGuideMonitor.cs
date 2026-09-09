using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TutzApp.Services
{
    /// <summary>
    /// Receives the Xbox/Guide button through Windows GameInput's system-button
    /// channel. Guide is not part of the normal XInput/HID gamepad bitmask for
    /// several composite controllers, so polling those channels cannot detect it.
    /// </summary>
    internal sealed class GameInputGuideMonitor : IDisposable
    {
        private const string GameInputRedistDll = "GameInputRedist.dll";
        private const uint GameInputEnableBackgroundInput = 0x00000040;
        private const uint GameInputEnableBackgroundGuideButton = 0x00000080;
        private const uint GameInputEnableBackgroundShareButton = 0x00000100;
        private const uint GameInputSystemButtonGuide = 0x00000001;
        private const uint GameInputKindGamepad = 0x00040000;
        private const uint GameInputGamepadB = 0x00000008;
        private const int RegisterSystemButtonCallbackSlot = 9;
        private const int RegisterReadingCallbackSlot = 7;
        private const int SetFocusPolicySlot = 16;
        private const int UnregisterCallbackSlot = 12;
        private const int ReleaseSlot = 2;
        private const int GetCurrentReadingSlot = 4;
        // GameInput v3: slot 18 is GetFlightStickState; GetGamepadState is 19.
        private const int GetGamepadStateSlot = 19;

        private static readonly Guid GameInputV3Iid =
            new("20EFC1C7-5D9A-43BA-B26F-B807FA48609C");

        private readonly Action<bool> _guideChanged;
        private readonly Action<bool>? _bChanged;
        private readonly Action<string>? _log;
        private readonly object _lifetimeLock = new();
        private SystemButtonCallback? _callback;
        private ReadingCallback? _readingCallback;
        private IntPtr _gameInput;
        private ulong _callbackToken;
        private ulong _readingCallbackToken;
        private bool _lastGameInputB;
        private CancellationTokenSource? _pollCancellation;
        private Task? _pollTask;
        private bool _disposed;

        public GameInputGuideMonitor(
            Action<bool> guideChanged,
            Action<bool>? bChanged = null,
            Action<string>? log = null)
        {
            _guideChanged = guideChanged;
            _bChanged = bChanged;
            _log = log;
        }

        public bool IsAvailable
        {
            get
            {
                lock (_lifetimeLock)
                {
                    return _gameInput != IntPtr.Zero && _callbackToken != 0;
                }
            }
        }

        public bool Start()
        {
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_gameInput != IntPtr.Zero && _callbackToken != 0)
                {
                    return true;
                }

                try
                {
                    Guid iid = GameInputV3Iid;
                    int hr = GameInputInitialize(ref iid, out _gameInput);
                    if (hr < 0 || _gameInput == IntPtr.Zero)
                    {
                        _gameInput = IntPtr.Zero;
                        _log?.Invoke($"GameInputGuideMonitor: GameInputInitialize falhou. HRESULT=0x{hr:X8}.");
                        return false;
                    }

                    var setFocusPolicy = GetMethod<SetFocusPolicyDelegate>(SetFocusPolicySlot);
                    setFocusPolicy(
                        _gameInput,
                        GameInputEnableBackgroundInput |
                        GameInputEnableBackgroundGuideButton |
                        GameInputEnableBackgroundShareButton);

                    _callback = OnSystemButtonChanged;
                    var register = GetMethod<RegisterSystemButtonCallbackDelegate>(RegisterSystemButtonCallbackSlot);
                    hr = register(
                        _gameInput,
                        IntPtr.Zero,
                        GameInputSystemButtonGuide,
                        IntPtr.Zero,
                        Marshal.GetFunctionPointerForDelegate(_callback),
                        out _callbackToken);
                    if (hr < 0 || _callbackToken == 0)
                    {
                        _log?.Invoke($"GameInputGuideMonitor: RegisterSystemButtonCallback falhou. HRESULT=0x{hr:X8}.");
                        _callbackToken = 0;
                        _callback = null;
                        ReleaseGameInput();
                        return false;
                    }

                    _readingCallback = OnGamepadReading;
                    var registerReading = GetMethod<RegisterReadingCallbackDelegate>(RegisterReadingCallbackSlot);
                    hr = registerReading(
                        _gameInput,
                        IntPtr.Zero,
                        GameInputKindGamepad,
                        IntPtr.Zero,
                        Marshal.GetFunctionPointerForDelegate(_readingCallback),
                        out _readingCallbackToken);
                    if (hr < 0 || _readingCallbackToken == 0)
                    {
                        _readingCallbackToken = 0;
                        _readingCallback = null;
                        _log?.Invoke(
                            $"GameInputGuideMonitor: RegisterReadingCallback(Gamepad) falhou. HRESULT=0x{hr:X8}; " +
                            "Guide continuará disponível.");
                    }
                    else
                    {
                        _log?.Invoke("GameInputGuideMonitor: callback de leitura Gamepad registrado para fallback A/B.");
                    }

                    _log?.Invoke(
                        "GameInputGuideMonitor: callback nativo do botão Guide registrado " +
                        "com entrada em background habilitada.");
                    _pollCancellation = new CancellationTokenSource();
                    _pollTask = Task.Run(
                        () => PollGamepadAsync(_pollCancellation.Token),
                        _pollCancellation.Token);
                    return true;
                }
                catch (DllNotFoundException ex)
                {
                    _log?.Invoke($"GameInputGuideMonitor: GameInputRedist.dll indisponível: {ex.Message}");
                    ReleaseGameInput();
                    return false;
                }
                catch (EntryPointNotFoundException ex)
                {
                    _log?.Invoke($"GameInputGuideMonitor: export do GameInput indisponível: {ex.Message}");
                    ReleaseGameInput();
                    return false;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"GameInputGuideMonitor: falha ao iniciar: {ex}");
                    _callbackToken = 0;
                    _callback = null;
                    ReleaseGameInput();
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _pollCancellation?.Cancel();
                try
                {
                    _pollTask?.Wait(500);
                }
                catch { }
                _pollTask = null;
                _pollCancellation?.Dispose();
                _pollCancellation = null;
                try
                {
                    if (_gameInput != IntPtr.Zero && _callbackToken != 0)
                    {
                        var unregister = GetMethod<UnregisterCallbackDelegate>(UnregisterCallbackSlot);
                        bool unregistered = unregister(_gameInput, _callbackToken);
                        _log?.Invoke(
                            $"GameInputGuideMonitor: callback Guide desregistrado. Result={unregistered}.");
                    }

                    if (_gameInput != IntPtr.Zero && _readingCallbackToken != 0)
                    {
                        var unregister = GetMethod<UnregisterCallbackDelegate>(UnregisterCallbackSlot);
                        bool unregistered = unregister(_gameInput, _readingCallbackToken);
                        _log?.Invoke(
                            $"GameInputGuideMonitor: callback Gamepad desregistrado. Result={unregistered}.");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"GameInputGuideMonitor: falha ao desregistrar callback: {ex.Message}");
                }
                finally
                {
                    _callbackToken = 0;
                    _readingCallbackToken = 0;
                    _callback = null;
                    _readingCallback = null;
                    ReleaseGameInput();
                }
            }
        }

        private void OnSystemButtonChanged(
            ulong callbackToken,
            IntPtr context,
            IntPtr device,
            ulong timestamp,
            uint currentButtons,
            uint previousButtons)
        {
            bool currentGuideDown = (currentButtons & GameInputSystemButtonGuide) != 0;
            bool previousGuideDown = (previousButtons & GameInputSystemButtonGuide) != 0;
            if (currentGuideDown == previousGuideDown)
            {
                return;
            }

            _log?.Invoke(
                $"GameInputGuideMonitor: Guide {(currentGuideDown ? "pressed" : "released")}. " +
                $"current=0x{currentButtons:X}, previous=0x{previousButtons:X}.");
            try
            {
                _guideChanged(currentGuideDown);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"GameInputGuideMonitor: erro no callback do consumidor: {ex}");
            }
        }

        private void OnGamepadReading(ulong callbackToken, IntPtr context, IntPtr reading)
        {
            if (_bChanged == null || reading == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var getGamepadState = GetReadingMethod<GetGamepadStateDelegate>(reading, GetGamepadStateSlot);
                GameInputGamepadState state = default;
                if (!getGamepadState(reading, out state))
                {
                    return;
                }

                PublishBState((state.Buttons & GameInputGamepadB) != 0, "callback");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"GameInputGuideMonitor: erro ao ler estado Gamepad: {ex}");
            }
        }

        private async Task PollGamepadAsync(CancellationToken cancellationToken)
        {
            bool hadState = false;
            bool previousB = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    IntPtr reading = IntPtr.Zero;
                    try
                    {
                        var getCurrentReading = GetMethod<GetCurrentReadingDelegate>(GetCurrentReadingSlot);
                        int hr = getCurrentReading(
                            _gameInput,
                            GameInputKindGamepad,
                            IntPtr.Zero,
                            out reading);
                        if (hr >= 0 && reading != IntPtr.Zero)
                        {
                            var getGamepadState = GetReadingMethod<GetGamepadStateDelegate>(reading, GetGamepadStateSlot);
                            GameInputGamepadState state = default;
                            if (getGamepadState(reading, out state))
                            {
                                bool currentB = (state.Buttons & GameInputGamepadB) != 0;
                                if (!hadState || currentB != previousB)
                                {
                                    previousB = currentB;
                                    hadState = true;
                                    PublishBState(currentB, "polling");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"GameInputGuideMonitor: erro no polling Gamepad: {ex.Message}");
                    }
                    finally
                    {
                        if (reading != IntPtr.Zero)
                        {
                            try
                            {
                                var release = GetReadingMethod<ReleaseDelegate>(reading, ReleaseSlot);
                                release(reading);
                            }
                            catch (Exception ex)
                            {
                                _log?.Invoke($"GameInputGuideMonitor: falha ao liberar leitura Gamepad: {ex.Message}");
                            }
                        }
                    }

                    await Task.Delay(16, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void PublishBState(bool isBDown, string source)
        {
            if (isBDown == _lastGameInputB)
            {
                return;
            }

            _lastGameInputB = isBDown;
            _log?.Invoke($"GameInputGuideMonitor: B {(isBDown ? "pressed" : "released")} ({source}).");
            _bChanged?.Invoke(isBDown);
        }

        private T GetMethod<T>(int slot) where T : Delegate
        {
            return GetVtableMethod<T>(_gameInput, slot);
        }

        private static T GetReadingMethod<T>(IntPtr reading, int slot) where T : Delegate
        {
            return GetVtableMethod<T>(reading, slot);
        }

        private static T GetVtableMethod<T>(IntPtr instance, int slot) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(instance);
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(method);
        }

        private void ReleaseGameInput()
        {
            if (_gameInput == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var release = GetMethod<ReleaseDelegate>(ReleaseSlot);
                release(_gameInput);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"GameInputGuideMonitor: falha ao liberar GameInput: {ex.Message}");
            }
            finally
            {
                _gameInput = IntPtr.Zero;
            }
        }

        [DllImport(GameInputRedistDll, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int GameInputInitialize(ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SystemButtonCallback(
            ulong callbackToken,
            IntPtr context,
            IntPtr device,
            ulong timestamp,
            uint currentButtons,
            uint previousButtons);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ReadingCallback(
            ulong callbackToken,
            IntPtr context,
            IntPtr reading);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetFocusPolicyDelegate(IntPtr @this, uint policy);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegisterSystemButtonCallbackDelegate(
            IntPtr @this,
            IntPtr device,
            uint buttonFilter,
            IntPtr context,
            IntPtr callback,
            out ulong callbackToken);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegisterReadingCallbackDelegate(
            IntPtr @this,
            IntPtr device,
            uint inputKind,
            IntPtr context,
            IntPtr callback,
            out ulong callbackToken);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCurrentReadingDelegate(
            IntPtr @this,
            uint inputKind,
            IntPtr device,
            out IntPtr reading);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetGamepadStateDelegate(
            IntPtr @this,
            out GameInputGamepadState state);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool UnregisterCallbackDelegate(IntPtr @this, ulong callbackToken);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr @this);

        [StructLayout(LayoutKind.Sequential)]
        private struct GameInputGamepadState
        {
            public uint Buttons;
            public float LeftTrigger;
            public float RightTrigger;
            public float LeftThumbstickX;
            public float LeftThumbstickY;
            public float RightThumbstickX;
            public float RightThumbstickY;
        }
    }
}
