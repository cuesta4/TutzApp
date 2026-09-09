using System.Threading;

namespace TutzApp.Services
{
    public interface INvidiaVibranceService
    {
        bool IsAvailable { get; }
        void StartMonitoring(CancellationToken cancellationToken);
        void RefreshSettings();
        void RestoreDefault();
    }
}
