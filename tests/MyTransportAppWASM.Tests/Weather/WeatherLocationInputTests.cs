using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class WeatherLocationInputTests
{
    [Fact]
    public async Task TypedIdPreservesSelectedLocationEvenWhenAnotherLocationHasTheSameCoordinates()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("Ds002"))
            .ReturnsAsync([(3.139, 101.6869, "MET ID: Ds002", "Selected location")]);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "Ds002";

        var plan = await planner.GetPlanAsync(query);

        Assert.True(plan.IsValid);
        Assert.Equal("Ds002", query.LocationId);
        Assert.Equal("Selected location", query.PlaceName);
        stationCatalog.Verify(
            service => service.FindNearestStationAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CoordinatesKeepExactPositionWhileResolvingNearestForecastId()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("3.14, 101.69"))
            .ReturnsAsync([(3.14, 101.69, "Latitude, longitude", "3.14, 101.69")]);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "3.14, 101.69";

        var plan = await planner.GetPlanAsync(query);

        Assert.True(plan.IsValid);
        Assert.Equal(3.14, query.Latitude);
        Assert.Equal(101.69, query.Longitude);
        Assert.Equal("Ds001", query.LocationId);
        Assert.Equal("3.14, 101.69", query.PlaceName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrAmbiguousSearchDoesNotFetchThePreviousLocation(bool ambiguous)
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("input"))
            .ReturnsAsync(ambiguous
                ? [(1, 100, "MET ID: Ds003", "First"), (2, 101, "MET ID: Ds004", "Second")]
                : []);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "input";

        var plan = await planner.GetPlanAsync(query);

        Assert.False(plan.IsValid);
        Assert.Contains(ambiguous ? "Several" : "Enter a known", plan.ValidationError);
        Assert.Equal("Ds001", query.LocationId);
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(false, WeatherForecastLimits.OpenMeteoForecastDays)]
    public async Task DateOutsideForecastWindowDoesNotFetch(bool isDeparture, int dayOffset)
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        if (isDeparture)
        {
            query.Departure = MalaysiaTime.At(MalaysiaTime.Today.AddDays(dayOffset));
        }
        else
        {
            query.Return = MalaysiaTime.At(MalaysiaTime.Today.AddDays(dayOffset));
        }

        var plan = await planner.GetPlanAsync(query);

        Assert.False(plan.IsValid);
        Assert.Contains(
            $"{WeatherForecastLimits.OpenMeteoForecastDays}-day forecast window",
            plan.ValidationError);
    }

    [Fact]
    public async Task MutatingQueryAfterGetPlanAsync_DoesNotMutateActivePlanQuery()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);

        var plan = await planner.GetPlanAsync(query);

        // Simulate user editing form coordinates before clicking "Get Forecast"
        query.Latitude = 5.4141;
        query.Longitude = 100.3288;
        query.PlaceName = null;
        query.LocationId = null;

        Assert.Equal(3.1390, plan.Query.Latitude);
        Assert.Equal(101.6869, plan.Query.Longitude);
        Assert.Equal("Nearest location", plan.Query.PlaceName);
        Assert.Equal("Ds001", plan.Query.LocationId);
    }

    private static (Mock<IPlaceResolutionService> PlaceResolution, Mock<IWeatherStationCatalogService> StationCatalog) CreateMocks()
    {
        var placeResolution = new Mock<IPlaceResolutionService>();
        var stationCatalog = new Mock<IWeatherStationCatalogService>();
        stationCatalog
            .Setup(service => service.FindNearestStationAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetLocation { LocationId = "Ds001", LocationName = "Nearest location", Latitude = 3.139, Longitude = 101.6869 });
        return (placeResolution, stationCatalog);
    }

    private static IWeatherPlannerService CreatePlanner(
        IPlaceResolutionService placeResolution,
        IWeatherStationCatalogService stationCatalog)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new WeatherPlannerService(
            new HttpClient(new StubHandler()),
            cache,
            Options.Create(new CacheExpirationOptions
            {
                Weather = new() { LiveSeconds = 600, OutlookSeconds = 10800, FallbackSeconds = 30 }
            }),
            Options.Create(new WeatherOptions()),
            placeResolution,
            stationCatalog);
    }

    private static async Task<WeatherQuery> CreateInitializedQueryAsync(IWeatherPlannerService planner)
    {
        var query = new WeatherQuery
        {
            Latitude = 3.1390,
            Longitude = 101.6869,
            Departure = MalaysiaTime.At(MalaysiaTime.Today),
            Return = MalaysiaTime.At(MalaysiaTime.Today.AddDays(3))
        };
        await planner.ResolveNearestLocationAsync(query, updatePlaceName: true);
        return query;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }
}
