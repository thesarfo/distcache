using System.Collections.Concurrent;
using System.Diagnostics;

namespace DistCache.Core.Concurrency;

/// <summary>
/// Coalesces concurrent asynchronous work for the same key: every caller for a given key
/// shares one <see cref="Task{TResult}"/>; only the leader runs the supplied operation.
/// </summary>
/// <typeparam name="TKey">The coalescing key; must be suitable for a concurrent dictionary (stable equality and hash code).</typeparam>
/// <typeparam name="TResult">The type of the shared result.</typeparam>
/// <remarks>
/// <para>
/// <see cref="ExecuteAsync(TKey, Func{CancellationToken, ValueTask{TResult}}, CancellationToken)"/>
/// only removes the in-flight entry after the task completes (success, fault, or cancel). If the
/// operation throws, the exception is observed by every waiter for that in-flight work.
/// </para>
/// <para>
/// <see cref="InFlightCount"/> is the number of in-flight coalesced operations (keys currently
/// in the map); it rises when a new leader is registered and falls when the shared task
/// finishes.
/// </para>
/// </remarks>
[DebuggerDisplay("InFlight = {InFlightCount}")]
public sealed class AsyncSingleFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Task<TResult>> inFlight;
    private long inFlightCount;

    /// <summary>Initializes a new instance of the <see cref="AsyncSingleFlight{TKey, TResult}"/> class.</summary>
    /// <param name="comparer">Optional equality comparer for <typeparamref name="TKey"/>; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public AsyncSingleFlight(IEqualityComparer<TKey>? comparer = null) =>
        inFlight = new ConcurrentDictionary<TKey, Task<TResult>>(comparer ?? EqualityComparer<TKey>.Default);

    /// <summary>Gets the number of in-flight (registered, not yet completed) operations.</summary>
    public long InFlightCount => Interlocked.Read(ref inFlightCount);

    /// <summary>
    /// Runs or joins the in-flight <paramref name="operation"/> for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Coalescing key.</param>
    /// <param name="operation">Async work invoked once per burst of waiters. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation for the <em>current</em> caller; this does not cancel the shared work unless the same token is passed to the leader and honored by <paramref name="operation"/>.</param>
    /// <returns>The shared <typeparamref name="TResult"/>.</returns>
    public async ValueTask<TResult> ExecuteAsync(
        TKey key,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(operation);

        while (true)
        {
            if (inFlight.TryGetValue(key, out Task<TResult>? existing))
            {
                return await existing.ConfigureAwait(false);
            }

            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!inFlight.TryAdd(key, tcs.Task))
            {
                continue;
            }

            Interlocked.Increment(ref inFlightCount);
            try
            {
                TResult result = await operation(cancellationToken).ConfigureAwait(false);
                tcs.SetResult(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                inFlight.TryRemove(key, out _);
                Interlocked.Decrement(ref inFlightCount);
            }
        }
    }
}
