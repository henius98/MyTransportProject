using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using MyTransportAppWASM.Gtfs;
using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services
{
  public class GtfsService : IGtfsService
  {
    private const int DefaultCapacity = 2 * 1024 * 1024;
    private const int MaximumFeedSize = 64 * 1024 * 1024;
    private const int MaximumValidatorEntries = 32;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ValidatorEntry> _validators = new(StringComparer.Ordinal);
    private readonly object _validatorLock = new();

    public GtfsService(HttpClient httpClient) : this(httpClient, TimeProvider.System)
    {
    }

    internal GtfsService(HttpClient httpClient, TimeProvider timeProvider)
    {
      _httpClient = httpClient;
      _timeProvider = timeProvider;
    }

    public async Task<GtfsFetchResult> GetBusPositionsAsync(
      string url,
      bool allowNotModified,
      CancellationToken cancellationToken = default)
    {
      try
      {
        var now = _timeProvider.GetUtcNow();
        var validators = GetValidators(url, now);

        if (allowNotModified && validators?.FreshUntil > now)
        {
          return new GtfsFetchResult(GtfsFetchStatus.NotModified);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (allowNotModified && validators is not null)
        {
          if (EntityTagHeaderValue.TryParse(validators.EntityTag, out var entityTag))
          {
            request.Headers.IfNoneMatch.Add(entityTag);
          }

          request.Headers.IfModifiedSince = validators.LastModified;
        }

        // Stream the response so a large feed is never first buffered into a separate byte[].
        using HttpResponseMessage response = await _httpClient.SendAsync(
          request,
          HttpCompletionOption.ResponseHeadersRead,
          cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified && allowNotModified)
        {
          RefreshValidators(url, validators, response, now);
          return new GtfsFetchResult(GtfsFetchStatus.NotModified);
        }

        if (!response.IsSuccessStatusCode)
        {
          return default;
        }

        var vehicles = await ParseVehiclesAsync(response.Content, cancellationToken);
        StoreValidators(url, response, now);
        return new GtfsFetchResult(GtfsFetchStatus.Updated, vehicles);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"GtfsService error fetching {url}: {ex.Message}");
        return default;
      }
    }

    private static async Task<List<BusLocation>> ParseVehiclesAsync(
      HttpContent content,
      CancellationToken cancellationToken)
    {
      var contentLength = content.Headers.ContentLength;
      if (contentLength > MaximumFeedSize)
      {
        throw new InvalidDataException(
          $"GTFS feed declares a size above the {MaximumFeedSize / 1024 / 1024} MB safety limit.");
      }

      await using var stream = await content.ReadAsStreamAsync(cancellationToken);
      int initialCapacity = contentLength is > 0 and <= MaximumFeedSize
        ? (int)contentLength.Value
        : DefaultCapacity;
      byte[] buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
      int totalBytesRead = 0;

      try
      {
        while (true)
        {
          if (totalBytesRead == buffer.Length)
          {
            if (buffer.Length >= MaximumFeedSize)
            {
              throw new InvalidDataException(
                $"GTFS feed exceeds the {MaximumFeedSize / 1024 / 1024} MB safety limit.");
            }

            int newSize = Math.Min(buffer.Length * 2, MaximumFeedSize);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            buffer.AsSpan(0, totalBytesRead).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = newBuffer;
          }

          int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytesRead), cancellationToken);
          if (bytesRead == 0) break;
          totalBytesRead += bytesRead;
        }

        var vehicles = new List<BusLocation>();
        GtfsFeedParser.ParseVehicles(
          buffer.AsMemory(0, totalBytesRead),
          count => { vehicles.EnsureCapacity(count); },
          (routeId, vehicleId, latitude, longitude, bearing, speed, timestamp) =>
            vehicles.Add(new BusLocation
            {
              RouteId = routeId,
              VehicleId = vehicleId,
              Lat = latitude,
              Lng = longitude,
              Bearing = bearing,
              Speed = speed,
              Timestamp = timestamp
            }),
          cancellationToken);

        return vehicles;
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    private ValidatorEntry? GetValidators(string url, DateTimeOffset now)
    {
      lock (_validatorLock)
      {
        if (!_validators.TryGetValue(url, out var entry))
        {
          return null;
        }

        entry.LastAccess = now;
        return entry.Clone();
      }
    }

    private void StoreValidators(string url, HttpResponseMessage response, DateTimeOffset now)
    {
      if (response.Headers.CacheControl?.NoStore == true)
      {
        lock (_validatorLock)
        {
          _validators.Remove(url);
        }
        return;
      }

      var entry = new ValidatorEntry
      {
        EntityTag = response.Headers.ETag?.ToString(),
        LastModified = response.Content.Headers.LastModified,
        FreshUntil = CalculateFreshUntil(response, now),
        LastAccess = now
      };

      if (entry.EntityTag is null && entry.LastModified is null && entry.FreshUntil <= now)
      {
        lock (_validatorLock)
        {
          _validators.Remove(url);
        }
        return;
      }

      lock (_validatorLock)
      {
        EvictOldestValidatorIfRequired(url);
        _validators[url] = entry;
      }
    }

    private void RefreshValidators(
      string url,
      ValidatorEntry? existing,
      HttpResponseMessage response,
      DateTimeOffset now)
    {
      if (existing is null)
      {
        return;
      }

      if (response.Headers.CacheControl?.NoStore == true)
      {
        lock (_validatorLock)
        {
          _validators.Remove(url);
        }
        return;
      }

      existing.EntityTag = response.Headers.ETag?.ToString() ?? existing.EntityTag;
      existing.LastModified = response.Content.Headers.LastModified ?? existing.LastModified;
      existing.FreshUntil = CalculateFreshUntil(response, now);
      existing.LastAccess = now;

      lock (_validatorLock)
      {
        EvictOldestValidatorIfRequired(url);
        _validators[url] = existing;
      }
    }

    private static DateTimeOffset CalculateFreshUntil(
      HttpResponseMessage response,
      DateTimeOffset now)
    {
      var cacheControl = response.Headers.CacheControl;
      if (cacheControl?.NoCache == true || cacheControl?.NoStore == true)
      {
        return now;
      }

      if (cacheControl?.MaxAge is { } maxAge)
      {
        var remaining = maxAge - (response.Headers.Age ?? TimeSpan.Zero);
        return remaining > TimeSpan.Zero ? now + remaining : now;
      }

      return response.Content.Headers.Expires is { } expires && expires > now
        ? expires
        : now;
    }

    private void EvictOldestValidatorIfRequired(string incomingUrl)
    {
      if (_validators.ContainsKey(incomingUrl) || _validators.Count < MaximumValidatorEntries)
      {
        return;
      }

      string? oldestUrl = null;
      var oldestAccess = DateTimeOffset.MaxValue;
      foreach (var pair in _validators)
      {
        if (pair.Value.LastAccess < oldestAccess)
        {
          oldestUrl = pair.Key;
          oldestAccess = pair.Value.LastAccess;
        }
      }

      if (oldestUrl is not null)
      {
        _validators.Remove(oldestUrl);
      }
    }

    private sealed class ValidatorEntry
    {
      public string? EntityTag { get; set; }
      public DateTimeOffset? LastModified { get; set; }
      public DateTimeOffset FreshUntil { get; set; }
      public DateTimeOffset LastAccess { get; set; }

      public ValidatorEntry Clone() => new()
      {
        EntityTag = EntityTag,
        LastModified = LastModified,
        FreshUntil = FreshUntil,
        LastAccess = LastAccess
      };
    }
  }
}
