namespace MyTransportAppWASM.Services.Interfaces;

/// <summary>
/// Centralized state preservation and hydration contract using PersistentComponentState.
/// Eliminates double-fetching and layout jumping between prerendering and WASM hydration.
/// </summary>
public interface IStateHydrationService : IDisposable
{
    /// <summary>
    /// Attempts to retrieve pre-persisted state by key, or produces and registers it.
    /// </summary>
    Task<T> GetOrHydrateAsync<T>(string key, Func<Task<T>> fetchFactory);

    /// <summary>
    /// Attempts to take a persisted value from component state.
    /// </summary>
    bool TryTake<T>(string key, out T? value);

    /// <summary>
    /// Registers a state callback invoked when component state is being persisted.
    /// </summary>
    void RegisterCallback<T>(string key, Func<T?> valueProvider);
}
