using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DistCache.Core;

/// <summary>
/// Thread-safe least-recently-used cache bounded by total value bytes, with optional per-entry time-to-live and background expiry sweeps.
/// The head of the internal list is the most recently used entry; the tail is evicted first when over budget.
/// </summary>
/// <remarks>
/// All operations take a single lock while mutating the <see cref="LinkedList{T}"/> and the companion map.
/// Reads do not wait on the background sweeper; sweeper work may briefly contend on the same lock as <see cref="TryGet"/>.
/// When a periodic sweep is configured, call <see cref="DisposeAsync"/> (e.g. <c>await using</c>) to stop the timer and release resources.
/// Do not invoke the byte-size callback in a way that re-enters this cache on the same thread while already holding work that waits on the same lock.
/// </remarks>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class LruCache<TKey, TValue> : IAsyncDisposable
    where TKey : notnull
{
    private readonly long capacityBytes;
    private readonly Func<TValue, long> measure;
    private readonly TimeSpan? entryTimeToLive;
    private readonly object sync = new();
    private readonly LinkedList<Entry> order = new();
    private readonly ConcurrentDictionary<TKey, LinkedListNode<Entry>> nodes;

    private long bytesUsed;

    private PeriodicTimer? sweeperTimer;
    private CancellationTokenSource? sweeperCancellation;
    private Task? sweeperTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="LruCache{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="maxBytes">Inclusive upper bound on the sum of entry byte sizes; must be positive.</param>
    /// <param name="getByteSize">Returns the byte contribution of a value; must be non-negative for any stored value.</param>
    /// <param name="keyComparer">Optional comparer for keys in the internal map.</param>
    /// <param name="entryTimeToLive">
    /// When set, entries expire after this duration from insertion or update (UTC clock). When <see langword="null"/>, entries do not expire by time.
    /// </param>
    /// <param name="sweepInterval">
    /// When set with <paramref name="entryTimeToLive"/>, runs a background task on this interval to remove expired entries.
    /// Must be positive. When <see langword="null"/>, no periodic sweep is started (expiry is still checked on <see cref="TryGet"/>).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxBytes"/> is not positive, or when <paramref name="sweepInterval"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="sweepInterval"/> is set but <paramref name="entryTimeToLive"/> is <see langword="null"/>.</exception>
    public LruCache(
        long maxBytes,
        Func<TValue, long> getByteSize,
        IEqualityComparer<TKey>? keyComparer = null,
        TimeSpan? entryTimeToLive = null,
        TimeSpan? sweepInterval = null)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Capacity must be positive.");
        }

        ArgumentNullException.ThrowIfNull(getByteSize);

        if (sweepInterval is not null && entryTimeToLive is null)
        {
            throw new InvalidOperationException("A sweep interval requires a non-null entry time-to-live.");
        }

        if (sweepInterval is not null && sweepInterval.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), sweepInterval, "Sweep interval must be positive.");
        }

        capacityBytes = maxBytes;
        measure = getByteSize;
        this.entryTimeToLive = entryTimeToLive;
        nodes = new ConcurrentDictionary<TKey, LinkedListNode<Entry>>(keyComparer ?? EqualityComparer<TKey>.Default);

        if (sweepInterval is not null)
        {
            sweeperCancellation = new CancellationTokenSource();
            sweeperTimer = new PeriodicTimer(sweepInterval.Value);
            CancellationToken token = sweeperCancellation.Token;
            sweeperTask = Task.Run(() => RunSweeperAsync(token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Gets the number of entries currently in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (sync)
            {
                return nodes.Count;
            }
        }
    }

    /// <summary>
    /// Gets the sum of byte sizes for all entries currently in the cache.
    /// </summary>
    public long TotalBytes
    {
        get
        {
            lock (sync)
            {
                return bytesUsed;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        PeriodicTimer? timer = sweeperTimer;
        CancellationTokenSource? cts = sweeperCancellation;
        Task? loop = sweeperTask;

        if (timer is null && cts is null)
        {
            return;
        }

        cts?.Cancel();
        timer?.Dispose();

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();

        sweeperTimer = null;
        sweeperCancellation = null;
        sweeperTask = null;
    }

    /// <summary>
    /// Attempts to get a value and marks the entry as most recently used when found and not expired.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the key exists and has not expired; otherwise <see langword="false"/>.</returns>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (sync)
        {
            if (!nodes.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = default!;
                return false;
            }

            if (IsExpired(node.Value))
            {
                RemoveNodeInternal(node);
                value = default!;
                return false;
            }

            value = node.Value.Value;
            MoveToFront(node);
            return true;
        }
    }

    /// <summary>
    /// Inserts or replaces a value for the key. Evicts least-recently-used entries until the total byte size is within budget.
    /// If the measured byte size of the new value exceeds the cache maximum, any existing entry for the key is removed and the new value is not stored.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value to store.</param>
    public void Put(TKey key, TValue value)
    {
        long size = measure(value);
        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Byte size cannot be negative.");
        }

        lock (sync)
        {
            if (size > capacityBytes)
            {
                if (nodes.TryGetValue(key, out LinkedListNode<Entry>? tooLargeExisting))
                {
                    RemoveNodeInternal(tooLargeExisting);
                }

                return;
            }

            DateTime expiresAt = ComputeExpiresAtUtc();

            if (nodes.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                bytesUsed -= existing.Value.ByteSize;
                existing.Value = new Entry(key, value, size, expiresAt);
                bytesUsed += size;
                MoveToFront(existing);
            }
            else
            {
                LinkedListNode<Entry> newNode = new(new Entry(key, value, size, expiresAt));
                order.AddFirst(newNode);
                nodes[key] = newNode;
                bytesUsed += size;
            }

            EvictWhileOverCapacity();
        }
    }

    /// <summary>
    /// Removes an entry for the key if it exists.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns><see langword="true"/> if an entry was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(TKey key)
    {
        lock (sync)
        {
            if (!nodes.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                return false;
            }

            RemoveNodeInternal(node);
            return true;
        }
    }

    /// <summary>
    /// Removes all entries from the cache.
    /// </summary>
    public void Clear()
    {
        lock (sync)
        {
            order.Clear();
            nodes.Clear();
            bytesUsed = 0;
        }
    }

    private DateTime ComputeExpiresAtUtc()
    {
        return entryTimeToLive is null ? DateTime.MaxValue : DateTime.UtcNow + entryTimeToLive.Value;
    }

    private bool IsExpired(Entry entry)
    {
        return DateTime.UtcNow >= entry.ExpiresAtUtc;
    }

    private void SweepExpired()
    {
        lock (sync)
        {
            LinkedListNode<Entry>? current = order.First;
            while (current is not null)
            {
                LinkedListNode<Entry>? next = current.Next;
                if (IsExpired(current.Value))
                {
                    RemoveNodeInternal(current);
                }

                current = next;
            }
        }
    }

    private async Task RunSweeperAsync(CancellationToken cancellationToken)
    {
        PeriodicTimer? timer = sweeperTimer;
        if (timer is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool hasNext;
                try
                {
                    hasNext = await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                SweepExpired();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MoveToFront(LinkedListNode<Entry> node)
    {
        if (node == order.First)
        {
            return;
        }

        order.Remove(node);
        order.AddFirst(node);
    }

    private void RemoveNodeInternal(LinkedListNode<Entry> node)
    {
        bytesUsed -= node.Value.ByteSize;
        nodes.TryRemove(node.Value.Key, out _);
        order.Remove(node);
    }

    private void EvictWhileOverCapacity()
    {
        while (bytesUsed > capacityBytes && order.Last is not null)
        {
            RemoveNodeInternal(order.Last);
        }
    }

    private sealed class Entry
    {
        public Entry(TKey key, TValue value, long byteSize, DateTime expiresAtUtc)
        {
            Key = key;
            Value = value;
            ByteSize = byteSize;
            ExpiresAtUtc = expiresAtUtc;
        }

        public TKey Key { get; }

        public TValue Value { get; set; }

        public long ByteSize { get; }

        public DateTime ExpiresAtUtc { get; }
    }
}
