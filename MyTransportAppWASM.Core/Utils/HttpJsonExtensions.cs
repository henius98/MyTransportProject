using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MyTransportAppWASM.Core.Utils;

internal static class HttpJsonExtensions
{
  private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

  public static async Task<T?> GetJsonAsync<T>(
    this HttpClient client,
    string requestUri,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken = default)
  {
    using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadJsonAsync(typeInfo, cancellationToken);
  }

  public static Task<HttpResponseMessage> PostJsonAsync<T>(
    this HttpClient client,
    string requestUri,
    T value,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken = default)
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    var content = new ByteArrayContent(bytes);
    content.Headers.ContentType = JsonMediaType;
    return client.PostAsync(requestUri, content, cancellationToken);
  }

  public static async Task<T?> ReadJsonAsync<T>(
    this HttpContent content,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken = default)
  {
    await using var stream = await content.ReadAsStreamAsync(cancellationToken);
    return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
  }
}
