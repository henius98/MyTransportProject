using Microsoft.Extensions.Caching.Memory;
using TransitRealtime;

namespace MyTransportAppWASM.Services
{
  public class GtfsService : IGtfsService
  {
    private readonly HttpClient _httpClient;

    public GtfsService(HttpClient httpClient)
    {
      _httpClient = httpClient;
    }
    public async Task<FeedMessage?> GetBusPositionsAsync(string url, CancellationToken cancellationToken = default)
    {
      try
      {
        // Use ResponseHeadersRead to prevent HttpClient from buffering the entire huge GTFS feed into a single byte[]
        HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
          using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
          
          // Rent a buffer to avoid Large Object Heap (LOH) allocations for feeds often > 1MB
          int initialCapacity = (int)(response.Content.Headers.ContentLength ?? 1024 * 1024 * 2); // default 2MB
          byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(initialCapacity);
          int totalBytesRead = 0;

          try
          {
            while (true)
            {
              if (totalBytesRead == buffer.Length)
              {
                // Double the buffer if we run out of space
                byte[] newBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Array.Copy(buffer, newBuffer, totalBytesRead);
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                buffer = newBuffer;
              }

              int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytesRead), cancellationToken);
              if (bytesRead == 0) break;
              totalBytesRead += bytesRead;
            }

            var feed = await ProtocolBufferUtils.ParseAsync<FeedMessage>(buffer.AsMemory(0, totalBytesRead), cancellationToken);
            return feed;
          }
          finally
          {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
          }
        }
        return null;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"GtfsService error fetching {url}: {ex.Message}");
        return null;
      }

    }
  }
}
