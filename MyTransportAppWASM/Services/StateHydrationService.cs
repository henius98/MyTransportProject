using Microsoft.AspNetCore.Components;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services;

/// <summary>
/// Implements centralized state preservation using PersistentComponentState.
/// Decouples lifecycle methods from repetitive serialization logic and prevents double-fetching.
/// </summary>
public sealed class StateHydrationService : IStateHydrationService
{
    private readonly PersistentComponentState? _persistentState;
    private readonly PersistingComponentStateSubscription? _subscription;
    private readonly Dictionary<string, Func<Task>> _persistCallbacks = new();
    private bool _isDisposed;

    public StateHydrationService(PersistentComponentState? persistentState = null)
    {
        _persistentState = persistentState;
        if (_persistentState != null)
        {
            _subscription = _persistentState.RegisterOnPersisting(PersistAllAsync);
        }
    }

    public async Task<T> GetOrHydrateAsync<T>(string key, Func<Task<T>> fetchFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fetchFactory);

        if (TryTake<T>(key, out var restored) && restored is not null)
        {
            return restored;
        }

        var result = await fetchFactory();

        RegisterCallback(key, () => result);
        return result;
    }

    public bool TryTake<T>(string key, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_persistentState != null &&
            _persistentState.TryTakeFromJson<T>(key, out var retrieved))
        {
            value = retrieved;
            return true;
        }

        value = default;
        return false;
    }

    public void RegisterCallback<T>(string key, Func<T?> valueProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueProvider);

        if (_persistentState == null)
        {
            return;
        }

        _persistCallbacks[key] = () =>
        {
            var val = valueProvider();
            if (val is not null)
            {
                _persistentState.PersistAsJson(key, val);
            }
            return Task.CompletedTask;
        };
    }

    private Task PersistAllAsync()
    {
        foreach (var callback in _persistCallbacks.Values)
        {
            callback();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _subscription?.Dispose();
        _persistCallbacks.Clear();
    }
}
