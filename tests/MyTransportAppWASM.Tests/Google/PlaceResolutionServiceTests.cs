using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
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
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);
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
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

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

    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    var suggestions = await service.GetSuggestionsAsync("Langkawi");

    Assert.Single(suggestions);
    Assert.Equal("Langkawi", suggestions[0].Name);
    Assert.Equal("MET ID: Ds001", suggestions[0].FormattedAddress);
    Assert.Equal(6.35, suggestions[0].Lat);
    Assert.Equal(99.8, suggestions[0].Lng);
  }

  [Fact]
  public async Task StationMissWithApiKey_FallsBackToGooglePlacesViaJs()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    mockCatalog.Setup(c => c.SearchStationsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([]);

    var mockJs = new Mock<IJSRuntime>();
    var mockModule = new Mock<IJSObjectReference>();
    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "./js/googleMap.js")))
        .ReturnsAsync(mockModule.Object);

    mockModule.Setup(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.Is<object[]>(args =>
            args.Length == 2 &&
            (string)args[0] == "KL Sentral" &&
            (string)args[1] == "fake-key")))
        .ReturnsAsync(new GeocodeResult
        {
          Lat = 3.134,
          Lng = 101.686,
          FormattedAddress = "Jalan Stesen Sentral, 50470 Kuala Lumpur"
        });

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    var suggestions = await service.GetSuggestionsAsync("KL Sentral");

    Assert.Single(suggestions);
    Assert.Equal(3.134, suggestions[0].Lat);
    Assert.Equal(101.686, suggestions[0].Lng);
    Assert.Equal("Jalan Stesen Sentral, 50470 Kuala Lumpur", suggestions[0].FormattedAddress);
  }

  [Fact]
  public async Task GeocodeAsync_WithJSRuntime_DelegatesToGoogleMapJsModule()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var mockModule = new Mock<IJSObjectReference>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "./js/googleMap.js")))
        .ReturnsAsync(mockModule.Object);

    mockModule.Setup(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.Is<object[]>(args =>
            args.Length == 2 &&
            (string)args[0] == "Petronas Twin Towers" &&
            (string)args[1] == "fake-key")))
        .ReturnsAsync(new GeocodeResult
        {
          Lat = 3.1578,
          Lng = 101.7119,
          FormattedAddress = "Petronas Twin Towers, Kuala Lumpur"
        });

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    var place = await service.GeocodeAsync("Petronas Twin Towers");

    Assert.NotNull(place);
    Assert.Equal(3.1578, place.Value.Lat);
    Assert.Equal(101.7119, place.Value.Lng);
    Assert.Equal("Petronas Twin Towers, Kuala Lumpur", place.Value.FormattedAddress);
    mockModule.Verify(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);
  }

  [Fact]
  public async Task GeocodeAsync_WithoutJsRuntime_ReturnsNull()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    var place = await service.GeocodeAsync("Petronas Twin Towers");

    Assert.Null(place);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithoutJsRuntime_ReturnsNull()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Null(address);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_CancelledToken_ThrowsOperationCanceledException()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        service.ReverseGeocodeAsync(3.1578, 101.7119, cts.Token));
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithoutApiKey_ReturnsNull()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var config = new ConfigurationBuilder().Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Null(address);
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
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object);

    var address = await service.ReverseGeocodeAsync(lat, lng);

    Assert.Null(address);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithJSRuntime_DelegatesToGoogleMapJsModule()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var mockModule = new Mock<IJSObjectReference>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "./js/googleMap.js")))
        .ReturnsAsync(mockModule.Object);

    mockModule.Setup(m => m.InvokeAsync<string?>("reverseGeocode", It.IsAny<CancellationToken>(), It.Is<object[]>(args =>
            args.Length == 3 &&
            (double)args[0] == 3.1578 &&
            (double)args[1] == 101.7119 &&
            (string)args[2] == "fake-key")))
        .ReturnsAsync("Petronas Twin Towers, Kuala Lumpur");

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

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
    var instance = ActivatorUtilities.CreateInstance<PlaceResolutionService>(provider);
    Assert.NotNull(instance);

    var scopedService = provider.GetRequiredService<IPlaceResolutionService>();
    Assert.NotNull(scopedService);
    Assert.IsType<PlaceResolutionService>(scopedService);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithJSRuntime_WhenJsThrows_ReturnsNull()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ThrowsAsync(new JSException("Script load failed"));

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    var address = await service.ReverseGeocodeAsync(3.1578, 101.7119);

    Assert.Null(address);
  }

  [Fact]
  public async Task ReverseGeocodeAsync_WithJSRuntime_Cancellation_ThrowsOperationCanceledException()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        service.ReverseGeocodeAsync(3.1578, 101.7119, cts.Token));
  }

  [Fact]
  public async Task GeocodeAsync_WithJSRuntime_Cancellation_ThrowsOperationCanceledException()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        service.GeocodeAsync("KL Sentral", cts.Token));
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
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var mockModule = new Mock<IJSObjectReference>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync(mockModule.Object);

    mockModule.Setup(m => m.InvokeAsync<GeocodeResult?>("geocodePlaceName", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync(new GeocodeResult { Lat = lat, Lng = lng, FormattedAddress = "Invalid Location" });

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    var place = await service.GeocodeAsync("Invalid Location");

    Assert.Null(place);
  }

  [Fact]
  public async Task DisposeAsync_IsIdempotent_DisposesModuleOnlyOnce()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();
    var mockModule = new Mock<IJSObjectReference>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync(mockModule.Object);

    mockModule.Setup(m => m.InvokeAsync<string?>("reverseGeocode", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ReturnsAsync("Some address");

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    // Warm up the module cache
    await service.ReverseGeocodeAsync(3.1, 101.6);

    // Dispose twice
    await service.DisposeAsync();
    await service.DisposeAsync();

    mockModule.Verify(m => m.DisposeAsync(), Times.Once);

    // Subsequent calls return null and do not re-import
    var postDisposeAddress = await service.ReverseGeocodeAsync(3.1, 101.6);
    Assert.Null(postDisposeAddress);
    var postDisposeGeocode = await service.GeocodeAsync("Test");
    Assert.Null(postDisposeGeocode);
    mockJs.Verify(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Once);
  }

  [Fact]
  public async Task DisposeAsync_WhenModuleTaskFaulted_DoesNotThrow()
  {
    var mockCatalog = new Mock<IWeatherStationCatalogService>();
    var mockJs = new Mock<IJSRuntime>();

    mockJs.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
        .ThrowsAsync(new JSException("Module failed to load"));

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "fake-key" })
        .Build();
    var service = new PlaceResolutionService(config, mockCatalog.Object, mockJs.Object);

    // Trigger faulted task
    await service.ReverseGeocodeAsync(3.1, 101.6);

    // DisposeAsync should handle faulted task gracefully without throwing
    var exception = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
    Assert.Null(exception);
  }
}
