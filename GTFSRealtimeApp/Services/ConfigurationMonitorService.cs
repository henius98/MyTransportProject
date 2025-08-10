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
    public class ConfigurationMonitorService : BackgroundService
    {
        private readonly ILogger<ConfigurationMonitorService> _logger;
        private readonly IOptionsMonitor<GTFSRealtimeApiSettings> _apiSettings;
        private readonly IOptionsMonitor<AppSettings> _appSettings;

        public ConfigurationMonitorService(
            ILogger<ConfigurationMonitorService> logger,
            IOptionsMonitor<GTFSRealtimeApiSettings> apiSettings,
            IOptionsMonitor<AppSettings> appSettings)
        {
            _logger = logger;
            _apiSettings = apiSettings;
            _appSettings = appSettings;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Monitor configuration changes
            using var apiSettingsChangeToken = _apiSettings.OnChange((settings, name) =>
            {
                _logger.LogInformation("API configuration changed: {Name}", name ?? "default");
            });

            using var appSettingsChangeToken = _appSettings.OnChange((settings, name) =>
            {
                _logger.LogInformation("App configuration changed: {Name}. New polling interval: {Interval} seconds",
                    name ?? "default", settings.PollingIntervalSeconds);
            });

            // Keep the service running to monitor changes
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Configuration monitoring service stopped");
            }
        }
    }
}
