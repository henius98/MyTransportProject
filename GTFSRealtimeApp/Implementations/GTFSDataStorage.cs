using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTFSRealtimeApp.Implementations
{
    public class GTFSDataStorage : IGTFSDataStorage
    {
        private readonly ILogger<GTFSDataStorage> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;

        public GTFSDataStorage(ILogger<GTFSDataStorage> logger, IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public async Task SaveDataAsync(string source, string data, CancellationToken cancellationToken = default)
        {
            try
            {
                var appSettings = _settings.CurrentValue;

                if (appSettings.SaveToDatabase)
                {
                    // TODO: Implement database storage
                    _logger.LogInformation("Saving {Source} data to database (not implemented)", source);
                }
                else
                {
                    // Save to file
                    var outputDir = appSettings.OutputDirectory;
                    Directory.CreateDirectory(outputDir);

                    var fileName = $"{source}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                    var filePath = Path.Combine(outputDir, fileName);

                    await File.WriteAllTextAsync(filePath, data, cancellationToken);
                    _logger.LogInformation("Saved {Source} data to file: {FilePath}", source, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save {Source} data", source);
                throw;
            }
        }
    }
}
