using System.Threading;

namespace TutzApp.Services
{
    public interface IGamepadMonitorService
    {
        event System.Action? RecognizedGamepadsChanged;
        void StartMonitoring(CancellationToken cancellationToken);
        void StartConnectionStatusMonitoring(CancellationToken cancellationToken);
        void RefreshSettings();
        bool StartCalibration();
    }
}
