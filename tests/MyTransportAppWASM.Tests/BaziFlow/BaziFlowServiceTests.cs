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
  }
}

