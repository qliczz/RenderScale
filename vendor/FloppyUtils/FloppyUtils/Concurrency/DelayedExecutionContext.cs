using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FloppyUtils.Concurrency;

/// <summary>
/// Glorified RunOnFrameworkThread which also supports other threads / contexts.
/// </summary>
public interface IDelayedExecutionContext
{
    string Name { get; }
    Task Run(Action action);
    Task Enqueue(Action action);
    TaskAwaiter GetAwaiter();
}

public sealed class DelayedExecutionContext(string name, Func<bool> canRun, Func<bool>? canRunOnFence = null) : IDelayedExecutionContext
{
    private static readonly Action _queueDone = () => throw new Exception("This should never run!");

    private readonly Queue _queue = new();
    private readonly Func<bool> _canRun = canRun;
    private readonly Func<bool> _canRunOnFence = canRunOnFence ?? canRun;

    public static DelayedExecutionContext Now { get; } = new("Now", static () => true);

    public string Name { get; } = name;

    public void Pump()
    {
        _queue.Pump();
    }

    public Task Run(Action action)
    {
        var (wrapped, task) = WrapTask(action);

        if (_canRun())
        {
            wrapped();
        }
        else
        {
            _queue.Enqueue(wrapped);
        }

        return task;
    }

    public Task Enqueue(Action action)
    {
        var (wrapped, task) = WrapTask(action);
        _queue.Enqueue(wrapped);
        return task;
    }

    public TaskAwaiter GetAwaiter()
    {
        if (_canRunOnFence())
        {
            Pump();
            return Task.CompletedTask.GetAwaiter();
        }

        return Enqueue(() => { }).GetAwaiter();
    }

    private static (Action, Task) WrapTask(Action action)
    {
        var completion = new TaskCompletionSource();
        void Wrapper()
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
                throw;
            }
        }

        return (Wrapper, completion.Task);
    }

    public sealed class Sub(string name, IDelayedExecutionContext parent) : IDelayedExecutionContext
    {
        private readonly IDelayedExecutionContext _parent = parent;
        private readonly Queue _queue = new();

        public string Name { get; } = parent.Name + "." + name;

        public void Pump()
        {
            _queue.Pump();
        }

        public Task Run(Action action)
        {
            var (wrapped, task) = WrapTask(WrapOnce(action));
            _parent.Run(wrapped);
            _queue.Enqueue(wrapped);
            return task;
        }

        public Task Enqueue(Action action)
        {
            var (wrapped, task) = WrapTask(WrapOnce(action));
            _parent.Enqueue(wrapped);
            _queue.Enqueue(wrapped);
            return task;
        }

        public TaskAwaiter GetAwaiter() => Enqueue(() => { }).GetAwaiter();

        private static Action WrapOnce(Action action)
        {
            bool ran = false;
            void Wrapper()
            {
                if (!Interlocked.Exchange(ref ran, true))
                {
                    action();
                }
            }

            return Wrapper;
        }
    }

    private sealed class Queue
    {
        private Lock _pumpLock = new();
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }

        public void Pump()
        {
            lock (_pumpLock)
            {
                _queue.Enqueue(_queueDone);
                while (_queue.TryDequeue(out var queued))
                {
                    if (queued == _queueDone)
                    {
                        return;
                    }
                    queued();
                }
            }
        }
    }
}
