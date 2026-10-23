using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyTransportAppWASM.Utils;

/// <summary>
/// Transient countdown timer. Calling StartAsync again cancels any running
/// countdown and starts fresh. Exposes RemainingSeconds for the parent to
/// bind to; call StateChanged = StateHasChanged so the timer triggers re-renders.
/// </summary>
public sealed class CountdownTimer : IDisposable
{
  private CancellationTokenSource? _cts = new();
  public int RemainingSeconds { get; private set; }

  public async Task StartAsync(int totalSeconds, Action StateChanged, Action onCompleted)
  {
    // Safely cancel and dispose the OLD token before making a new one
    Dispose();

    // Issue a fresh token for this run
    var cts = new CancellationTokenSource();
    _cts = cts;
    CancellationToken token = cts.Token;

    RemainingSeconds = totalSeconds;
    StateChanged.Invoke();

    while (RemainingSeconds > 0)
    {
      try
      {
        await Task.Delay(1000, token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      if (token.IsCancellationRequested) return;

      RemainingSeconds--;
      StateChanged.Invoke();
    }

    if (!token.IsCancellationRequested)
    {
      Interlocked.CompareExchange(ref _cts, null, cts)?.Dispose();
      onCompleted();
    }
  }

  public void Dispose()
  {
    var cts = Interlocked.Exchange(ref _cts, null);
    if (cts is null)
    {
      return;
    }

    if (!cts.IsCancellationRequested)
    {
      try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
    cts.Dispose();
  }
}
