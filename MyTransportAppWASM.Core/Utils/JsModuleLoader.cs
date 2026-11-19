namespace MyTransportAppWASM.Core.Utils;

/// <summary>
/// Thread-safe, retryable lazy loader and disposer for an ES module imported via IJSRuntime.
/// </summary>
public sealed class JsModuleLoader : IAsyncDisposable
{
  private readonly IJSRuntime _jsRuntime;
  private readonly string _modulePath;
  private readonly Func<Task>? _prepareAsync;
  private readonly bool _passCancellationToken;
  private readonly Lock _sync = new();
  private Task<IJSObjectReference>? _moduleTask;
  private bool _disposed;

  public JsModuleLoader(
    IJSRuntime jsRuntime,
    string modulePath,
    Func<Task>? prepareAsync = null,
    bool passCancellationToken = false)
  {
    _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    _modulePath = modulePath;
    _prepareAsync = prepareAsync;
    _passCancellationToken = passCancellationToken;
  }

  public bool IsDisposed
  {
    get
    {
      lock (_sync)
      {
        return _disposed;
      }
    }
  }

  public IJSObjectReference? LoadedModule
  {
    get
    {
      lock (_sync)
      {
        return !_disposed && _moduleTask is { IsCompletedSuccessfully: true } task
          ? task.Result
          : null;
      }
    }
  }

  public async ValueTask<IJSObjectReference?> GetIfStartedAsync()
  {
    Task<IJSObjectReference>? moduleTask;
    lock (_sync)
    {
      if (_disposed || _moduleTask is null || _moduleTask.IsFaulted || _moduleTask.IsCanceled)
        return null;

      moduleTask = _moduleTask;
    }

    try
    {
      return await moduleTask;
    }
    catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException || moduleTask.IsFaulted || moduleTask.IsCanceled)
    {
      return null;
    }
  }

  public async Task<IJSObjectReference> GetAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();

    Task<IJSObjectReference> moduleTask;
    lock (_sync)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      moduleTask = GetOrStartImportLocked();
    }

    return await WaitWithCancellationAsync(moduleTask, cancellationToken);
  }

  public async ValueTask<IJSObjectReference?> TryGetAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();

    Task<IJSObjectReference> moduleTask;
    lock (_sync)
    {
      if (_disposed)
        return null;

      moduleTask = GetOrStartImportLocked();
    }

    return await WaitWithCancellationAsync(moduleTask, cancellationToken);
  }

  private Task<IJSObjectReference> GetOrStartImportLocked()
  {
    if (_moduleTask is null || _moduleTask.IsFaulted || _moduleTask.IsCanceled)
    {
      _moduleTask = ImportCoreAsync();
    }

    return _moduleTask;
  }

  private static Task<IJSObjectReference> WaitWithCancellationAsync(
    Task<IJSObjectReference> moduleTask,
    CancellationToken cancellationToken) =>
    cancellationToken.CanBeCanceled ? moduleTask.WaitAsync(cancellationToken) : moduleTask;

  private async Task<IJSObjectReference> ImportCoreAsync()
  {
    var importTask = (_passCancellationToken
      ? _jsRuntime.InvokeAsync<IJSObjectReference>("import", CancellationToken.None, [_modulePath])
      : _jsRuntime.InvokeAsync<IJSObjectReference>("import", _modulePath)).AsTask();

    if (_prepareAsync is null)
    {
      return await importTask;
    }

    try
    {
      await Task.WhenAll(_prepareAsync(), importTask);
      return await importTask;
    }
    catch
    {
      if (importTask.IsCompletedSuccessfully)
      {
        try
        {
          await importTask.Result.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException)
        {
        }
      }
      throw;
    }
  }

  public async ValueTask DisposeAsync()
  {
    Task<IJSObjectReference>? moduleTask;
    lock (_sync)
    {
      if (_disposed)
        return;

      _disposed = true;
      moduleTask = _moduleTask;
      _moduleTask = null;
    }

    if (moduleTask is null)
      return;

    try
    {
      var module = await moduleTask;
      await module.DisposeAsync();
    }
    catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException || moduleTask.IsFaulted || moduleTask.IsCanceled)
    {
      // Loading may have failed while offline, or the browser may be closing during teardown.
    }
  }
}
