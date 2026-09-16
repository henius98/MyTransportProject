namespace MyTransportAppWASM.Services.Interfaces;

public interface IBrowserStorageService
{
  ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);
  ValueTask<double?> GetDoubleAsync(string key, CancellationToken cancellationToken = default);
  ValueTask SetStringAsync(string key, string value, CancellationToken cancellationToken = default);
  ValueTask SetDoubleAsync(string key, double value, CancellationToken cancellationToken = default);
}
