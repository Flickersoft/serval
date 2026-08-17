using Microsoft.Extensions.Options;

namespace Serval.Server.Tests;

/// <summary>
/// An <see cref="IOptionsMonitor{T}"/> whose value can be changed, so a test can do what the
/// settings page does: write a new value and check the thing reading it noticed.
///
/// <para>There is no framework helper for this — <c>Options.Create</c> produces an
/// <see cref="IOptions{T}"/>, which is precisely the interface the server moved off in order to be
/// reconfigurable. Building the real <c>OptionsMonitor</c> needs a factory, a source list and a
/// cache to say one thing, so this says it directly.</para>
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    private readonly List<Action<T, string?>> _listeners = [];

    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }

    public T Get(string? name) => CurrentValue;

    /// <summary>Publishes a new value, exactly as a reload of the settings overlay does.</summary>
    public void Set(T value)
    {
        CurrentValue = value;

        foreach (Action<T, string?> listener in _listeners.ToList())
        {
            listener(value, Options.DefaultName);
        }
    }

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
