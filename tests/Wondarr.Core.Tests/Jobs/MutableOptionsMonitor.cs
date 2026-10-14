using Microsoft.Extensions.Options;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// An options monitor a test can change: <see cref="Set"/> replaces the value and tells the listeners,
/// as a configuration reload does.
/// </summary>
internal sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];
    private T _value;

    public MutableOptionsMonitor(T value) => _value = value;

    /// <inheritdoc />
    public T CurrentValue => _value;

    /// <summary>Gets how many listeners are subscribed right now.</summary>
    public int ListenerCount => _listeners.Count;

    /// <inheritdoc />
    public T Get(string? name) => _value;

    /// <inheritdoc />
    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);

        return new Subscription(this, listener);
    }

    /// <summary>Replaces the value and calls every listener.</summary>
    /// <param name="value">The reloaded value.</param>
    public void Set(T value)
    {
        _value = value;

        foreach (var listener in _listeners.ToList())
        {
            listener(value, null);
        }
    }

    private sealed class Subscription(MutableOptionsMonitor<T> owner, Action<T, string?> listener) : IDisposable
    {
        public void Dispose() => owner._listeners.Remove(listener);
    }
}
