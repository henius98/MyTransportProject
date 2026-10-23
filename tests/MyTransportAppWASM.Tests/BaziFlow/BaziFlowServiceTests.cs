using System.Net;
using System.Text;
using Moq;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

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
            Assert.False(await service.CreateProfileAsync(new MyTransportAppWASM.Models.BaziFlow.CreateProfileRequest()));
            Assert.Null(await service.GetDateFortuneAsync("2026-10-12"));
        }

        private sealed class FakeHttpHandler(string json) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}

