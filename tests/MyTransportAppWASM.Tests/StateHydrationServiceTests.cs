using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class StateHydrationServiceTests
{
    [Fact]
    public async Task GetOrHydrateAsync_WithoutPersistentState_ExecutesFactory()
    {
        using var service = new StateHydrationService(persistentState: null);
        var factoryCallCount = 0;

        var result = await service.GetOrHydrateAsync("test-key", () =>
        {
            factoryCallCount++;
            return Task.FromResult(new WeatherQuery { Latitude = 3.14, Longitude = 101.69 });
        });

        Assert.NotNull(result);
        Assert.Equal(3.14, result.Latitude);
        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public void TryTake_WithoutPersistentState_ReturnsFalse()
    {
        using var service = new StateHydrationService(persistentState: null);

        var found = service.TryTake<WeatherQuery>("missing-key", out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrHydrateAsync_ThrowsOnNullOrEmptyKey()
    {
        using var service = new StateHydrationService(persistentState: null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetOrHydrateAsync<string>("", () => Task.FromResult("val")));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetOrHydrateAsync<string>("valid-key", null!));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new StateHydrationService(persistentState: null);

        service.Dispose();
        service.Dispose(); // second call must not throw
    }
}
