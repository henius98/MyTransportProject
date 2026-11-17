using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

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
    var (service, mockCatalog, _, _) = CreateService(apiKey: null, withJs: false);
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
    var (service, mockCatalog, _, _) = CreateService(apiKey: null, withJs: false);
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);

    Assert.Empty(await service.GetSuggestionsAsync(input));
  }

  [Fact]
  public async Task StationMatch_ReturnsFormattedStationSuggestions()
  {
    var (service, mockCatalog, _, _) = CreateService(apiKey: null, withJs: false);
    mockCatalog.Setup(c => c.SearchStationsAsync("Langkawi", 15, It.IsAny<CancellationToken>()))
        .ReturnsAsync([
            new MetLocation { LocationId = "Ds001", LocationName = "Langkawi", Latitude = 6.35, Longitude = 99.8 }
        ]);

    var suggestions = await service.GetSuggestionsAsync("Langkawi");

    Assert.Single(suggestions);
    Assert.Equal("Langkawi", suggestions[0].Name);
    Assert.Equal("Ds001", suggestions[0].LocationId);
    Assert.Equal("Langkawi", suggestions[0].FormattedAddress);
    Assert.Equal(6.35, suggestions[0].Lat);
    Assert.Equal(99.8, suggestions[0].Lng);
  }

  [Fact]
  public async Task StationMissWithApiKey_FallsBackToGooglePlacesViaJs()
  {
    var (service, mockCatalog, _, mockModule) = CreateService(withImport: true);
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);
    SetupGeocode(mockModule, "KL Sentral", 3.134, 101.686, "Jalan Stesen Sentral, 50470 Kuala Lumpur");

    var suggestions = await service.GetSuggestionsAsync("KL Sentral");

    Assert.Single(suggestions);
    Assert.Equal(3.134, suggestions[0].Lat);
    Assert.Equal(101.686, suggestions[0].Lng);
    Assert.Equal("Jalan Stesen Sentral, 50470 Kuala Lumpur", suggestions[0].FormattedAddress);
  }

  [Fact]
  public async Task GeocodeAsync_WithJSRuntime_DelegatesToGoogleMapJsModule()
  {
    var (service, _, _, mockModule) = CreateService(withImport: true);
    SetupGeocode(mockModule, "Petronas Twin Towers", 3.1578, 101.7119, "Petronas Twin Towers, Kuala Lumpur");

    var place = await service.GeocodeAsync("Petronas Twin Towers");

    Assert.NotNull(place);
    Assert.Equal(3.1578, place.Value.Lat);
    Assert.Equal(101.7119, place.Value.Lng);
    Assert.Equal("Petronas Twin Towers, Kuala Lumpur", place.Value.FormattedAddress);
    mockModule.Verify(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task WithoutJsRuntime_ReturnsNull(bool reverse)
  {
    var (service, _, _, _) = CreateService(withJs: false);

    if (reverse)
      Assert.Null(await service.ReverseGeocodeAsync(3.1578, 101.7119));
    else
      Assert.Null(await service.GeocodeAsync("Petronas Twin Towers"));
  }

  [Theory]
  [InlineData(false, true)]
  [InlineData(true, true)]
  [InlineData(true, false)]
  public async Task CancelledToken_ThrowsOperationCanceledException(bool withJs, bool reverse)
  {
    var (service, _, _, _) = CreateService(withJs: withJs);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reverse
        ? service.ReverseGeocodeAsync(3.1578, 101.7119, cts.Token)
        : service.GeocodeAsync("KL Sentral", cts.Token));
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithoutApiKey_ReturnsNull()
  {
    var (service, _, _, _) = CreateService(apiKey: null, withJs: false);

    Assert.Null(await service.ReverseGeocodeAsync(3.1578, 101.7119));
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
    var (service, _, _, _) = CreateService(withJs: false);

    Assert.Null(await service.ReverseGeocodeAsync(lat, lng));
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithJSRuntime_DelegatesToGoogleMapJsModule()
  {
    var (service, _, mockJs, mockModule) = CreateService(withImport: true);
    mockModule.Setup(m => m.InvokeAsync<string?>("reverseGeocode", It.IsAny<CancellationToken>(), It.Is<object[]>(args =>
            args.Length == 3 &&
            (double)args[0] == 3.1578 &&
            (double)args[1] == 101.7119 &&
            (string)args[2] == "fake-key")))
        .ReturnsAsync("Petronas Twin Towers, Kuala Lumpur");

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Equal("Petronas Twin Towers, Kuala Lumpur", address);
    mockModule.Verify(m => m.InvokeAsync<string?>("reverseGeocode", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);

    // Finding 7: Module is cached, subsequent calls do not re-import
    var secondAddress = await service.ReverseGeocodeAsync(3.1578, 101.7119);
    Assert.Equal("Petronas Twin Towers, Kuala Lumpur", secondAddress);
    mockJs.Verify(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);

    // Module disposed on service disposal
    await service.DisposeAsync();
    mockModule.Verify(m => m.DisposeAsync(), Times.Once);
  }

  [Fact]
  public void DI_ActivatorUtilities_ResolvesPlaceResolutionServiceWithoutAmbiguity()
  {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddSingleton(new Mock<IWeatherStationCatalogService>().Object);
    services.AddSingleton(new Mock<IJSRuntime>().Object);
    services.AddScoped<IPlaceResolutionService, PlaceResolutionService>();

    var provider = services.BuildServiceProvider();
    Assert.NotNull(ActivatorUtilities.CreateInstance<PlaceResolutionService>(provider));

    var scopedService = provider.GetRequiredService<IPlaceResolutionService>();
    Assert.NotNull(scopedService);
    Assert.IsType<PlaceResolutionService>(scopedService);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithJSRuntime_WhenJsThrows_ReturnsNull()
  {
    var (service, _, _, _) = CreateService(importError: new JSException("Script load failed"));

    Assert.Null(await service.ReverseGeocodeAsync(3.1578, 101.7119));
  }

  [Theory]
  [InlineData(double.NaN, 101.7)]
  [InlineData(3.1, double.PositiveInfinity)]
  [InlineData(91.0, 101.7)]
  [InlineData(-91.0, 101.7)]
  [InlineData(3.1, 181.0)]
  [InlineData(3.1, -181.0)]
  public async Task GeocodeAsync_InvalidOrOutOfBoundsCoordinates_ReturnsNull(double lat, double lng)
  {
    var (service, _, _, mockModule) = CreateService(withImport: true);
    mockModule.Setup(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync(new GeocodeResult { Lat = lat, Lng = lng, FormattedAddress = "Invalid Location" });

    Assert.Null(await service.GeocodeAsync("Invalid Location"));
  }

  [Fact]
  public async Task DisposeAsync_IsIdempotent_DisposesModuleOnlyOnce()
  {
    var (service, _, mockJs, mockModule) = CreateService(withImport: true);
    mockModule.Setup(m => m.InvokeAsync<string?>("reverseGeocode", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync("Some address");

    // Warm up the module cache
    await service.ReverseGeocodeAsync(3.1, 101.6);

    // Dispose twice
    await service.DisposeAsync();
    await service.DisposeAsync();

    mockModule.Verify(m => m.DisposeAsync(), Times.Once);

    // Subsequent calls return null and do not re-import
    Assert.Null(await service.ReverseGeocodeAsync(3.1, 101.6));
    Assert.Null(await service.GeocodeAsync("Test"));
    mockJs.Verify(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);
  }

  [Fact]
  public async Task DisposeAsync_WhenModuleTaskFaulted_DoesNotThrow()
  {
    var (service, _, _, _) = CreateService(importError: new JSException("Module failed to load"));

    // Trigger faulted task
    await service.ReverseGeocodeAsync(3.1, 101.6);

    // DisposeAsync should handle faulted task gracefully without throwing
    Assert.Null(await Record.ExceptionAsync(() => service.DisposeAsync().AsTask()));
  }

  private static (
      PlaceResolutionService Service,
      Mock<IWeatherStationCatalogService> Catalog,
      Mock<IJSRuntime> Js,
      Mock<IJSObjectReference> Module) CreateService(
      string? apiKey = "fake-key",
      bool withJs = true,
      bool withImport = false,
      Exception? importError = null)
  {
    var catalog = new Mock<IWeatherStationCatalogService>();
    var js = new Mock<IJSRuntime>();
    var module = new Mock<IJSObjectReference>();
    if (importError != null)
    {
      js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
          .ThrowsAsync(importError);
    }
    else if (withImport)
    {
      js.Setup(j => j.InvokeAsync<IJSObjectReference>(
              "import",
              It.IsAny<CancellationToken>(),
              It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "./js/googleMap.js")))
          .ReturnsAsync(module.Object);
    }

    var configData = apiKey is null
        ? new Dictionary<string, string?>()
        : new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = apiKey };
    var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
    var service = new PlaceResolutionService(config, catalog.Object, withJs ? js.Object : null);
    return (service, catalog, js, module);
  }

  private static void SetupGeocode(
      Mock<IJSObjectReference> module,
      string query,
      double lat,
      double lng,
      string formattedAddress) =>
      module.Setup(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.Is<object[]>(args =>
              args.Length == 2 &&
              (string)args[0] == query &&
              (string)args[1] == "fake-key")))
          .ReturnsAsync(new GeocodeResult { Lat = lat, Lng = lng, FormattedAddress = formattedAddress });
}
