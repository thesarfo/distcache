using System.Diagnostics.CodeAnalysis;

namespace DistCache.Core.Registry;

/// <summary>
/// A registered key space: the current <see cref="IKeySpace"/> definition and a dedicated local <see cref="LruCache{TKey, TValue}"/> for string keys and byte payloads.
/// </summary>
/// <remarks>
/// A private lock coordinates reads (<see cref="TryGetLocal"/>, <see cref="PutLocal"/>) with <see cref="ApplyUpdate"/> so definition and cache stay consistent.
/// Updates replace the LRU instance; the previous instance is disposed asynchronously outside the lock.
/// </remarks>
public sealed class KeySpaceEntry
{
    private readonly object gate = new();
    private IKeySpace definition;
    private LruCache<string, byte[]>? cache;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeySpaceEntry"/> class from options.
    /// </summary>
    /// <param name="options">Key space configuration; must match <see cref="IKeySpace.Name"/> used as the registry key.</param>
    public KeySpaceEntry(KeySpaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        definition = options;
        cache = CreateLruFrom(options);
    }

    /// <summary>
    /// Gets the current definition. The returned reference is stable for immutable implementations (e.g. <see cref="KeySpaceOptions"/>).
    /// </summary>
    public IKeySpace Definition
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return definition;
            }
        }
    }

    /// <summary>
    /// Stores a value in the local LRU for this key space.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="value">Payload.</param>
    public void PutLocal(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            ThrowIfDisposed();
            cache!.Put(key, value);
        }
    }

    /// <summary>
    /// Attempts to read a value from the local LRU.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="value">Payload when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the entry exists and is valid.</returns>
    public bool TryGetLocal(string key, [MaybeNullWhen(false)] out byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            if (disposed)
            {
                value = default!;
                return false;
            }

            return cache!.TryGet(key, out value);
        }
    }

    /// <summary>
    /// Disposes the local LRU and marks this entry as unusable.
    /// </summary>
    /// <returns>A task that completes when disposal finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        LruCache<string, byte[]>? toDispose;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toDispose = cache;
            cache = null;
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the definition and swaps in a new local LRU built from <paramref name="options"/>.
    /// The previous LRU is disposed asynchronously after the lock is released.
    /// </summary>
    /// <param name="options">New options; <see cref="KeySpaceOptions.Name"/> must match the existing key space name.</param>
    internal void ApplyUpdate(KeySpaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LruCache<string, byte[]>? oldCache;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!string.Equals(definition.Name, options.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException("Options name must match this key space name.", nameof(options));
            }

            oldCache = cache;
            definition = options;
            cache = CreateLruFrom(options);
        }

        if (oldCache is not null)
        {
            _ = Task.Run(async () =>
            {
                await oldCache.DisposeAsync().ConfigureAwait(false);
            });
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private LruCache<string, byte[]> CreateLruFrom(IKeySpace space)
    {
        long maxBytes = space.MaxBytes ?? long.MaxValue;
        return new LruCache<string, byte[]>(
            maxBytes: maxBytes,
            getByteSize: static b => b.LongLength,
            keyComparer: null,
            entryTimeToLive: space.Ttl,
            sweepInterval: space.SweepInterval);
    }
}
