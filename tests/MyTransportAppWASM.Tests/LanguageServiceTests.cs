using System.Net;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class LanguageServiceTests
{
    [Theory]
    [InlineData("Tiada hujan", "No rain")]
    [InlineData("Tiada Hujan", "No rain")]
    [InlineData("TIADA HUJAN", "No rain")]
    [InlineData("Unknown weather", "Unknown weather")]
    public async Task TranslationLookupIgnoresCasingAndPreservesUnknownKeys(string key, string expected)
    {
        using var client = new HttpClient(new WeatherLanguageHandler()) { BaseAddress = new Uri("https://app.example/") };
        var language = new LanguageService(client);

        await language.LoadLanguageAsync("en-US");

        Assert.Equal(expected, language[key]);
    }

    [Fact]
    public async Task SlowPreviousAccountsLanguageDoesNotOverrideCurrentLanguage()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new LanguageHandler(started, resume)) { BaseAddress = new Uri("https://app.example/") };
        var language = new LanguageService(client);
        await language.LoadLanguageAsync("en-US");
        var previousAccount = language.LoadLanguageAsync("ms-MY");
        await started.Task;

        await language.LoadLanguageAsync("en-US");
        resume.SetResult();
        await previousAccount;

        Assert.Equal("en-US", language.CurrentLanguageName);
        Assert.Equal("Home", language["Home"]);
    }

    private sealed class WeatherLanguageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Tiada hujan\":\"No rain\"}")
            });
        }
    }

    private sealed class LanguageHandler(TaskCompletionSource started, TaskCompletionSource resume) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("ms-MY"))
            {
                started.SetResult();
                await resume.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Home\":\"Utama\"}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Home\":\"Home\"}") };
        }
    }
}
