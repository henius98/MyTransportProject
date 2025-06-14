using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTFSRealtimeApp.Services
{
    public class HealthMonitorService : BackgroundService
    {
        private readonly ILogger<HealthMonitorService> _logger;
        private readonly IHealthMonitor _healthMonitor;
        private readonly IOptionsMonitor<AppSettings> _settings;

        public HealthMonitorService(
            ILogger<HealthMonitorService> logger,
            IHealthMonitor healthMonitor,
            IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _healthMonitor = healthMonitor;
            _settings = settings;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.CurrentValue.EnableHealthMonitoring)
            {
                _logger.LogInformation("Health monitoring is disabled");
                return;
            }

            _logger.LogInformation("Health monitoring service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var healthStatus = _healthMonitor.GetCurrentStatus();

                    if (!healthStatus.IsHealthy)
                    {
                        _logger.LogWarning("Application health check failed: {Reason}", healthStatus.LastError);
                    }
                    else
                    {
                        _logger.LogDebug("Health check passed. Last successful operation: {Time}",
                            healthStatus.LastSuccessTime);
                    }

                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in health monitor service");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("Health monitoring service stopped");
        }
    }
}
