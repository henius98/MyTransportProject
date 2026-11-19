namespace MyTransportAppWASM.Core.Utils;

/// <summary>
/// Safe JS interop helpers for UI calls that can race with disconnect or component teardown.
/// </summary>
public static class JsInteropExtensions
{
  public static Task TryInvokeVoidSafeAsync(this IJSRuntime jsRuntime, string identifier, params object?[] args) =>
    TryInvokeVoidCoreAsync(jsRuntime.InvokeVoidAsync(identifier, args));

  public static Task TryInvokeVoidSafeAsync(this IJSObjectReference jsObject, string identifier, params object?[] args) =>
    TryInvokeVoidCoreAsync(jsObject.InvokeVoidAsync(identifier, args));

  private static async Task TryInvokeVoidCoreAsync(ValueTask call)
  {
    try
    {
      await call;
    }
    catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException)
    {
    }
  }

  public static Task TryShowModalAsync(this IJSRuntime jsRuntime, string dialogId) =>
    jsRuntime.TryInvokeVoidSafeAsync("dialogHelper.showModal", dialogId);

  public static Task TryCloseModalAsync(this IJSRuntime jsRuntime, string dialogId) =>
    jsRuntime.TryInvokeVoidSafeAsync("dialogHelper.close", dialogId);
}
