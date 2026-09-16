using System.Net;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class LanguageServiceTests
{
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
