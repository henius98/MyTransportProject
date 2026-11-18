using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;

namespace MyTransportAppWASM.Tests;

public class JsModuleLoaderTests
{
  [Fact]
  public async Task GetAsync_SharesConcurrentImport_AndRetriesAfterFailure()
  {
    var js = new Mock<IJSRuntime>(MockBehavior.Strict);
    var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
    var importTcs = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);

    js.SetupSequence(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
        .Returns(ValueTask.FromException<IJSObjectReference>(new JSException("Offline")))
        .Returns(new ValueTask<IJSObjectReference>(importTcs.Task));
    module.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

    await using var loader = new JsModuleLoader(js.Object, "./js/sample.js");

    await Assert.ThrowsAsync<JSException>(() => loader.GetAsync());

    var first = loader.GetAsync();
    var second = loader.GetAsync();
    Assert.Null(loader.LoadedModule);

    importTcs.SetResult(module.Object);
    Assert.Same(module.Object, await first);
    Assert.Same(module.Object, await second);
    Assert.Same(module.Object, loader.LoadedModule);

    await loader.DisposeAsync();
    await loader.DisposeAsync();

    js.Verify(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()), Times.Exactly(2));
    module.Verify(m => m.DisposeAsync(), Times.Once);
  }

  [Fact]
  public async Task DisposedLoader_ThrowsOnGetAsync_AndReturnsNullOnTryGetAsync()
  {
    var js = new Mock<IJSRuntime>(MockBehavior.Strict);
    var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
    js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
        .ReturnsAsync(module.Object);
    module.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

    var loader = new JsModuleLoader(js.Object, "./js/sample.js");
    Assert.Same(module.Object, await loader.TryGetAsync());

    await loader.DisposeAsync();

    Assert.True(loader.IsDisposed);
    Assert.Null(loader.LoadedModule);
    Assert.Null(await loader.TryGetAsync());
    await Assert.ThrowsAsync<ObjectDisposedException>(() => loader.GetAsync());
  }

  [Fact]
  public async Task JsInteropExtensions_SwallowsDisconnectExceptions_AndRethrowsUnexpectedErrors()
  {
    var js = new Mock<IJSRuntime>();
    js.Setup(runtime => runtime.InvokeAsync<IJSVoidResult>("dialogHelper.showModal", It.IsAny<object?[]>()))
        .ThrowsAsync(new JSDisconnectedException("Disconnected"));
    js.Setup(runtime => runtime.InvokeAsync<IJSVoidResult>("dialogHelper.close", It.IsAny<object?[]>()))
        .ThrowsAsync(new InvalidOperationException("Unexpected failure"));

    await js.Object.TryShowModalAsync("helpDialog");
    await Assert.ThrowsAsync<InvalidOperationException>(() => js.Object.TryCloseModalAsync("helpDialog"));
  }

  [Fact]
  public async Task GetAsync_RunsPrepareConcurrentlyWithImport_AndDisposesModuleWhenPrepareFails()
  {
    var js = new Mock<IJSRuntime>(MockBehavior.Strict);
    var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
    var prepareTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var importStarted = false;

    js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
        .Callback(() => importStarted = true)
        .ReturnsAsync(module.Object);
    module.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

    await using var loader = new JsModuleLoader(js.Object, "./js/sample.js", () => prepareTcs.Task);

    var loadTask = loader.GetAsync();
    Assert.True(importStarted);

    prepareTcs.SetException(new InvalidOperationException("Prepare failed"));
    await Assert.ThrowsAsync<InvalidOperationException>(() => loadTask);
    Assert.Null(loader.LoadedModule);
    module.Verify(m => m.DisposeAsync(), Times.Once);
  }
}
