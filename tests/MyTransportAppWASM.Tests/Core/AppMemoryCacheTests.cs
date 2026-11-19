namespace MyTransportAppWASM.Tests;

public class AppMemoryCacheTests
{
  [Fact]
  public void SetAndTryGetValue_ExpiresEntriesAndSupportsRemoveAndDispose()
  {
    var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    using var cache = new AppMemoryCache(sizeLimit: 10, utcNow: () => now);

    cache.Set("a", "alpha", TimeSpan.FromSeconds(30));
    cache.Set("b", 42, TimeSpan.FromSeconds(30));

    Assert.True(cache.TryGetValue<string>("a", out var aValue));
    Assert.Equal("alpha", aValue);
    Assert.False(cache.TryGetValue<int>("a", out _));

    cache.Remove("b");
    Assert.False(cache.TryGetValue<int>("b", out _));

    now = now.AddSeconds(30);
    Assert.False(cache.TryGetValue<string>("a", out _));

    cache.Set("c", "gamma", TimeSpan.FromSeconds(30));
    cache.Dispose();
    Assert.False(cache.TryGetValue<string>("c", out _));
  }

  [Fact]
  public void Set_EvictsExpiredEntriesBeforeOldestLiveEntryAtSizeLimit()
  {
    var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    using var cache = new AppMemoryCache(sizeLimit: 2, utcNow: () => now);

    cache.Set("first", "1", TimeSpan.FromSeconds(60));
    cache.Set("short", "2", TimeSpan.FromSeconds(10));

    now = now.AddSeconds(15);
    cache.Set("third", "3", TimeSpan.FromSeconds(60));

    // Expired "short" should be swept, preserving live "first".
    Assert.True(cache.TryGetValue<string>("first", out _));
    Assert.True(cache.TryGetValue<string>("third", out _));
    Assert.False(cache.TryGetValue<string>("short", out _));

    // Cache is full of live entries; inserting "fourth" evicts oldest ("first").
    cache.Set("fourth", "4", TimeSpan.FromSeconds(60));
    Assert.False(cache.TryGetValue<string>("first", out _));
    Assert.True(cache.TryGetValue<string>("third", out _));
    Assert.True(cache.TryGetValue<string>("fourth", out _));
  }

  [Fact]
  public void Set_UpdatingExistingKeyAtSizeLimit_DoesNotEvictOtherEntries()
  {
    var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    using var cache = new AppMemoryCache(sizeLimit: 2, utcNow: () => now);

    cache.Set("a", "1", TimeSpan.FromSeconds(60));
    cache.Set("b", "2", TimeSpan.FromSeconds(60));
    cache.Set("a", "1-updated", TimeSpan.FromSeconds(60));

    Assert.True(cache.TryGetValue<string>("a", out var a));
    Assert.Equal("1-updated", a);
    Assert.True(cache.TryGetValue<string>("b", out var b));
    Assert.Equal("2", b);

    // "a" now has a newer sequence than "b", so inserting "c" evicts "b".
    cache.Set("c", "3", TimeSpan.FromSeconds(60));
    Assert.True(cache.TryGetValue<string>("a", out _));
    Assert.False(cache.TryGetValue<string>("b", out _));
    Assert.True(cache.TryGetValue<string>("c", out _));
  }

  [Fact]
  public void GetOrCreate_CachesNullReferenceValuesConsistentlyWithTryGetValue()
  {
    var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    using var cache = new AppMemoryCache(sizeLimit: 4, utcNow: () => now);
    var calls = 0;

    var first = cache.GetOrCreate<string?>("missing", TimeSpan.FromSeconds(60), () =>
    {
      calls++;
      return null;
    });
    var second = cache.GetOrCreate<string?>("missing", TimeSpan.FromSeconds(60), () =>
    {
      calls++;
      return "should-not-run";
    });

    Assert.Null(first);
    Assert.Null(second);
    Assert.Equal(1, calls);
    Assert.True(cache.TryGetValue<string?>("missing", out var cached));
    Assert.Null(cached);
  }

  [Fact]
  public void NonPositiveTtl_DoesNotCacheOrEvictExistingEntries()
  {
    var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    using var cache = new AppMemoryCache(sizeLimit: 1, utcNow: () => now);

    cache.Set("live", "keep", TimeSpan.FromSeconds(60));
    cache.Set("zero", "drop", TimeSpan.Zero);
    var created = cache.GetOrCreate("negative", TimeSpan.FromSeconds(-5), () => "temp");

    Assert.Equal("temp", created);
    Assert.True(cache.TryGetValue<string>("live", out var live));
    Assert.Equal("keep", live);
    Assert.False(cache.TryGetValue<string>("zero", out _));
    Assert.False(cache.TryGetValue<string>("negative", out _));
  }
}
