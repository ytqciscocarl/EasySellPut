using System.Collections.Concurrent;

namespace Meili.Data.Http;

/// <summary>Small in-memory TTL cache with in-flight de-duplication (concurrent callers for the same key share one request).</summary>
public sealed class ResponseCache
{
    private sealed record Entry(object Value, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new();
    private readonly TimeProvider _time;
    public int MaxEntries { get; set; } = 2000;

    public ResponseCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool TryGet<T>(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var e) && e.Expires > _time.GetUtcNow() && e.Value is T t) { value = t; return true; }
        value = default!;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl) where T : notnull
    {
        if (_entries.Count >= MaxEntries) Prune();
        _entries[key] = new Entry(value, _time.GetUtcNow() + ttl);
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);
    public void Clear() => _entries.Clear();

    /// <remarks>
    /// The shared request is detached from its callers: it runs with <see cref="CancellationToken.None"/>, stores its own
    /// result and removes its own in-flight entry. A caller's <paramref name="ct"/> only ends that caller's wait, so one
    /// page navigating away cannot cancel the request another page is sharing.
    /// </remarks>
    public async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default) where T : notnull
    {
        if (TryGet<T>(key, out var hit)) return hit;
        var lazy = _inflight.GetOrAdd(key, _ => NewShared(key, ttl, factory));
        return (T)await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private Lazy<Task<object>> NewShared<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory) where T : notnull
    {
        Lazy<Task<object>>? self = null;
        self = new Lazy<Task<object>>(() => ObserveFault(RunSharedAsync(key, ttl, factory, self!)));
        return self;
    }

    /// <summary>
    /// The shared task can fail after every caller has stopped waiting; reading its exception here keeps that from
    /// surfacing as an unobserved task exception. Callers that do await it still receive the exception.
    /// </summary>
    private static Task<object> ObserveFault(Task<object> task)
    {
        _ = task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task<object> RunSharedAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, Lazy<Task<object>> self) where T : notnull
    {
        try
        {
            var value = await factory(CancellationToken.None).ConfigureAwait(false);
            if (ttl > TimeSpan.Zero) Set(key, value, ttl);
            return value;
        }
        finally { _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, self)); }
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _entries) if (kv.Value.Expires <= now) _entries.TryRemove(kv.Key, out _);
        if (_entries.Count >= MaxEntries)
            foreach (var kv in _entries.OrderBy(k => k.Value.Expires).Take(_entries.Count / 4)) _entries.TryRemove(kv.Key, out _);
    }
}
