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
        public async Task GetDateFortuneAsync_FallsBackToDefaults_WhenApiReturnsEmptyLists()
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
            Assert.Equal(new[] { "North", "East", "Southeast" }, fortune.FavorableDirections);
            Assert.Equal(new[] { 7, 8, 9, 17, 18 }, fortune.LuckyHours);
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

