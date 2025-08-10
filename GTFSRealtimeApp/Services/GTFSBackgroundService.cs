using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GTFSRealtimeApp.Services
{
    public class GTFSBackgroundService : BackgroundService
    {
        private readonly ILogger<GTFSBackgroundService> _logger;
        private readonly IOptionsMonitor<GTFSRealtimeApiSettings> _apiSettings;
        private readonly IOptionsMonitor<AppSettings> _appSettings;
        private readonly IGTFSDataProcessor _dataProcessor;
        private readonly IHealthMonitor _healthMonitor;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IServiceScopeFactory _scopeFactory;

        public GTFSBackgroundService(
            ILogger<GTFSBackgroundService> logger,
            IOptionsMonitor<GTFSRealtimeApiSettings> apiSettings,
            IOptionsMonitor<AppSettings> appSettings,
            IGTFSDataProcessor dataProcessor,
            IHealthMonitor healthMonitor,
            IHostApplicationLifetime lifetime,
            IServiceScopeFactory scopeFactory
            )
        {
            _logger = logger;
            _apiSettings = apiSettings;
            _appSettings = appSettings;
            _dataProcessor = dataProcessor;
            _healthMonitor = healthMonitor;
            _lifetime = lifetime;
            _scopeFactory = scopeFactory;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("GTFS Background Service is starting...");

            // Perform startup validation
            try
            {
                var apiConfig = _apiSettings.CurrentValue;
                var appConfig = _appSettings.CurrentValue;

                _logger.LogInformation("Configuration validated successfully");
                _logger.LogInformation("Polling interval: {Interval} seconds", appConfig.PollingIntervalSeconds);
                _logger.LogInformation("RapidPenang API: {Url}", apiConfig.Prasarana.RapidPenang);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to validate configuration during startup");
                _lifetime.StopApplication();
                return;
            }

            await base.StartAsync(cancellationToken);
            _logger.LogInformation("GTFS Background Service started successfully");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var consecutiveErrors = 0;
            const int maxConsecutiveErrors = 5;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _logger.BeginScope("GTFS Data Processing Cycle");

                    var startTime = DateTime.UtcNow;
                    _logger.LogInformation("Starting data processing cycle at {StartTime}", startTime);

                    var apiConfig = _apiSettings.CurrentValue;
                    var appConfig = _appSettings.CurrentValue;

                    // Process GTFS data
                    var result = await _dataProcessor.ProcessAllDataAsync(apiConfig, stoppingToken);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    _logger.LogInformation("Data processing cycle completed in {Duration}ms. Success: {Success}",
                        duration.TotalMilliseconds, result.IsSuccess);

                    // Update health status
                    _healthMonitor.UpdateStatus(result.IsSuccess, result.ErrorMessage);

                    // Reset error counter on success
                    if (result.IsSuccess)
                    {
                        consecutiveErrors = 0;
                    }
                    else
                    {
                        consecutiveErrors++;
                        _logger.LogWarning("Processing failed. Consecutive errors: {Count}/{Max}",
                            consecutiveErrors, maxConsecutiveErrors);
                    }

                    // Check if we should stop due to too many errors
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        _logger.LogCritical("Too many consecutive errors ({Count}). Stopping service.", consecutiveErrors);
                        _lifetime.StopApplication();
                        break;
                    }

                    // Wait for next polling interval
                    var delay = TimeSpan.FromSeconds(appConfig.PollingIntervalSeconds);
                    _logger.LogDebug("Waiting {Delay} seconds until next cycle", appConfig.PollingIntervalSeconds);

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Background service cancellation requested");
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex, "Unexpected error in background service execution. Consecutive errors: {Count}", consecutiveErrors);

                    _healthMonitor.UpdateStatus(false, ex.Message);

                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        _logger.LogCritical("Too many consecutive errors. Stopping application.");
                        _lifetime.StopApplication();
                        break;
                    }

                    // Wait before retrying
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("GTFS Background Service is stopping...");
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("GTFS Background Service stopped");
        }
    }
}
