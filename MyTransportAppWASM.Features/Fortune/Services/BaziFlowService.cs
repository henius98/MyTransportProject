using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyTransportAppWASM.Features.Fortune.Services
{
  public sealed class BaziFlowService(
      HttpClient httpClient,
      IBrowserStorageService localStorage,
      ILogger<BaziFlowService>? logger = null) : IBaziFlowService
  {
    private readonly ILogger<BaziFlowService> _logger = logger ?? NullLogger<BaziFlowService>.Instance;
    private CachedDateFortune? _cachedFortune;
    internal const string ApiKeyStorageKey = "baziflow_api_key";
    internal const string FortuneCacheStorageKey = "fortune_cache_v1";

    private async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
      return await localStorage.GetStringAsync(ApiKeyStorageKey, cancellationToken);
    }

    public async Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
      var previousKey = await GetApiKeyAsync(cancellationToken);
      await localStorage.SetStringAsync(ApiKeyStorageKey, apiKey, cancellationToken);
      if (string.IsNullOrEmpty(apiKey) || !string.Equals(previousKey, apiKey, StringComparison.Ordinal))
      {
        await ClearFortuneCacheAsync(cancellationToken);
      }
    }

    public async Task<bool> SetupProfileAsync(string apiKey, CreateProfileRequest request, CancellationToken cancellationToken = default)
    {
      await SaveApiKeyAsync(apiKey, cancellationToken);
      try
      {
        if (await CreateProfileAsync(request, cancellationToken))
        {
          return true;
        }
      }
      catch (OperationCanceledException)
      {
        await SaveApiKeyAsync(string.Empty, CancellationToken.None);
        throw;
      }

      await SaveApiKeyAsync(string.Empty, CancellationToken.None);
      return false;
    }

    public async Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default)
    {
      if (httpClient.BaseAddress is null)
      {
        return false;
      }
      var key = await GetApiKeyAsync(cancellationToken);
      return !string.IsNullOrEmpty(key);
    }

    public async Task<ProfileData?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
      if (httpClient.BaseAddress is null)
      {
        return null;
      }
      try
      {
        var response = await httpClient.GetJsonAsync(
            "/api/v1/profile",
            FeaturesJsonSerializerContext.Default.ApiResponseProfileData,
            cancellationToken);
        return response?.Data;
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogDebug("Error fetching profile: {Message}", ex.Message);
        return null;
      }
    }

    public async Task<bool> CreateProfileAsync(CreateProfileRequest request, CancellationToken cancellationToken = default)
    {
      if (httpClient.BaseAddress is null)
      {
        return false;
      }
      try
      {
        using var response = await httpClient.PostJsonAsync(
            "/api/v1/profile",
            request,
            FeaturesJsonSerializerContext.Default.CreateProfileRequest,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
          await ClearFortuneCacheAsync(cancellationToken);
          return true;
        }
        return false;
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogWarning("Error creating profile: {Message}", ex.Message);
        return false;
      }
    }

    public async Task<FortuneData?> GetDateFortuneAsync(string date, CancellationToken cancellationToken = default)
    {
      if (httpClient.BaseAddress is null)
      {
        return null;
      }

      var cachedInMemory = _cachedFortune;
      if (cachedInMemory is { Data: not null } &&
          string.Equals(cachedInMemory.Date, date, StringComparison.Ordinal))
      {
        return cachedInMemory.Data;
      }

      var storedJson = await localStorage.GetStringAsync(FortuneCacheStorageKey, cancellationToken);
      if (!string.IsNullOrWhiteSpace(storedJson))
      {
        try
        {
          var stored = JsonSerializer.Deserialize(
            storedJson,
            FeaturesJsonSerializerContext.Default.CachedDateFortune);
          if (stored is { Data: not null } &&
              string.Equals(stored.Date, date, StringComparison.Ordinal))
          {
            NormalizeFortuneData(stored.Data);
            _cachedFortune = stored;
            return stored.Data;
          }
        }
        catch (JsonException ex)
        {
          _logger.LogDebug("Ignoring corrupt fortune cache: {Message}", ex.Message);
        }
      }

      try
      {
        var request = new DateFortuneRequest { Date = date };
        using var response = await httpClient.PostJsonAsync(
            "/api/v1/date-fortune",
            request,
            FeaturesJsonSerializerContext.Default.DateFortuneRequest,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
          var result = await response.Content.ReadJsonAsync(
              FeaturesJsonSerializerContext.Default.ApiResponseFortuneData,
              cancellationToken);
          if (result?.Data is { } fortune)
          {
            NormalizeFortuneData(fortune);
            var entry = new CachedDateFortune { Date = date, Data = fortune };
            _cachedFortune = entry;
            var serialized = JsonSerializer.Serialize(
              entry,
              FeaturesJsonSerializerContext.Default.CachedDateFortune);
            await localStorage.SetStringAsync(FortuneCacheStorageKey, serialized, cancellationToken);
            return fortune;
          }
        }
        return null;
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogDebug("Error fetching date fortune: {Message}", ex.Message);
        return null;
      }
    }

    private async Task ClearFortuneCacheAsync(CancellationToken cancellationToken)
    {
      _cachedFortune = null;
      await localStorage.RemoveAsync(FortuneCacheStorageKey, cancellationToken);
    }

    private static void NormalizeFortuneData(FortuneData data)
    {
      data.FavorableDirections ??= [];
      data.LuckyHours ??= [];
    }
  }
}
