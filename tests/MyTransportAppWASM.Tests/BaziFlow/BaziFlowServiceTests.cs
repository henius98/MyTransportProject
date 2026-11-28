using System.Net;
using System.Text;
using Moq;

namespace MyTransportAppWASM.Tests
{
  public class BaziFlowServiceTests
  {
    [Fact]
    public async Task GetDateFortuneAsync_PreservesApiDirectionsAndHours_WhenReturnedByApi()
    {
      var json = """
            {
                "status": "success",
                "data": {
                    "almanac": "Good day for travel",
                    "analysis": "Smooth commute",
                    "favorableDirections": ["South", "West"],
                    "luckyHours": [10, 14, 20]
                }
            }
            """;

      var handler = new FakeHttpHandler(json);
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var mockStorage = new Mock<IBrowserStorageService>();
      var service = new BaziFlowService(client, mockStorage.Object);

      var fortune = await service.GetDateFortuneAsync("2026-10-12");

      Assert.NotNull(fortune);
      Assert.Equal("Good day for travel", fortune.Almanac);
      Assert.Equal(new[] { "South", "West" }, fortune.FavorableDirections);
      Assert.Equal(new[] { 10, 14, 20 }, fortune.LuckyHours);
    }

    [Fact]
    public async Task GetDateFortuneAsync_PreservesEmptyLists_WhenApiReturnsEmptyLists()
    {
      var json = """
            {
                "status": "success",
                "data": {
                    "almanac": "Fair day",
                    "analysis": "Normal",
                    "favorableDirections": [],
                    "luckyHours": []
                }
            }
            """;

      var handler = new FakeHttpHandler(json);
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var mockStorage = new Mock<IBrowserStorageService>();
      var service = new BaziFlowService(client, mockStorage.Object);

      var fortune = await service.GetDateFortuneAsync("2026-10-12");

      Assert.NotNull(fortune);
      Assert.Empty(fortune.FavorableDirections);
      Assert.Empty(fortune.LuckyHours);
    }

    [Fact]
    public async Task WhenBaseAddressIsNull_MethodsDegradeGracefullyWithoutHttpCalls()
    {
      var handler = new FakeHttpHandler("{}");
      var client = new HttpClient(handler);
      var mockStorage = new Mock<IBrowserStorageService>();
      mockStorage.Setup(s => s.GetStringAsync(It.IsAny<string>())).ReturnsAsync("stored-key");
      var service = new BaziFlowService(client, mockStorage.Object);

      Assert.False(await service.HasApiKeyAsync());
      Assert.Null(await service.GetProfileAsync());
      Assert.False(await service.CreateProfileAsync(new CreateProfileRequest()));
      Assert.Null(await service.GetDateFortuneAsync("2026-10-12"));
    }

