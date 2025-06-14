using GTFSRealtimeApp.Interfaces;
using Models;

namespace GTFSRealtimeApp.Implementations
{
    public class HealthMonitor : IHealthMonitor
    {
        private readonly object _lock = new();
        private HealthStatus _currentStatus = new() { IsHealthy = true };

        public void UpdateStatus(bool isSuccess, string? errorMessage = null)
        {
            lock (_lock)
            {
                _currentStatus.IsHealthy = isSuccess;
                _currentStatus.LastCheckTime = DateTime.UtcNow;

                if (isSuccess)
                {
                    _currentStatus.LastSuccessTime = DateTime.UtcNow;
                    _currentStatus.LastError = null;
                }
                else
                {
                    _currentStatus.LastError = errorMessage;
                }
            }
        }

        public HealthStatus GetCurrentStatus()
        {
            lock (_lock)
            {
                return new HealthStatus
                {
                    IsHealthy = _currentStatus.IsHealthy,
                    LastSuccessTime = _currentStatus.LastSuccessTime,
                    LastError = _currentStatus.LastError,
                    LastCheckTime = _currentStatus.LastCheckTime
                };
            }
        }
    }
}
