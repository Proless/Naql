using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Engine;

/// <summary>
/// Lets at most <see cref="Limit"/> parts run at once, in the order they asked, with a limit that can change while
/// they run: raising it starts waiting parts at once, lowering it lets running parts finish and starts no new ones
/// until fewer run.
/// </summary>
internal sealed class ConnectionGate
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiting = new();
    private int _limit;
    private int _active;

    public ConnectionGate(int limit)
    {
        Limit = limit;
    }

    /// <summary>
    /// Raised outside the lock when a connection frees up or the limit rises, so a waiting caller can use it.
    /// </summary>
    public event Action? Freed;

    public int Limit
    {
        get => Volatile.Read(ref _limit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(Limit));
            bool raised;
            lock (_lock)
            {
                raised = value > _limit;
                _limit = value;
                Admit();
            }
            if (raised)
            {
                Freed?.Invoke();
            }
        }
    }

    /// <summary>
    /// The parts running now.
    /// </summary>
    public int Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// The parts waiting for a connection.
    /// </summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>
    /// Waits for a connection.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (_lock)
        {
            if (_active < _limit && _waiting.Count == 0)
            {
                _active++;
                return Task.CompletedTask;
            }
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return waiter.Task;
    }

    /// <summary>
    /// Gives a connection back.
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            _active--;
            Admit();
        }
        Freed?.Invoke();
    }

    // Lets waiting parts in while there's room; one that was canceled meanwhile doesn't take a connection
    private void Admit()
    {
        while (_active < _limit && _waiting.Count > 0)
        {
            if (_waiting.Dequeue().TrySetResult(true))
            {
                _active++;
            }
        }
    }
}
