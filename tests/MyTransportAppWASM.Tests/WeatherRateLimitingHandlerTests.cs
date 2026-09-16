using System.Net;
using Moq;
using Moq.Protected;
using Xunit;
using MyTransportAppWASM.Services.Handlers;

namespace MyTransportAppWASM.Tests
{
    public class WeatherRateLimitingHandlerTests
    {
        [Fact]
        public async Task SendAsync_ThrottlesRequests_ToSameHost()
        {
            // Arrange
            var handler = new WeatherRateLimitingHandler()
            {
                InnerHandler = new Mock<HttpMessageHandler>().Object
            };

            var mockInnerHandler = Mock.Get(handler.InnerHandler);
            mockInnerHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var client = new HttpClient(handler);
            var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.met.gov.my/data");
            var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.met.gov.my/other");

            // Act
            // First request should pass immediately
            var response1 = await client.SendAsync(request1);
            
            // Second request to same host within 500ms might be queued or delayed.
            // Since we use a rate limiter with 1 per 500ms, the second call will take ~500ms
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var response2 = await client.SendAsync(request2);
            watch.Stop();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
            
            // Should have waited roughly 500ms (allow some tolerance for test runner)
            Assert.True(watch.ElapsedMilliseconds >= 400, $"Elapsed was {watch.ElapsedMilliseconds}ms, expected >= 400ms");
            
            mockInnerHandler.Verify();
        }

        [Fact]
        public async Task SendAsync_DoesNotThrottle_DifferentHosts()
        {
            // Arrange
            var handler = new WeatherRateLimitingHandler()
            {
                InnerHandler = new Mock<HttpMessageHandler>().Object
            };

            var mockInnerHandler = Mock.Get(handler.InnerHandler);
            mockInnerHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var client = new HttpClient(handler);
            var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.met.gov.my/data");
            var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.open-meteo.com/v1");

            // Act
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task1 = client.SendAsync(request1);
            var task2 = client.SendAsync(request2);
            await Task.WhenAll(task1, task2);
            watch.Stop();

            var response1 = await task1;
            var response2 = await task2;
            Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
            
            // Should be very fast (less than 500ms delay) because different hosts
            Assert.True(watch.ElapsedMilliseconds < 400, $"Elapsed was {watch.ElapsedMilliseconds}ms, expected < 400ms");
            
            mockInnerHandler.Verify();
        }
    }
}
