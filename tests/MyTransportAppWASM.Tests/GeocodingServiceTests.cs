using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class GeocodingServiceTests
{
    [Fact]
    public async Task LocationDataLoad_IsCoalescedAcrossConcurrentCallers()
    {
        var handler = new CountingLocationHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.example.test/") };
        var service = new GeocodingService(client, new ConfigurationBuilder().Build());

        var allLocationsTask = service.GetAllLocationsAsync();
        var suggestionsTask = service.GetSuggestionsAsync("Kuala");
        await Task.WhenAll(allLocationsTask, suggestionsTask);
        var allLocations = await allLocationsTask;
        var suggestions = await suggestionsTask;
        var cachedLocations = await service.GetAllLocationsAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(2, allLocations.Count);
        Assert.Single(suggestions);
        Assert.Same(allLocations, cachedLocations);
    }

    private sealed class CountingLocationHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(30, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    [
                      { "location_id": "loc-1", "location_name": "Kuala Lumpur", "latitude": 3.139, "longitude": 101.687 },
                      { "location_id": "loc-2", "location_name": "Johor Bahru", "latitude": 1.4927, "longitude": 103.7414 }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
