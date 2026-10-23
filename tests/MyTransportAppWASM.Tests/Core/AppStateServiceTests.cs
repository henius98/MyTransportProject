using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class AppStateServiceTests
{
    [Fact]
    public void RequestShowLogin_PersistsRequest_WhenNoSubscriberExistsYet()
    {
        var state = new AppStateService();

        state.RequestShowLogin();

        Assert.True(state.IsLoginRequested);
    }

    [Fact]
    public void TakeReturnUrl_ReturnsAndClearsPendingUrl()
    {
        var state = new AppStateService
        {
            ReturnUrl = "https://example.test/weather"
        };

        var result = state.TakeReturnUrl();

        Assert.Equal("https://example.test/weather", result);
        Assert.Null(state.ReturnUrl);
    }
}
