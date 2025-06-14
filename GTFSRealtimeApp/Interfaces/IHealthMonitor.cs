using GTFSRealtimeAppSettings;
using Models;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IHealthMonitor
    {
        void UpdateStatus(bool isSuccess, string? errorMessage = null);
        HealthStatus GetCurrentStatus();
    }
}
