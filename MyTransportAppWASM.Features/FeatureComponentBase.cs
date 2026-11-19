using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace MyTransportAppWASM.Features;

/// <summary>
/// Base component for lazy-loaded feature components that resolve services via <see cref="FeatureServices"/>.
/// </summary>
public abstract class FeatureComponentBase : ComponentBase
{
  [Inject]
  private IServiceProvider Services { get; set; } = default!;

  private Dictionary<Type, object>? _featureServices;

  protected T GetFeatureService<T>() where T : notnull
  {
    _featureServices ??= new Dictionary<Type, object>(2);
    if (_featureServices.TryGetValue(typeof(T), out var cached))
    {
      return (T)cached;
    }

    var resolved = FeatureServices.Resolve<T>(Services);
    _featureServices[typeof(T)] = resolved;
    return resolved;
  }

  protected bool TryGetCachedFeatureService<T>([NotNullWhen(true)] out T? service) where T : notnull
  {
    if (_featureServices is not null && _featureServices.TryGetValue(typeof(T), out var cached))
    {
      service = (T)cached;
      return true;
    }

    service = default;
    return false;
  }
}
