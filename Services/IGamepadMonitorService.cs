// Services/IGamepadMonitorService.cs
using System.Threading;

namespace TutzApp.Services
{
    public interface IGamepadMonitorService
    {
        void StartMonitoring(CancellationToken cancellationToken);
        void RefreshSettings();
    }
}
