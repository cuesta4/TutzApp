using System.Threading;

namespace TutzApp.Services
{
    public interface IProcessMonitorService
    {
        bool IsCs2Running { get; }
        bool IsAnyGameRunning { get; }
        void StartMonitoring(CancellationToken cancellationToken);
    }
}
