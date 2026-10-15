using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Tests;

public class PlaceResolutionServiceTests
{
  [Theory]
  [InlineData("3.14, 101.69", 3.14, 101.69)]
  [InlineData("3.139, 101.687", 3.139, 101.687)]
  [InlineData("  -33.86   +151.21  ", -33.86, 151.21)]
  [InlineData("0,0", 0, 0)]
  [InlineData("-90,180", -90, 180)]
  [InlineData("90,-180", 90, -180)]
  public async Task CoordinateQuery_ReturnsCoordinateSuggestionDirectlyWithoutNetwork(string input, double lat, double lng)
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var handler = new MockHttpHandler();
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);
    var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
      System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
      var suggestions = await service.GetSuggestionsAsync(input);

      var suggestion = Assert.Single(suggestions);
      Assert.Equal(lat, suggestion.Lat);
      Assert.Equal(lng, suggestion.Lng);
      Assert.Equal("Latitude, longitude", suggestion.FormattedAddress);
      Assert.Equal(FormattableString.Invariant($"{lat}, {lng}"), suggestion.Name);
      mockCatalog.Verify(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);
    var handler = new MockHttpHandler();
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    Assert.Empty(await service.GetSuggestionsAsync(input));
  }

  [Fact]
  public async Task StationMatch_ReturnsFormattedStationSuggestions()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    mockCatalog.Setup(c => c.SearchStationsAsync("Langkawi", 15, It.IsAny<CancellationToken>()))
        .ReturnsAsync([
            new MetLocation { LocationId = "Ds001", LocationName = "Langkawi", Latitude = 6.35, Longitude = 99.8 }
        ]);

    var handler = new MockHttpHandler();
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    var suggestions = await service.GetSuggestionsAsync("Langkawi");

    Assert.Single(suggestions);
    Assert.Equal("Langkawi", suggestions[0].Name);
    Assert.Equal("MET ID: Ds001", suggestions[0].FormattedAddress);
    Assert.Equal(6.35, suggestions[0].Lat);
    Assert.Equal(99.8, suggestions[0].Lng);
  }

  [Fact]
  public async Task StationMissWithApiKey_FallsBackToGooglePlaces()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);

    var handler = new MockHttpHandler(
        """
            {
              "places": [
                {
                  "formattedAddress": "Jalan Stesen Sentral, 50470 Kuala Lumpur",
                  "location": { "latitude": 3.134, "longitude": 101.686 },
                  "displayName": { "text": "KL Sentral" }
                }
              ]
            }
            """);
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    var suggestions = await service.GetSuggestionsAsync("KL Sentral");

    Assert.Single(suggestions);
    Assert.Equal(3.134, suggestions[0].Lat);
    Assert.Equal(101.686, suggestions[0].Lng);
    Assert.Equal("Jalan Stesen Sentral, 50470 Kuala Lumpur", suggestions[0].FormattedAddress);
    Assert.Equal(1, handler.RequestCount);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_ParsesFormattedAddressFromGoogleGeocodingJson()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var handler = new MockHttpHandler(
        """
            {
              "status": "OK",
              "results": [
                {
                  "formatted_address": "Petronas Twin Towers, Kuala Lumpur"
                }
              ]
            }
            """);
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Equal("Petronas Twin Towers, Kuala Lumpur", address);
    Assert.NotNull(handler.LastRequest);
    Assert.True(handler.LastRequest.Headers.Contains("X-Goog-Api-Key"));
    Assert.Equal("fake-key", handler.LastRequest.Headers.GetValues("X-Goog-Api-Key").Single());
    Assert.DoesNotContain("key=", handler.LastRequest.RequestUri?.Query ?? "");
  }

  [Fact]
  public async Task ReverseGeocodeAsync_CancelledToken_ThrowsOperationCanceledException()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var handler = new MockHttpHandler("{}");
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        service.ReverseGeocodeAsync(3.1578, 101.7119, cts.Token));
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithoutApiKey_ReturnsNull()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var handler = new MockHttpHandler();
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Null(address);
    Assert.Equal(0, handler.RequestCount);
  }

  [Theory]
  [InlineData(double.NaN, 101.7)]
  [InlineData(3.1, double.PositiveInfinity)]
  [InlineData(91.0, 101.7)]
  [InlineData(3.1, 181.0)]
  [InlineData(-91.0, 101.7)]
  [InlineData(3.1, -181.0)]
  public async Task ReverseGeocodeAsync_WithInvalidCoordinates_ReturnsNullWithoutRequest(double lat, double lng)
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var handler = new MockHttpHandler();
    var client = new HttpClient(handler);
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(client, config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(lat, lng);

    Assert.Null(address);
    Assert.Equal(0, handler.RequestCount);
  }

  private sealed class MockHttpHandler : HttpMessageHandler
  {
    private readonly string? _responseBody;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpHandler(string? responseBody = null) => _responseBody = responseBody;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      LastRequest = request;
      Interlocked.Increment(ref _requestCount);
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(_responseBody ?? "{}", Encoding.UTF8, "application/json")
      });
    }
  }
}
