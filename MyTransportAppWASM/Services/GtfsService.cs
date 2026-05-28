using Microsoft.Extensions.Caching.Memory;
using MyTransportAppWASM.Utils;
using MyTransportAppWASM.Services.Interfaces;
using TransitRealtime;

namespace MyTransportAppWASM.Services
{
    public class GtfsService : IGtfsService
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;

        public GtfsService(HttpClient httpClient, IMemoryCache cache)
        {
            _httpClient = httpClient;
            _cache = cache;
        }
        public async Task<FeedMessage?> GetBusPositionsAsync(string url, CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(url, out FeedMessage? cachedFeed))
            {
                return cachedFeed;
            }

            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // Get the raw bytes
                    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();

                    var feed = await ProtocolBufferUtils.ParseAsync<FeedMessage>(responseBytes);
                    if (feed != null)
                    {
                        _cache.Set(url, feed, TimeSpan.FromSeconds(15));
                    }
                    return feed;
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GtfsService error fetching {url}: {ex.Message}");
                return null;
            }

        }
    }
}
