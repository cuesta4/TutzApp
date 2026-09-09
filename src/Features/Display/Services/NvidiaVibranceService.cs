using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TutzApp.Common;

namespace TutzApp.Services
{
    public class NvidiaVibranceService : INvidiaVibranceService
    {
        private const int NvapiOk = 0;
        private const uint NvapiInitializeId = 0x0150E828;
        private const uint NvapiEnumNvidiaDisplayHandleId = 0x9ABDD40D;
        private const uint NvapiGetDvcInfoExId = 0x0E45002D;
        private const uint NvapiSetDvcLevelExId = 0x4A82C2B1;
        private const uint DvcInfoExVersion = 0x10014; // MAKE_NVAPI_VERSION(sizeof(NV_DISPLAY_DVC_INFO_EX), 1)

        private readonly ISystemControlService _sysControl;
        private readonly object _settingsLock = new();
        private readonly object _nvapiLock = new();
        private readonly Dictionary<string, int> _rulesByProcessName = new(StringComparer.OrdinalIgnoreCase);

        private QueryInterfaceDelegate? _queryInterface;
        private InitializeDelegate? _initialize;
        private EnumNvidiaDisplayHandleDelegate? _enumDisplayHandle;
        private GetDvcInfoExDelegate? _getDvcInfoEx;
        private SetDvcLevelExDelegate? _setDvcLevelEx;
        private IntPtr _nvapiModule = IntPtr.Zero;
        private bool _settingsEnabled;
        private int _pollIntervalMs;
        private int _defaultDriverLevel;
        private int? _lastAppliedLevel;
        private int _monitorStarted;

        public bool IsAvailable { get; private set; }

        public NvidiaVibranceService(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
            RefreshSettings();
            IsAvailable = InitializeNvApi();
        }

        public void RefreshSettings()
        {
            var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in _sysControl.Config.Vibrance.AppRules)
            {
                if (string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    continue;
                }

                rules[rule.ProcessName] = Math.Clamp(rule.DriverLevel, 50, 100);
            }

            lock (_settingsLock)
            {
                _settingsEnabled = _sysControl.Config.Vibrance.Enabled;
                _pollIntervalMs = Math.Clamp(_sysControl.Config.Vibrance.FocusPollIntervalMs, 1000, 10000);
                _defaultDriverLevel = Math.Clamp(_sysControl.Config.Vibrance.DefaultDriverLevel, 0, 100);
                _rulesByProcessName.Clear();
                foreach (var pair in rules)
                {
                    _rulesByProcessName[pair.Key] = pair.Value;
                }
            }
        }

        public void StartMonitoring(CancellationToken cancellationToken)
        {
            if (!IsAvailable)
            {
                _sysControl.LogDebug("NvidiaVibrance: NVAPI indisponível. Monitoramento desativado.");
                return;
            }

            if (Interlocked.Exchange(ref _monitorStarted, 1) != 0)
            {
                _sysControl.LogDebug("NvidiaVibrance: tentativa duplicada de iniciar monitoramento ignorada.");
                return;
            }

            _ = Task.Run(() => RunMonitoringAsync(cancellationToken));
        }

