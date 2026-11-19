namespace MyTransportAppWASM.Core.Services;

/// <summary>
/// Owns lazily created feature service containers so they are disposed with the host DI scope.
/// </summary>
public sealed class FeatureServiceHost : IAsyncDisposable, IDisposable
{
  private readonly object _gate = new();
  private IServiceProvider? _provider;
  private bool _disposed;

  public IServiceProvider GetOrCreate(Func<IServiceProvider> factory)
  {
    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      return _provider ??= factory();
    }
  }

  public void Dispose()
  {
    if (TakeProvider() is IDisposable disposable)
    {
      disposable.Dispose();
    }
  }

  public async ValueTask DisposeAsync()
  {
    var provider = TakeProvider();
    if (provider is IAsyncDisposable asyncDisposable)
    {
      await asyncDisposable.DisposeAsync();
    }
    else if (provider is IDisposable disposable)
    {
      disposable.Dispose();
    }
  }

  private IServiceProvider? TakeProvider()
  {
    lock (_gate)
    {
      if (_disposed)
      {
        return null;
      }

      _disposed = true;
      var provider = _provider;
      _provider = null;
      return provider;
    }
  }
}
