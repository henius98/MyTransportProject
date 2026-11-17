using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Features.Weather.Components;

namespace MyTransportAppWASM.Tests;

public class WeatherLocationInputTests
{
    [Fact]
    public async Task TypedIdPreservesSelectedLocationEvenWhenAnotherLocationHasTheSameCoordinates()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("Ds002", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlaceSuggestion(3.139, 101.6869, "Selected location", "Selected location", "Ds002")]);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "Ds002";

        var plan = await planner.GetPlanAsync(query);

        Assert.True(plan.IsValid);
        Assert.Equal("Ds002", plan.Query.LocationId);
        Assert.Equal("Selected location", plan.Query.PlaceName);
        Assert.Null(plan.Query.PendingSearchText);
        // Caller's input WeatherQuery must remain untouched by GetPlanAsync
        Assert.Equal("Ds001", query.LocationId);
        Assert.Equal("Ds002", query.PendingSearchText);
        stationCatalog.Verify(
            service => service.FindNearestStationAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CoordinatesKeepExactPositionWhileResolvingNearestForecastId()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("3.14, 101.69", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlaceSuggestion(3.14, 101.69, "Latitude, longitude", "3.14, 101.69")]);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "3.14, 101.69";

        var plan = await planner.GetPlanAsync(query);

        Assert.True(plan.IsValid);
        Assert.Equal(3.14, plan.Query.Latitude);
        Assert.Equal(101.69, plan.Query.Longitude);
        Assert.Equal("Ds001", plan.Query.LocationId);
        Assert.Equal("3.14, 101.69", plan.Query.PlaceName);
        Assert.Equal(3.1390, query.Latitude);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrAmbiguousSearchDoesNotFetchThePreviousLocation(bool ambiguous)
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution.Setup(service => service.GetSuggestionsAsync("input", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ambiguous
                ? [new PlaceSuggestion(1, 100, "First", "First", "Ds003"), new PlaceSuggestion(2, 101, "Second", "Second", "Ds004")]
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

    [Fact]
    public async Task GetPlanAsync_ForwardsCancellationToken_ToPlaceResolutionSuggestions()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        using var cts = new CancellationTokenSource();
        placeResolution.Setup(service => service.GetSuggestionsAsync("Kuala Lumpur", cts.Token))
            .ReturnsAsync([new PlaceSuggestion(3.139, 101.6869, "Kuala Lumpur", "Kuala Lumpur", "Ds002")]);

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "Kuala Lumpur";

        var plan = await planner.GetPlanAsync(query, cts.Token);

        Assert.True(plan.IsValid);
        placeResolution.Verify(
            service => service.GetSuggestionsAsync("Kuala Lumpur", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task GetPlanAsync_WhenCancelledDuringSuggestions_ThrowsOperationCanceledException()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        using var cts = new CancellationTokenSource();
        placeResolution.Setup(service => service.GetSuggestionsAsync("Kuala Lumpur", cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var planner = CreatePlanner(placeResolution.Object, stationCatalog.Object);
        var query = await CreateInitializedQueryAsync(planner);
        query.PendingSearchText = "Kuala Lumpur";

        await Assert.ThrowsAsync<OperationCanceledException>(() => planner.GetPlanAsync(query, cts.Token));
    }

    [Fact]
    public async Task LocationSearchAutocomplete_RapidTypingSelectionAndDisposal_DoesNotThrow()
    {
        var (placeResolution, stationCatalog) = CreateMocks();
        placeResolution
            .Setup(s => s.GetSuggestionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlaceSuggestion(3.139, 101.6869, "Kuala Lumpur", "Kuala Lumpur", "Ds002")]);

        var activator = new AutocompleteActivator();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(placeResolution.Object)
            .AddSingleton(stationCatalog.Object)
            .AddSingleton(Mock.Of<IJSRuntime>())
            .AddSingleton<IComponentActivator>(activator)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        LocationSearchAutocomplete? component = null;
        var disposeDuringCallback = false;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<LocationSearchAutocomplete>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LocationSearchAutocomplete.Value)] = "",
                [nameof(LocationSearchAutocomplete.ValueChanged)] = EventCallback.Factory.Create<string>(this, async _ =>
                {
                    if (disposeDuringCallback && component is not null)
                    {
                        await component.DisposeAsync();
                    }
                })
            }));
            component = activator.Component!;

            var searchField = typeof(LocationSearchAutocomplete).GetField("_searchText", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var triggerSearch = typeof(LocationSearchAutocomplete).GetMethod("TriggerSearch", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var selectSuggestion = typeof(LocationSearchAutocomplete).GetMethod("SelectSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!;

            searchField.SetValue(component, "Ku");
            await (Task)triggerSearch.Invoke(component, null)!;

            searchField.SetValue(component, "Kuala");
            await (Task)triggerSearch.Invoke(component, null)!;

            await (Task)selectSuggestion.Invoke(component, [new PlaceSuggestion(3.139, 101.6869, "Kuala Lumpur", "Kuala Lumpur", "Ds002")])!;

            searchField.SetValue(component, " ");
            await (Task)triggerSearch.Invoke(component, null)!;

            disposeDuringCallback = true;
            searchField.SetValue(component, "Penang");
            await (Task)triggerSearch.Invoke(component, null)!;
        });
    }

    private sealed class AutocompleteActivator : IComponentActivator
    {
        public LocationSearchAutocomplete? Component { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is LocationSearchAutocomplete autocomplete) Component = autocomplete;
            return component;
        }
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
