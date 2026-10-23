using Moq;
using Moq.Protected;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Xunit;
using MyTransportAppWASM.Services.Handlers; // Will be created in GREEN step
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Tests
{
    public class BaziFlowAuthHandlerTests
    {
        [Fact]
        public async Task SendAsync_AddsBearerToken_WhenApiKeyExists()
        {
            // Arrange
            var mockLocalStorage = new Mock<IBrowserStorageService>();
            mockLocalStorage.Setup(x => x.GetStringAsync("baziflow_api_key", It.IsAny<CancellationToken>()))
                .ReturnsAsync("test_api_key");

            var handler = new BaziFlowAuthHandler(mockLocalStorage.Object)
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
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK))
                .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                {
                    Assert.NotNull(req.Headers.Authorization);
                    Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
                    Assert.Equal("test_api_key", req.Headers.Authorization.Parameter);
                })
                .Verifiable();

            var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

            // Act
            await client.SendAsync(request);

            // Assert
            mockInnerHandler.Verify();
        }

        [Fact]
        public async Task SendAsync_DoesNotAddBearerToken_WhenHostDoesNotMatch()
        {
            var mockLocalStorage = new Mock<IBrowserStorageService>();
            mockLocalStorage.Setup(x => x.GetStringAsync("baziflow_api_key", It.IsAny<CancellationToken>()))
                .ReturnsAsync("test_api_key");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["BaziFlow:BaseUrl"] = "https://baziflow.example.com" })
                .Build();

            var handler = new BaziFlowAuthHandler(mockLocalStorage.Object, config)
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
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK))
                .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                {
                    Assert.Null(req.Headers.Authorization);
                })
                .Verifiable();

            var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://thirdparty.example.com/api");

            await client.SendAsync(request);

            mockInnerHandler.Verify();
        }
    }
}