    [Fact]
    public async Task SetupProfileAsync_SavesKeyOnSuccess_AndRollsBackOnFailure()
    {
      var successClient = new HttpClient(new FakeHttpHandler("{\"status\":\"success\"}"))
      {
        BaseAddress = new Uri("https://api.example.com")
      };
      var storage = new Mock<IBrowserStorageService>();
      var service = new BaziFlowService(successClient, storage.Object);

      var ok = await service.SetupProfileAsync("valid-key", new CreateProfileRequest { Location = "Malaysia" });
      Assert.True(ok);
      storage.Verify(s => s.SetStringAsync(BaziFlowService.ApiKeyStorageKey, "valid-key", It.IsAny<CancellationToken>()), Times.Once);
      storage.Verify(s => s.SetStringAsync(BaziFlowService.ApiKeyStorageKey, string.Empty, It.IsAny<CancellationToken>()), Times.Never);

      var failHandler = new FakeHttpHandler("{}", HttpStatusCode.Unauthorized);
      var failClient = new HttpClient(failHandler) { BaseAddress = new Uri("https://api.example.com") };
      var failStorage = new Mock<IBrowserStorageService>();
      var failService = new BaziFlowService(failClient, failStorage.Object);

      var failed = await failService.SetupProfileAsync("bad-key", new CreateProfileRequest { Location = "Malaysia" });
      Assert.False(failed);
      failStorage.Verify(s => s.SetStringAsync(BaziFlowService.ApiKeyStorageKey, "bad-key", It.IsAny<CancellationToken>()), Times.Once);
      failStorage.Verify(s => s.SetStringAsync(BaziFlowService.ApiKeyStorageKey, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsProfileData_WhenApiSucceeds()
    {
      var json = """
            {
              "status": "success",
              "data": {
                "profile": {
                  "gender": "Male",
                  "solarDate": "1996-01-01",
                  "baziSummary": "Balanced"
                }
              }
            }
            """;
      var client = new HttpClient(new FakeHttpHandler(json)) { BaseAddress = new Uri("https://api.example.com") };
      var service = new BaziFlowService(client, new Mock<IBrowserStorageService>().Object);

      var data = await service.GetProfileAsync();

      Assert.NotNull(data?.Profile);
      Assert.Equal("1996-01-01", data!.Profile!.SolarDate);
      Assert.Equal("Balanced", data.Profile.BaziSummary);
    }

    [Fact]
    public async Task GetDateFortuneAsync_CachesInMemoryAndLocalStorageAcrossSessions()
    {
      var json = BuildFortuneJson("Good day for travel", ["South"], [10, 14]);
      var handler = new CountingHttpHandler(_ => json);
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var storage = new InMemoryBrowserStorageService();
      var service = new BaziFlowService(client, storage);

      var first = await service.GetDateFortuneAsync("2026-10-12");
      var second = await service.GetDateFortuneAsync("2026-10-12");

      Assert.NotNull(first);
      Assert.Same(first, second);
      Assert.Equal(1, handler.CallCount);
      Assert.Equal(1, storage.GetCount(BaziFlowService.FortuneCacheStorageKey));
      Assert.True(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));

      // Fresh service instance (new session) reads from localStorage without an HTTP call.
      var reloadedService = new BaziFlowService(client, storage);
      var fromStorage = await reloadedService.GetDateFortuneAsync("2026-10-12");

      Assert.NotNull(fromStorage);
      Assert.Equal("Good day for travel", fromStorage.Almanac);
      Assert.Equal(new[] { "South" }, fromStorage.FavorableDirections);
      Assert.Equal(new[] { 10, 14 }, fromStorage.LuckyHours);
      Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetDateFortuneAsync_OverwritesSingleStorageKeyOnDateRollover()
    {
      var responses = new Queue<string>(
      [
        BuildFortuneJson("Day 1", ["East"], [8]),
        BuildFortuneJson("Day 2", ["North"], [16])
      ]);
      var handler = new CountingHttpHandler(_ => responses.Dequeue());
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var storage = new InMemoryBrowserStorageService();
      var service = new BaziFlowService(client, storage);

      var day1 = await service.GetDateFortuneAsync("2026-10-12");
      var day2 = await service.GetDateFortuneAsync("2026-10-13");

      Assert.Equal("Day 1", day1?.Almanac);
      Assert.Equal("Day 2", day2?.Almanac);
      Assert.Equal(2, handler.CallCount);
      Assert.Contains("\"date\":\"2026-10-13\"", storage.Values[BaziFlowService.FortuneCacheStorageKey]);
      Assert.Single(storage.Values);
    }

    [Fact]
    public async Task GetDateFortuneAsync_FallsBackToApiWhenStoredJsonIsCorrupt()
    {
      var handler = new CountingHttpHandler(_ => BuildFortuneJson("Fresh after corrupt", ["West"], [12]));
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var storage = new InMemoryBrowserStorageService();
      storage.Values[BaziFlowService.FortuneCacheStorageKey] = "{not valid json";
      var service = new BaziFlowService(client, storage);

      var fortune = await service.GetDateFortuneAsync("2026-10-12");

      Assert.NotNull(fortune);
      Assert.Equal("Fresh after corrupt", fortune.Almanac);
      Assert.Equal(1, handler.CallCount);
      Assert.Contains("Fresh after corrupt", storage.Values[BaziFlowService.FortuneCacheStorageKey]);
    }

    [Fact]
    public async Task SaveApiKeyAsync_ClearsFortuneCacheWhenKeyChangesOrClears()
    {
      var responses = new Queue<string>(
      [
        BuildFortuneJson("Account A", ["East"], [9]),
        BuildFortuneJson("Account B", ["South"], [15]),
        BuildFortuneJson("Account C", ["North"], [19])
      ]);
      var handler = new CountingHttpHandler(_ => responses.Dequeue());
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var storage = new InMemoryBrowserStorageService();
      storage.Values[BaziFlowService.ApiKeyStorageKey] = "key-a";
      var service = new BaziFlowService(client, storage);

      var initial = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Account A", initial?.Almanac);
      Assert.True(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));

      // Saving the identical non-empty key retains cached fortune.
      await service.SaveApiKeyAsync("key-a");
      var unchanged = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Account A", unchanged?.Almanac);
      Assert.Equal(1, handler.CallCount);

      // Changing the key clears both localStorage and in-memory fortune.
      await service.SaveApiKeyAsync("key-b");
      Assert.False(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));
      var afterChange = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Account B", afterChange?.Almanac);
      Assert.Equal(2, handler.CallCount);

      // Clearing the key clears both localStorage and in-memory fortune.
      await service.SaveApiKeyAsync(string.Empty);
      Assert.False(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));
      var afterClear = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Account C", afterClear?.Almanac);
      Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task SetupProfileAsync_ClearsFortuneCacheWhenApiKeyIsUnchanged()
    {
      var fortuneResponses = new Queue<string>(
      [
        BuildFortuneJson("Old Birth Profile", ["East"], [9]),
        BuildFortuneJson("Updated Birth Profile", ["South"], [14])
      ]);
      var handler = new CountingHttpHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
          ? """{"status":"success"}"""
          : fortuneResponses.Dequeue());
      var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
      var storage = new InMemoryBrowserStorageService();
      storage.Values[BaziFlowService.ApiKeyStorageKey] = "same-key";
      var service = new BaziFlowService(client, storage);

      var initial = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Old Birth Profile", initial?.Almanac);
      Assert.True(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));

