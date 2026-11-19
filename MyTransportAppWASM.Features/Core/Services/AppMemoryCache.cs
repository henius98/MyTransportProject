namespace MyTransportAppWASM.Core.Services;

public interface IAppCache
{
  bool TryGetValue<T>(string key, out T? value);
  T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory);
  void Set<T>(string key, T value, TimeSpan ttl);
  void Remove(string key);
}

public sealed class AppMemoryCache : IAppCache, IDisposable
{
  private readonly record struct CacheEntry(object? Value, DateTimeOffset ExpiresAt, long Sequence);

  private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();
  private readonly int _sizeLimit;
  private readonly Func<DateTimeOffset> _utcNow;
  private long _sequence;

  public AppMemoryCache(int sizeLimit = 200, Func<DateTimeOffset>? utcNow = null)
  {
    _sizeLimit = sizeLimit > 0 ? sizeLimit : 200;
    _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
  }

  public bool TryGetValue<T>(string key, out T? value)
  {
    lock (_gate)
    {
      if (_entries.TryGetValue(key, out var entry))
      {
        if (entry.ExpiresAt > _utcNow())
        {
          if (TryReadValue(entry, out value))
          {
            return true;
          }
        }
        else
        {
          _entries.Remove(key);
        }
      }
    }

    value = default;
    return false;
  }

  public T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory)
  {
    lock (_gate)
    {
      var now = _utcNow();
      if (_entries.TryGetValue(key, out var existing))
      {
        if (existing.ExpiresAt > now && TryReadValue<T>(existing, out var existingValue))
        {
          return existingValue!;
        }
        _entries.Remove(key);
      }

      var created = factory();
      SetLocked(key, created, now + ttl, now);
      return created;
    }
  }

  private static bool TryReadValue<T>(CacheEntry entry, out T? value)
  {
    if (entry.Value is T typed)
    {
      value = typed;
      return true;
    }
    if (entry.Value is null && default(T) is null)
    {
      value = default;
      return true;
    }

    value = default;
    return false;
  }

  public void Set<T>(string key, T value, TimeSpan ttl)
  {
    lock (_gate)
    {
      var now = _utcNow();
      SetLocked(key, value, now + ttl, now);
    }
  }

  public void Remove(string key)
  {
    lock (_gate)
    {
      _entries.Remove(key);
    }
  }

  public void Dispose()
  {
    lock (_gate)
    {
      _entries.Clear();
    }
  }

  private void SetLocked(string key, object? value, DateTimeOffset expiresAt, DateTimeOffset now)
  {
    if (expiresAt <= now)
    {
      _entries.Remove(key);
      return;
    }

    if (!_entries.ContainsKey(key) && _entries.Count >= _sizeLimit)
    {
      EvictExpiredLocked(now);
      if (_entries.Count >= _sizeLimit)
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

    _entries[key] = new CacheEntry(value, expiresAt, ++_sequence);
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
