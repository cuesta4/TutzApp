// Services/ProcessMonitorService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TutzApp.Common;

namespace TutzApp.Services
{
    public class ProcessMonitorService : IProcessMonitorService
    {
        private readonly ISystemControlService _sysControl;
        private bool _cs2RunningState = false;
        private int _gamesRunningState = -1; // -1 = inicial
        private int _monitorStarted;

        // Backup de resolução pré-CS2
        private uint _prevResW = 2560;
        private uint _prevResH = 1440;
        private uint _prevResR = 144;

        public bool IsCs2Running { get; private set; } = false;
        public bool IsAnyGameRunning { get; private set; } = false;

        public ProcessMonitorService(ISystemControlService sysControl)
        {
            _sysControl = sysControl;
        }

        public void StartMonitoring(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _monitorStarted, 1) != 0)
            {
                _sysControl.LogDebug("ProcessMonitorService: tentativa duplicada de iniciar monitoramento ignorada.");
                return;
            }

            _ = Task.Run(() => RunMonitoringAsync(cancellationToken));
        }

        private async Task RunMonitoringAsync(CancellationToken cancellationToken)
        {
            _sysControl.LogDebug("ProcessMonitorService: Iniciando loop de monitoramento de processos...");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await CheckProcessesAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _sysControl.LogDebug($"ProcessMonitorService: erro no monitoramento de processos: {ex}");
                    }

                    int intervalMs = Math.Clamp(_sysControl.Config.Monitoring.GamesCheckIntervalMs, 250, 60000);
                    await Task.Delay(intervalMs, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Encerramento normal do aplicativo.
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"ProcessMonitorService: loop encerrado inesperadamente: {ex}");
            }
            finally
            {
                _sysControl.LogDebug("ProcessMonitorService: loop de monitoramento encerrado.");
                Interlocked.Exchange(ref _monitorStarted, 0);
            }
        }

        private HashSet<string> GetRunningProcessNamesWithoutExtension()
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS = 0x00000002
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return running;

            try
            {
                var entry = new NativeMethods.PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32));
                if (NativeMethods.Process32First(snapshot, ref entry))
                {
                    do
                    {
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(entry.szExeFile);
                        if (!string.IsNullOrEmpty(nameWithoutExt))
                        {
                            running.Add(nameWithoutExt);
                        }
                    } while (NativeMethods.Process32Next(snapshot, ref entry));
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"ProcessMonitorService: erro ao listar processos via Toolhelp: {ex}");
            }
            finally
            {
                NativeMethods.CloseHandle(snapshot);
            }
            return running;
        }

        private bool IsMonitoredGameRunning(HashSet<string> runningProcesses, bool cs2Now)
        {
            if (cs2Now)
                return true;

            foreach (var gameExe in _sysControl.Config.Monitoring.Games)
            {
                string name = Path.GetFileNameWithoutExtension(gameExe);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("cs2", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (runningProcesses.Contains(name))
                    return true;
            }

            return false;
        }

        private string GetActiveGameName(bool anyGameNow, bool cs2Now)
        {
            return anyGameNow ? (cs2Now ? "CS2" : "Outro Jogo") : "Nenhum";
        }

        private async Task CheckProcessesAsync(CancellationToken cancellationToken)
        {
            var runningProcesses = GetRunningProcessNamesWithoutExtension();
            bool cs2Now = runningProcesses.Contains("cs2");
            IsCs2Running = cs2Now;

            // Monitorar mudanças específicas do CS2 para resolução
            if (cs2Now != _cs2RunningState)
            {
                _sysControl.LogDebug($"ProcessMonitor: Estado do CS2 alterou para {cs2Now}");
                _cs2RunningState = cs2Now;

                if (_sysControl.Config.Monitoring.AutoResolutionSwitch)
                {
                    if (cs2Now)
                    {
                        // Guardar resolução atual como backup, desde que não seja a própria resolução de jogo
                        if (_sysControl.GetCurrentResolution(out uint w, out uint h, out uint r))
                        {
                            if (!(w == 1920 && h == 1080 && r == 144))
                            {
                                _prevResW = w;
                                _prevResH = h;
                                _prevResR = r;
                                _sysControl.LogDebug($"ProcessMonitor: Backup de Resolução Salvo: {_prevResW}x{_prevResH}@{_prevResR}Hz");
                            }
                        }

                        // Trocar para a resolução competitiva (1920x1080@144Hz)
                        bool changed = _sysControl.ChangeResolution(1920, 1080, 144);
                        _sysControl.LogDebug($"ProcessMonitor: alteração para resolução competitiva concluída. Sucesso={changed}.");
                    }
                    else
                    {
                        // Restaurar resolução anterior
                        _sysControl.LogDebug($"ProcessMonitor: Restaurando Resolução Anterior: {_prevResW}x{_prevResH}@{_prevResR}Hz");
                        bool restored = _sysControl.ChangeResolution(_prevResW, _prevResH, _prevResR);
                        _sysControl.LogDebug($"ProcessMonitor: restauração da resolução anterior concluída. Sucesso={restored}.");
                    }
                }
            }

            // Checar se qualquer jogo da lista de monitorados está rodando
            bool anyGameNow = IsMonitoredGameRunning(runningProcesses, cs2Now);
            IsAnyGameRunning = anyGameNow;
            _sysControl.Status.ActiveGame = GetActiveGameName(anyGameNow, cs2Now);
            _sysControl.NotifyStatusChanged();

            int runningStateNow = anyGameNow ? 1 : 0;
            if (runningStateNow == _gamesRunningState)
                return;

            if (_sysControl.Config.Monitoring.AutoGameProfileSwitch)
            {
                int delayMs = anyGameNow
                    ? _sysControl.Config.Monitoring.GameProfileEnterDelayMs
                    : _sysControl.Config.Monitoring.GameProfileExitDelayMs;

                delayMs = Math.Clamp(delayMs, 0, 10000);
                if (delayMs > 0)
                {
                    _sysControl.LogDebug($"ProcessMonitor: Aguardando {delayMs}ms antes de aplicar perfil automático...");
                    await Task.Delay(delayMs, cancellationToken);

                    runningProcesses = GetRunningProcessNamesWithoutExtension();
                    cs2Now = runningProcesses.Contains("cs2");
                    anyGameNow = IsMonitoredGameRunning(runningProcesses, cs2Now);
                    IsCs2Running = cs2Now;
                    IsAnyGameRunning = anyGameNow;
                    _sysControl.Status.ActiveGame = GetActiveGameName(anyGameNow, cs2Now);
                    _sysControl.NotifyStatusChanged();
                    runningStateNow = anyGameNow ? 1 : 0;

                    if (runningStateNow == _gamesRunningState)
                    {
                        _sysControl.LogDebug("ProcessMonitor: transição cancelada após revalidação; estado já coincide com o último perfil aplicado.");
                        return;
                    }
                }
            }

            _gamesRunningState = runningStateNow;
            _sysControl.LogDebug($"ProcessMonitor: Estado dos jogos (any game) alterou para: {anyGameNow}");

            if (_sysControl.Config.Monitoring.AutoGameProfileSwitch)
            {
                _sysControl.LogDebug($"ProcessMonitor: aplicando perfil automático após confirmação estável. Perfil={(anyGameNow ? "Jogo" : "Normal")}.");
                _sysControl.ApplyAutomationProfile(anyGameNow);
            }
        }
    }
}
