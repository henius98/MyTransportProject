using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Models;

namespace GTFSRealtimeApp.Implementations
{
    public class GTFSDataProcessor : IGTFSDataProcessor
    {
        private readonly ILogger<GTFSDataProcessor> _logger;
        private readonly IGTFSApiClient _apiClient;
        private readonly IGTFSDataStorage _storage;
        private readonly IOptionsMonitor<AppSettings> _appSettings;

        public GTFSDataProcessor(
            ILogger<GTFSDataProcessor> logger,
            IGTFSApiClient apiClient,
            IGTFSDataStorage storage,
            IOptionsMonitor<AppSettings> appSettings)
        {
            _logger = logger;
            _apiClient = apiClient;
            _storage = storage;
            _appSettings = appSettings;
        }

        public async Task<JobResult> ProcessAllDataAsync(GTFSRealtimeApiSettings settings, CancellationToken cancellationToken)
        {
            var result = new JobResult();
            var processedCount = 0;

            try
            {
                // Process RapidPenang data
                _logger.LogInformation("Fetching RapidPenang data...");
                var rapidPenangData = await _apiClient.GetDataAsync(settings.Prasarana.RapidPenang, cancellationToken);
                await _storage.SaveDataAsync("RapidPenang", rapidPenangData, cancellationToken);

                await using var httpStream = await _apiClient.GetGtfsStaticDataAsync("rapid-bus-penang", cancellationToken);
                await _storage.SaveGtfsStaticDataAsync(httpStream, cancellationToken);

                processedCount++;
                _logger.LogInformation("RapidPenang data processed successfully. Size: {Size} bytes", rapidPenangData.ToString().Length);

                result.IsSuccess = true;
                result.ProcessedItems = processedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing GTFS data");
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                result.ProcessedItems = processedCount;
            }

            return result;
        }
    }
}
