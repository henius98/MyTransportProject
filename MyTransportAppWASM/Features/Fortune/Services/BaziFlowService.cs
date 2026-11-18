using System.Net.Http.Json;
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
    internal const string ApiKeyStorageKey = "baziflow_api_key";

    private async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
      return await localStorage.GetStringAsync(ApiKeyStorageKey, cancellationToken);
    }

    public async Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
      await localStorage.SetStringAsync(ApiKeyStorageKey, apiKey, cancellationToken);
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
        var response = await httpClient.GetFromJsonAsync(
            "/api/v1/profile",
            AppJsonSerializerContext.Default.ApiResponseProfileData,
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
        using var response = await httpClient.PostAsJsonAsync(
            "/api/v1/profile",
            request,
            AppJsonSerializerContext.Default.CreateProfileRequest,
            cancellationToken);
        return response.IsSuccessStatusCode;
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
      try
      {
        var request = new DateFortuneRequest { Date = date };
        using var response = await httpClient.PostAsJsonAsync(
            "/api/v1/date-fortune",
            request,
            AppJsonSerializerContext.Default.DateFortuneRequest,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
          var result = await response.Content.ReadFromJsonAsync(
              AppJsonSerializerContext.Default.ApiResponseFortuneData,
              cancellationToken);
          result?.Data?.FavorableDirections ??= [];
          result?.Data?.LuckyHours ??= [];
          return result?.Data;
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
  }
}
