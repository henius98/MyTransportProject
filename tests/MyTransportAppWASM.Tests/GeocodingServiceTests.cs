using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class GeocodingServiceTests
{
    [Theory]
    [InlineData(" loc-2 ")]
    [InlineData("LOC-2")]
    [InlineData(" Johor ")]
    public async Task Suggestions_MatchTrimmedNamesAndLocationIds(string input)
    {
        var client = new HttpClient(new CountingLocationHandler()) { BaseAddress = new Uri("https://app.example.test/") };
        var service = new GeocodingService(client, new ConfigurationBuilder().Build());

        var suggestion = Assert.Single(await service.GetSuggestionsAsync(input));

        Assert.Equal("MET ID: loc-2", suggestion.FormattedAddress);
        Assert.Equal("Johor Bahru", suggestion.Name);
        Assert.Equal(1.4927, suggestion.Lat);
        Assert.Equal(103.7414, suggestion.Lng);
    }

    [Theory]
    [InlineData("3.139, 101.687", 3.139, 101.687)]
    [InlineData("  -33.86   +151.21  ", -33.86, 151.21)]
    [InlineData("0,0", 0, 0)]
    [InlineData("-90,180", -90, 180)]
    [InlineData("90,-180", 90, -180)]
    public async Task Suggestions_AcceptCoordinatesWithoutLoadingLocationCatalog(string input, double lat, double lng)
    {
        var handler = new CountingLocationHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.example.test/") };
        var service = new GeocodingService(client, new ConfigurationBuilder().Build());
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var suggestion = Assert.Single(await service.GetSuggestionsAsync(input));

            Assert.Equal(lat, suggestion.Lat);
            Assert.Equal(lng, suggestion.Lng);
            Assert.Equal(FormattableString.Invariant($"{lat}, {lng}"), suggestion.Name);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("91, 101")]
    [InlineData("3, -181")]
    [InlineData("NaN, 101")]
    [InlineData("3, Infinity")]
    [InlineData("3, 101, 5")]
    [InlineData("3,")]
    [InlineData("unknown-id")]
    public async Task Suggestions_RejectInvalidCoordinatesAndUnknownLocations(string input)
    {
        var client = new HttpClient(new CountingLocationHandler()) { BaseAddress = new Uri("https://app.example.test/") };
        var service = new GeocodingService(client, new ConfigurationBuilder().Build());

        Assert.Empty(await service.GetSuggestionsAsync(input));
    }

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