      var created = await service.SetupProfileAsync("same-key", new CreateProfileRequest
      {
        Gender = 1,
        BirthDate = "1995-05-05",
        BirthHour = 10,
        BirthMinute = 30,
        Location = "Malaysia"
      });

      Assert.True(created);
      Assert.False(storage.Values.ContainsKey(BaziFlowService.FortuneCacheStorageKey));

      var refreshed = await service.GetDateFortuneAsync("2026-10-12");
      Assert.Equal("Updated Birth Profile", refreshed?.Almanac);
      Assert.Equal(3, handler.CallCount);
    }

    private static string BuildFortuneJson(string almanac, string[] directions, int[] luckyHours) => $$"""
      {
        "status": "success",
        "data": {
          "almanac": "{{almanac}}",
          "analysis": "Summary",
          "favorableDirections": [{{string.Join(",", directions.Select(d => $"\"{d}\""))}}],
          "luckyHours": [{{string.Join(",", luckyHours)}}]
        }
      }
      """;

    private sealed class FakeHttpHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
        return Task.FromResult(new HttpResponseMessage(status)
        {
          Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
      }
    }

    private sealed class CountingHttpHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
      public int CallCount { get; private set; }

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
        CallCount++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
        });
      }
    }

    private sealed class InMemoryBrowserStorageService : IBrowserStorageService
    {
      private readonly Dictionary<string, int> _getCounts = new(StringComparer.Ordinal);
      public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

      public int GetCount(string key) => _getCounts.GetValueOrDefault(key);

      public ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
      {
        _getCounts[key] = _getCounts.GetValueOrDefault(key) + 1;
        return ValueTask.FromResult(Values.TryGetValue(key, out var value) ? value : null);
      }

      public ValueTask SetStringAsync(string key, string value, CancellationToken cancellationToken = default)
      {
        Values[key] = value;
        return ValueTask.CompletedTask;
      }

      public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
      {
        Values.Remove(key);
        return ValueTask.CompletedTask;
      }
    }
  }
}

