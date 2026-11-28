namespace MyTransportAppWASM.Core.Services;

public interface IBrowserStorageService
{
  ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);
  ValueTask SetStringAsync(string key, string value, CancellationToken cancellationToken = default);
  ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}
