using Microsoft.JSInterop;

namespace MyTransportAppWASM.Tests;

public class BrowserStorageServiceTests
{
  [Fact]
  public async Task ReadsRawAndLegacyJsonEncodedValues()
  {
    var runtime = new StorageJsRuntime(new Dictionary<string, string>
    {
      ["theme"] = "dark",
      ["api-key"] = "\"legacy-secret\""
    });
    var storage = new BrowserStorageService(runtime);

    Assert.Equal("dark", await storage.GetStringAsync("theme"));
    Assert.Equal("legacy-secret", await storage.GetStringAsync("api-key"));
  }

  [Fact]
  public async Task WritesValuesInJavaScriptCompatibleInvariantFormat()
  {
    var runtime = new StorageJsRuntime();
    var storage = new BrowserStorageService(runtime);

    await storage.SetStringAsync("theme", "light");

    Assert.Equal("light", runtime.Values["theme"]);
  }

  [Fact]
  public async Task RemovesValuesFromStorage()
  {
    var runtime = new StorageJsRuntime(new Dictionary<string, string>
    {
      ["fortune_cache_v1"] = "{\"date\":\"2026-10-12\"}"
    });
    var storage = new BrowserStorageService(runtime);

    await storage.RemoveAsync("fortune_cache_v1");

    Assert.False(runtime.Values.ContainsKey("fortune_cache_v1"));
    Assert.Null(await storage.GetStringAsync("fortune_cache_v1"));
  }

  [Fact]
  public async Task FailingStorage_ReturnsDefaultWithoutThrowing()
  {
    var runtime = new FailingStorageJsRuntime();
    var storage = new BrowserStorageService(runtime);

    var str = await storage.GetStringAsync("key");
    await storage.SetStringAsync("key", "val");
    await storage.RemoveAsync("key");

    Assert.Null(str);
  }

  private sealed class FailingStorageJsRuntime : IJSRuntime
  {
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        throw new JSException("Storage is disabled");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        throw new JSException("Storage is disabled");
  }

  private sealed class StorageJsRuntime : IJSRuntime
  {
    public StorageJsRuntime(Dictionary<string, string>? values = null)
    {
      Values = values ?? new Dictionary<string, string>();
    }

    public Dictionary<string, string> Values { get; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
      var key = Assert.IsType<string>(args![0]);
      if (string.Equals(identifier, "localStorage.getItem", StringComparison.Ordinal))
      {
        Values.TryGetValue(key, out var value);
        return ValueTask.FromResult(value is null ? default! : (TValue)(object)value);
      }

      if (string.Equals(identifier, "localStorage.removeItem", StringComparison.Ordinal))
      {
        Values.Remove(key);
        return ValueTask.FromResult(default(TValue)!);
      }

      Assert.Equal("localStorage.setItem", identifier);
      Values[key] = Assert.IsType<string>(args[1]);
      return ValueTask.FromResult(default(TValue)!);
    }
  }
}
