using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Weather;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class WeatherLocationInputTests
{
    [Fact]
    public async Task TypedIdPreservesSelectedLocationEvenWhenAnotherLocationHasTheSameCoordinates()
    {
        await WithPageAsync(async (page, renderer, geocoding) =>
        {
            geocoding.Setup(service => service.GetSuggestionsAsync("Ds002"))
                .ReturnsAsync([(3.139, 101.6869, "MET ID: Ds002", "Selected location")]);

            await SubmitAsync(page, renderer, "Ds002");
            await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(page, "LoadPlanAsync"));

            var query = Read<WeatherQuery>(page, "query");
            Assert.Equal("Ds002", query.LocationId);
            Assert.Equal("Selected location", query.PlaceName);
            Assert.True(Read<bool>(page, "isFetched"));
            geocoding.Verify(service => service.GetAllLocationsAsync(), Times.Once);
        });
    }

    [Fact]
    public async Task CoordinatesKeepExactPositionWhileResolvingNearestForecastId()
    {
        await WithPageAsync(async (page, renderer, geocoding) =>
        {
            geocoding.Setup(service => service.GetSuggestionsAsync("3.14, 101.69"))
                .ReturnsAsync([(3.14, 101.69, "Latitude, longitude", "3.14, 101.69")]);

            await SubmitAsync(page, renderer, "3.14, 101.69");

            var query = Read<WeatherQuery>(page, "query");
            Assert.Equal(3.14, query.Latitude);
            Assert.Equal(101.69, query.Longitude);
            Assert.Equal("Ds001", query.LocationId);
            Assert.Equal("3.14, 101.69", query.PlaceName);
            Assert.True(Read<bool>(page, "isFetched"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrAmbiguousSearchDoesNotFetchThePreviousLocation(bool ambiguous)
    {
        await WithPageAsync(async (page, renderer, geocoding) =>
        {
            geocoding.Setup(service => service.GetSuggestionsAsync("input"))
                .ReturnsAsync(ambiguous
                    ? [(1, 100, "MET ID: Ds003", "First"), (2, 101, "MET ID: Ds004", "Second")]
                    : []);

            await SubmitAsync(page, renderer, "input");

            Assert.False(Read<bool>(page, "isFetched"));
            Assert.Contains(ambiguous ? "Several" : "Enter a known", Read<string>(page, "errorMessage"));
            Assert.Equal("Ds001", Read<WeatherQuery>(page, "query").LocationId);
        });
    }

    [Theory]
    [InlineData("departureDate", -1)]
    [InlineData("returnDate", WeatherForecastLimits.OpenMeteoForecastDays)]
    public async Task DateOutsideForecastWindowDoesNotFetch(string field, int dayOffset)
    {
        await WithPageAsync(async (page, renderer, _) =>
        {
            Write(page, field, MalaysiaTime.Today.AddDays(dayOffset));

            await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(page, "LoadPlanAsync"));

            Assert.False(Read<bool>(page, "isFetched"));
            Assert.Contains($"{WeatherForecastLimits.OpenMeteoForecastDays}-day forecast window",
                Read<string>(page, "errorMessage"));
        });
    }

    private static Task SubmitAsync(Weather page, HtmlRenderer renderer, string input) =>
        renderer.Dispatcher.InvokeAsync(async () =>
        {
            await InvokeAsync(page, "HandleSearchChanged", input);
            await InvokeAsync(page, "LoadPlanAsync");
        });

    private static async Task WithPageAsync(Func<Weather, HtmlRenderer, Mock<IGeocodingService>, Task> test)
    {
        var geocoding = new Mock<IGeocodingService>();
        geocoding.Setup(service => service.GetAllLocationsAsync()).ReturnsAsync(
        [
            new() { LocationId = "Ds001", LocationName = "Nearest location", Latitude = 3.139, Longitude = 101.6869 },
            new() { LocationId = "Ds002", LocationName = "Selected location", Latitude = 3.139, Longitude = 101.6869 }
        ]);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IGeocodingService>(geocoding.Object)
            .AddSingleton(Mock.Of<ILocationService>())
            .AddSingleton(Mock.Of<IJSRuntime>())
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(new LanguageService(new HttpClient()))
            .AddSingleton<IComponentActivator>(activator)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<Weather>());
        await test(activator.Page!, renderer, geocoding);
    }

    private static T Read<T>(Weather page, string field) =>
        (T)typeof(Weather).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void Write<T>(Weather page, string field, T value) =>
        typeof(Weather).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static async Task InvokeAsync(Weather page, string method, params object[] args)
    {
        var result = typeof(Weather).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
        if (result is Task task) await task;
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public Weather? Page { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is Weather page) Page = page;
            return component;
        }
    }
}
