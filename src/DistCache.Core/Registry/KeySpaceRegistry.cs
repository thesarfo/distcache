using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DistCache.Core.Registry;

/// <summary>
/// Thread-safe registry of named key spaces, each with its own <see cref="KeySpaceEntry"/> and local LRU.
/// </summary>
public sealed class KeySpaceRegistry
{
    private readonly ConcurrentDictionary<string, KeySpaceEntry> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a new key space. Names must be unique and non-empty.
    /// </summary>
    /// <param name="options">Configuration for the key space.</param>
    /// <exception cref="ArgumentException">Thrown when the name is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a key space with the same name is already registered.</exception>
    public void Register(KeySpaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.Name);

        KeySpaceEntry entry = new(options);
        if (!entries.TryAdd(options.Name, entry))
        {
            entry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"A key space named '{options.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Removes a key space by name and disposes its local cache.
    /// </summary>
    /// <param name="name">Key space name.</param>
    /// <returns><see langword="true"/> if an entry was removed; otherwise <see langword="false"/>.</returns>
    public async ValueTask<bool> UnregisterAsync(string name)
    {
        if (!entries.TryRemove(name, out KeySpaceEntry? entry))
        {
            return false;
        }

        await entry.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Attempts to resolve a registered key space by name.
    /// </summary>
    /// <param name="name">Key space name.</param>
    /// <param name="entry">The entry when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the name is registered.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out KeySpaceEntry? entry)
    {
        return entries.TryGetValue(name, out entry);
    }

    /// <summary>
    /// Replaces the definition and local LRU for an existing key space.
    /// </summary>
    /// <param name="options">New configuration; <see cref="KeySpaceOptions.Name"/> identifies the key space.</param>
    /// <exception cref="ArgumentException">Thrown when the name is null, empty, or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when no key space with that name exists.</exception>
    public void Update(KeySpaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.Name);

        if (!entries.TryGetValue(options.Name, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{options.Name}' is registered.");
        }

        entry.ApplyUpdate(options);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Key space name must be non-empty.", nameof(name));
        }
    }
}
