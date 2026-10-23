using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class CountdownTimerTests
{
  [Fact]
  public async Task StartAsync_WhenCompletedAndComponentDisposesTimer_DoesNotThrowObjectDisposedException()
  {
    var timer = new CountdownTimer();
    var completed = false;

    await timer.StartAsync(0, () => { }, () => completed = true);

    Assert.True(completed);
    var exception = Record.Exception(() => timer.Dispose());
    Assert.Null(exception);
  }

  [Fact]
  public async Task StartAsync_WhenDisposedDuringActiveCountdown_CompletesCleanlyWithoutThrowing()
  {
    var timer = new CountdownTimer();
    var completed = false;

    var runTask = timer.StartAsync(5, () => { }, () => completed = true);
    timer.Dispose();

    var exception = await Record.ExceptionAsync(() => runTask);
    Assert.Null(exception);
    Assert.False(completed);
  }
}
