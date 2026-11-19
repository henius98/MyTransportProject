namespace MyTransportAppWASM.Core.Services;

internal static class HttpClientDefaults
{
  internal static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(15);
}

public interface IAppHttpClientFactory
{
  HttpClient ExternalApi { get; }
  HttpClient StaticAssets { get; }
  HttpMessageHandler CreateExternalHandler();
}

public sealed class AppHttpClientFactory : IAppHttpClientFactory, IDisposable
{
  private readonly Func<HttpMessageHandler> _handlerFactory;

  public HttpClient ExternalApi { get; }
  public HttpClient StaticAssets { get; }

  public AppHttpClientFactory(Uri staticAssetsBaseAddress, Func<HttpMessageHandler>? handlerFactory = null)
  {
    _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
    ExternalApi = new HttpClient(_handlerFactory(), disposeHandler: true)
    {
      Timeout = HttpClientDefaults.ExternalTimeout
    };
    StaticAssets = new HttpClient(_handlerFactory(), disposeHandler: true)
    {
      BaseAddress = staticAssetsBaseAddress,
      Timeout = HttpClientDefaults.ExternalTimeout
    };
  }

  public HttpMessageHandler CreateExternalHandler() => _handlerFactory();

  public void Dispose()
  {
    ExternalApi.Dispose();
    StaticAssets.Dispose();
  }
}
