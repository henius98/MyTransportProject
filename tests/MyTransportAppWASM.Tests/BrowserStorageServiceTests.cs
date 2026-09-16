using Microsoft.JSInterop;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class BrowserStorageServiceTests
{
    [Fact]
    public async Task ReadsRawAndLegacyJsonEncodedValues()
    {
        var runtime = new StorageJsRuntime(new Dictionary<string, string>
        {
            ["theme"] = "dark",
            ["api-key"] = "\"legacy-secret\"",
            ["latitude"] = "3.139"
        });
        var storage = new BrowserStorageService(runtime);

        Assert.Equal("dark", await storage.GetStringAsync("theme"));
        Assert.Equal("legacy-secret", await storage.GetStringAsync("api-key"));
        Assert.Equal(3.139, await storage.GetDoubleAsync("latitude"));
    }

    [Fact]
    public async Task WritesValuesInJavaScriptCompatibleInvariantFormat()
    {
        var runtime = new StorageJsRuntime();
        var storage = new BrowserStorageService(runtime);

        await storage.SetStringAsync("theme", "light");
        await storage.SetDoubleAsync("latitude", 3.139);

        Assert.Equal("light", runtime.Values["theme"]);
        Assert.Equal("3.139", runtime.Values["latitude"]);
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

            Assert.Equal("localStorage.setItem", identifier);
            Values[key] = Assert.IsType<string>(args[1]);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