        private async Task RunMonitoringAsync(CancellationToken cancellationToken)
        {
            _sysControl.LogDebug("NvidiaVibrance: Iniciando monitoramento de foco.");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int delayMs;
                    try
                    {
                        ApplyForForegroundWindow();
                        lock (_settingsLock)
                        {
                            delayMs = _settingsEnabled && _rulesByProcessName.Count > 0 ? _pollIntervalMs : 5000;
                        }
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"NvidiaVibrance: erro no monitoramento: {ex}");
                        delayMs = 2000;
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
                _sysControl.LogDebug($"NvidiaVibrance: loop encerrado inesperadamente: {ex}");
            }
            finally
            {
                _sysControl.LogDebug("NvidiaVibrance: monitoramento de foco encerrado.");
                Interlocked.Exchange(ref _monitorStarted, 0);
            }
        }

        public void RestoreDefault()
        {
            int defaultLevel;
            lock (_settingsLock)
            {
                defaultLevel = _defaultDriverLevel;
            }

            ApplyLevelIfNeeded(defaultLevel);
        }

        private void ApplyForForegroundWindow()
        {
            int targetLevel;
            lock (_settingsLock)
            {
                if (!_settingsEnabled || _rulesByProcessName.Count == 0)
                {
                    targetLevel = _defaultDriverLevel;
                }
                else
                {
                    string? processName = _sysControl.GetForegroundProcessName();
                    targetLevel = processName != null && _rulesByProcessName.TryGetValue(processName, out int appLevel)
                        ? appLevel
                        : _defaultDriverLevel;
                }
            }

            ApplyLevelIfNeeded(targetLevel);
        }

        private void ApplyLevelIfNeeded(int level)
        {
            level = Math.Clamp(level, 0, 100);
            if (_lastAppliedLevel == level)
            {
                return;
            }

            if (SetDvcLevel(level))
            {
                _lastAppliedLevel = level;
                _sysControl.LogDebug($"NvidiaVibrance: Digital Vibrance aplicado em {level}%.");
            }
        }

        private bool SetDvcLevel(int level)
        {
            if (_enumDisplayHandle == null || _getDvcInfoEx == null || _setDvcLevelEx == null)
            {
                return false;
            }

            lock (_nvapiLock)
            {
                bool applied = false;
                for (uint i = 0; i < 8; i++)
                {
                    int enumStatus = _enumDisplayHandle(i, out IntPtr displayHandle);
                    if (enumStatus != NvapiOk || displayHandle == IntPtr.Zero)
                    {
                        break;
                    }

                    var dvcInfo = new NvDisplayDvcInfoEx { Version = DvcInfoExVersion };
                    int getStatus = _getDvcInfoEx(displayHandle, 0, ref dvcInfo);
                    if (getStatus != NvapiOk)
                    {
                        continue;
                    }

                    dvcInfo.CurrentLevel = Math.Clamp(level, dvcInfo.MinLevel, dvcInfo.MaxLevel);
                    int setStatus = _setDvcLevelEx(displayHandle, 0, ref dvcInfo);
                    applied |= setStatus == NvapiOk;
                }

                return applied;
            }
        }

        private bool InitializeNvApi()
        {
            try
            {
                _nvapiModule = NativeMethods.LoadLibrary("nvapi64.dll");
                if (_nvapiModule == IntPtr.Zero)
                {
                    _nvapiModule = NativeMethods.LoadLibrary("nvapi.dll");
                }

                if (_nvapiModule == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr queryAddress = NativeMethods.GetProcAddress(_nvapiModule, "nvapi_QueryInterface");
                if (queryAddress == IntPtr.Zero)
                {
                    return false;
                }

                _queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryAddress);
                _initialize = GetNvApiDelegate<InitializeDelegate>(NvapiInitializeId);
                _enumDisplayHandle = GetNvApiDelegate<EnumNvidiaDisplayHandleDelegate>(NvapiEnumNvidiaDisplayHandleId);
                _getDvcInfoEx = GetNvApiDelegate<GetDvcInfoExDelegate>(NvapiGetDvcInfoExId);
                _setDvcLevelEx = GetNvApiDelegate<SetDvcLevelExDelegate>(NvapiSetDvcLevelExId);

                if (_initialize == null || _enumDisplayHandle == null || _getDvcInfoEx == null || _setDvcLevelEx == null)
                {
                    return false;
                }

                int status = _initialize();
                _sysControl.LogDebug($"NvidiaVibrance: NVAPI initialize status {status}.");
                return status == NvapiOk;
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"NvidiaVibrance: falha ao inicializar NVAPI: {ex.Message}");
                return false;
            }
        }

        private TDelegate? GetNvApiDelegate<TDelegate>(uint id) where TDelegate : Delegate
        {
            if (_queryInterface == null) return null;

            IntPtr address = _queryInterface(id);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvDisplayDvcInfoEx
        {
            public uint Version;
            public int CurrentLevel;
            public int MinLevel;
            public int MaxLevel;
            public int DefaultLevel;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr QueryInterfaceDelegate(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumNvidiaDisplayHandleDelegate(uint thisEnum, out IntPtr displayHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDvcInfoExDelegate(IntPtr displayHandle, uint outputId, ref NvDisplayDvcInfoEx dvcInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetDvcLevelExDelegate(IntPtr displayHandle, uint outputId, ref NvDisplayDvcInfoEx dvcInfo);

    }
}
