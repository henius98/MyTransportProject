using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GTFSRealtimeApp.Utils;
using TransitRealtime;

namespace GTFSRealtimeApp.Implementations
{
    public class GTFSApiClient : IGTFSApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GTFSApiClient> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;

        public GTFSApiClient(HttpClient httpClient, ILogger<GTFSApiClient> logger, IOptionsMonitor<AppSettings> settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        public async Task<FeedMessage> GetDataAsync(string url, CancellationToken cancellationToken = default)
        {
            var maxRetries = _settings.CurrentValue.MaxRetryAttempts;
            var retryDelay = TimeSpan.FromSeconds(_settings.CurrentValue.RetryDelaySeconds);
            var message = new FeedMessage();

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _logger.LogDebug("Attempting to fetch data from {Url} (attempt {Attempt}/{MaxAttempts})",
                        url, attempt, maxRetries);

                    var response = await _httpClient.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("✓ Successfully retrieved data!");
                        _logger.LogInformation($"Status Code: {response.StatusCode}");
                        _logger.LogInformation($"Content Type: {response.Content.Headers.ContentType}");

                        // Get the raw bytes
                        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
                        _logger.LogInformation($"Content Length: {responseBytes.Length} bytes");

                        message = await ProtocolBufferUtils.ParseAsync<FeedMessage>(responseBytes);
                    }

                    _logger.LogDebug("Successfully fetched data from {Url} on attempt {Attempt}", url, attempt);
                    return message;
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning("HTTP request failed for {Url} on attempt {Attempt}: {Error}. Retrying in {Delay}s...",
                        url, attempt, ex.Message, retryDelay.TotalSeconds);

                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt < maxRetries)
                {
                    _logger.LogWarning("Request timeout for {Url} on attempt {Attempt}. Retrying in {Delay}s...",
                        url, attempt, retryDelay.TotalSeconds);

                    await Task.Delay(retryDelay, cancellationToken);
                }
            }

            throw new HttpRequestException($"Failed to fetch data from {url} after {maxRetries} attempts");
        }
    }
}
