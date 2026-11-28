namespace MyTransportAppWASM.Features.Weather.Services;

internal sealed class WeatherResponseCache(
  Func<DateTimeOffset> utcNow,
  int maxEntries = WeatherResponseCache.DefaultMaxEntries)
{
  internal const int DefaultMaxEntries = 50;

  private readonly record struct CacheEntry(
    Lazy<Task<WeatherProviderResult>> Task,
    DateTimeOffset ExpiresAt,
    long Sequence,
    bool IsFallback = false);

  private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();
  private long _sequence;

  public (Lazy<Task<WeatherProviderResult>> Task, bool WasCached) GetOrAdd(
    string key,
    TimeSpan ttl,
    Func<Lazy<Task<WeatherProviderResult>>> factory)
  {
    lock (_gate)
    {
      var now = utcNow();
      if (_entries.TryGetValue(key, out var existing))
      {
        if (existing.ExpiresAt > now)
        {
          return (existing.Task, true);
        }
        _entries.Remove(key);
      }

      if (_entries.Count >= maxEntries)
      {
        EvictExpiredLocked(now);
        if (_entries.Count >= maxEntries)
        {
          string? oldestKey = null;
          var oldestSequence = long.MaxValue;
          foreach (var pair in _entries)
          {
            if (pair.Value.Sequence < oldestSequence)
            {
              oldestSequence = pair.Value.Sequence;
              oldestKey = pair.Key;
            }
          }
          if (oldestKey is not null)
          {
            _entries.Remove(oldestKey);
          }
        }
      }

      var created = factory();
      _entries[key] = new CacheEntry(created, now + ttl, ++_sequence);
      return (created, false);
    }
  }

  public void ApplyFallbackLifetime(
    string key,
    Lazy<Task<WeatherProviderResult>> expectedTask,
    TimeSpan fallbackTtl)
  {
    lock (_gate)
    {
      if (_entries.TryGetValue(key, out var existing) &&
          ReferenceEquals(existing.Task, expectedTask) &&
          !existing.IsFallback)
      {
        _entries[key] = existing with { ExpiresAt = utcNow() + fallbackTtl, IsFallback = true };
      }
    }
  }

  public void Remove(string key, Lazy<Task<WeatherProviderResult>>? expectedTask = null)
  {
    lock (_gate)
    {
      if (expectedTask is null ||
          (_entries.TryGetValue(key, out var existing) && ReferenceEquals(existing.Task, expectedTask)))
      {
        _entries.Remove(key);
      }
    }
  }

  private void EvictExpiredLocked(DateTimeOffset now)
  {
    List<string>? expired = null;
    foreach (var pair in _entries)
    {
      if (pair.Value.ExpiresAt <= now)
      {
        (expired ??= []).Add(pair.Key);
      }
    }
    if (expired is not null)
    {
      foreach (var key in expired)
      {
        _entries.Remove(key);
      }
    }
  }
}
