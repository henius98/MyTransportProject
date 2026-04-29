using MyTransportAppWASM.Utils;
using MyTransportAppWASM.Services.Interfaces;
using TransitRealtime;

namespace MyTransportAppWASM.Services
{
    public class GtfsService : IGtfsService
    {
        private readonly HttpClient _httpClient;

        public GtfsService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        public async Task<FeedMessage?> GetBusPositionsAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // Get the raw bytes
                    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();

                    return await ProtocolBufferUtils.ParseAsync<FeedMessage>(responseBytes);
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Failed to fetch data {ex}");
            }

        }
    }
}
