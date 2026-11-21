using System.Collections.Concurrent;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Thread-safe in-memory domain event bus for cross-module coordination inside <c>Spawn.Host</c>.
/// </summary>
public sealed class InMemorySpawnEventBus : ISpawnEventBus
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>> _subscribers = new();

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        var map = _subscribers.GetOrAdd(typeof(TEvent), static _ => new ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>());
        map[id] = (evt, ct) => handler((TEvent)evt, ct);

        return new SubscriptionToken(() => map.TryRemove(id, out _));
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Subscribe<TEvent>((evt, _) =>
        {
            handler(evt);
            return Task.CompletedTask;
        });
    }

    public async ValueTask PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!_subscribers.TryGetValue(typeof(TEvent), out var map) || map.IsEmpty)
        {
            return;
        }

        foreach (var (_, callback) in map.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await callback(domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SubscriptionToken(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
