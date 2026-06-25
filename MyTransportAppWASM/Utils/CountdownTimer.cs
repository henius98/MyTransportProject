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
  private CancellationTokenSource _cts = new();
  public int RemainingSeconds { get; private set; }

  public async Task StartAsync(int totalSeconds, Action StateChanged, Action onCompleted)
  {
    // Safely cancel and dispose the OLD token before making a new one
    if (!_cts.IsCancellationRequested)
    {
      try { _cts.Cancel(); } catch { }
    }
    _cts.Dispose();

    // Issue a fresh token for this run
    _cts = new CancellationTokenSource();
    CancellationToken token = _cts.Token;

    RemainingSeconds = totalSeconds;
    StateChanged.Invoke();

    while (RemainingSeconds > 0)
    {
      await Task.Delay(1000, token).ConfigureAwait(false);
      if (token.IsCancellationRequested) return;

      RemainingSeconds--;
      StateChanged.Invoke();
    }

    if (!token.IsCancellationRequested)
    {
      onCompleted();
      Dispose();
    }
  }

  public void Dispose()
  {
    if (!_cts.IsCancellationRequested)
    {
      _cts.Cancel();
    }
    _cts.Dispose();
  }
}
