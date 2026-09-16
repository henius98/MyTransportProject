using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services;

public sealed class BrowserStorageService(IJSRuntime jsRuntime) : IBrowserStorageService
{
  public async ValueTask<string?> GetStringAsync(
    string key,
    CancellationToken cancellationToken = default)
  {
    var value = await jsRuntime.InvokeAsync<string?>(
      "localStorage.getItem",
      cancellationToken,
      [key]);

    if (value is null || string.Equals(value, "null", StringComparison.Ordinal))
    {
      return null;
    }

    // Blazored.LocalStorage v4 JSON-encoded strings. Accept those existing values
    // while also supporting raw strings written by theme-manager.js.
    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
    {
      try
      {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.String
          ? document.RootElement.GetString()
          : value;
      }
      catch (JsonException)
      {
        // Preserve malformed legacy data as a raw value rather than losing it.
      }
    }

    return value;
  }

  public async ValueTask<double?> GetDoubleAsync(
    string key,
    CancellationToken cancellationToken = default)
  {
    var value = await jsRuntime.InvokeAsync<string?>(
      "localStorage.getItem",
      cancellationToken,
      [key]);

    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
      ? result
      : null;
  }

  public ValueTask SetStringAsync(
    string key,
    string value,
    CancellationToken cancellationToken = default) =>
    jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, value);

  public ValueTask SetDoubleAsync(
    string key,
    double value,
    CancellationToken cancellationToken = default) =>
    jsRuntime.InvokeVoidAsync(
      "localStorage.setItem",
      cancellationToken,
      key,
      value.ToString("R", CultureInfo.InvariantCulture));
}
