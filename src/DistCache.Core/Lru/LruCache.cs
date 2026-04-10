using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DistCache.Core;

/// <summary>
/// Thread-safe least-recently-used cache bounded by total value bytes.
/// The head of the internal list is the most recently used entry; the tail is evicted first when over budget.
/// </summary>
/// <remarks>
/// All operations take a single lock while mutating the <see cref="LinkedList{T}"/> and the companion map.
/// Do not invoke the byte-size callback in a way that re-enters this cache on the same thread while already holding work that waits on the same lock.
/// </remarks>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly long capacityBytes;
    private readonly Func<TValue, long> measure;
    private readonly object sync = new();
    private readonly LinkedList<Entry> order = new();
    private readonly ConcurrentDictionary<TKey, LinkedListNode<Entry>> nodes;

    private long bytesUsed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LruCache{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="maxBytes">Inclusive upper bound on the sum of entry byte sizes; must be positive.</param>
    /// <param name="getByteSize">Returns the byte contribution of a value; must be non-negative for any stored value.</param>
    /// <param name="keyComparer">Optional comparer for keys in the internal map.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxBytes"/> is not positive.</exception>
    public LruCache(long maxBytes, Func<TValue, long> getByteSize, IEqualityComparer<TKey>? keyComparer = null)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Capacity must be positive.");
        }

        ArgumentNullException.ThrowIfNull(getByteSize);
        capacityBytes = maxBytes;
        measure = getByteSize;
        nodes = new ConcurrentDictionary<TKey, LinkedListNode<Entry>>(keyComparer ?? EqualityComparer<TKey>.Default);
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

    /// <summary>
    /// Attempts to get a value and marks the entry as most recently used when found.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the key exists; otherwise <see langword="false"/>.</returns>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (sync)
        {
            if (!nodes.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
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

            if (nodes.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                bytesUsed -= existing.Value.ByteSize;
                existing.Value = new Entry(key, value, size);
                bytesUsed += size;
                MoveToFront(existing);
            }
            else
            {
                LinkedListNode<Entry> node = new(new Entry(key, value, size));
                order.AddFirst(node);
                nodes[key] = node;
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
        public Entry(TKey key, TValue value, long byteSize)
        {
            Key = key;
            Value = value;
            ByteSize = byteSize;
        }

        public TKey Key { get; }

        public TValue Value { get; set; }

        public long ByteSize { get; }
    }
}
