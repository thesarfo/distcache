namespace DistCache.Tests.Engine;

/// <summary>
/// Minimal thread-safe subject for injecting observable events in tests without adding System.Reactive.
/// Implements both <see cref="IObservable{T}"/> (so it can be returned from a stub) and
/// <see cref="IObserver{T}"/> (so tests can push events via <see cref="OnNext"/>).
/// </summary>
internal sealed class SimpleSubject<T> : IObservable<T>, IObserver<T>
{
    private readonly List<IObserver<T>> observers = [];
    private readonly object observersLock = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (observersLock)
        {
            observers.Add(observer);
        }

        return new Unsubscriber(observers, observer, observersLock);
    }

    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (observersLock)
        {
            snapshot = [.. observers];
        }

        foreach (IObserver<T> o in snapshot)
        {
            o.OnNext(value);
        }
    }

    public void OnError(Exception error)
    {
        IObserver<T>[] snapshot;
        lock (observersLock)
        {
            snapshot = [.. observers];
        }

        foreach (IObserver<T> o in snapshot)
        {
            o.OnError(error);
        }
    }

    public void OnCompleted()
    {
        IObserver<T>[] snapshot;
        lock (observersLock)
        {
            snapshot = [.. observers];
        }

        foreach (IObserver<T> o in snapshot)
        {
            o.OnCompleted();
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly List<IObserver<T>> observers;
        private readonly IObserver<T> observer;
        private readonly object @lock;

        internal Unsubscriber(List<IObserver<T>> observers, IObserver<T> observer, object @lock)
        {
            this.observers = observers;
            this.observer = observer;
            this.@lock = @lock;
        }

        public void Dispose()
        {
            lock (@lock)
            {
                observers.Remove(observer);
            }
        }
    }
}
