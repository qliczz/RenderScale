using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;

namespace FloppyUtils.Concurrency;

/// <summary>
/// A chain of operations which needs to be executed across multiple ticks,
/// similar to Unity coroutines.
/// Should I pivot to tasks instead? Yeah, but eh. -jade
/// </summary>
public class TickChain(IDelayedExecutionContext executor, IEnumerator coroutine)
{
    private readonly Lock _lock = new();
    private readonly IDelayedExecutionContext _executor = executor;
    private readonly IEnumerator _coroutine = coroutine;
    private TickChainState _state = TickChainState.Pending;
    private TickChainResult _result;
    private string _context = Environment.StackTrace;
    private Exception? _ex;

    public TickChainState State => _state;

    public Exception? Exception => _ex;

    public object? Result => _result.Value;

    public static TickChain Done()
        => new(DelayedExecutionContext.Now, Nop())
        {
            _state = TickChainState.Done
        };

    public static TickChain Run(IDelayedExecutionContext executor, IEnumerator coroutine)
    {
        var tc = new TickChain(executor, coroutine);
        tc.Run();
        return tc;
    }

    public void Run()
    {
        lock (_lock)
        {
            if (_state != TickChainState.Pending)
            {
                throw new InvalidOperationException("Already running or done!");
            }

            _state = TickChainState.Running;
        }

        _executor.Run(Step);
    }

    private void Step()
    {
        try
        {
            if (!_coroutine.MoveNext())
            {
                _state = TickChainState.Done;
                return;
            }

            var curr = _coroutine.Current;
            if (curr is TimeSpan time)
            {
                var timer = Stopwatch.StartNew();
                void SubStep()
                {
                    if (timer.Elapsed < time)
                    {
                        _executor.Run(SubStep);
                    }
                    else
                    {
                        Step();
                    }
                }
                _executor.Enqueue(SubStep);
            }
            else if (curr is int ticks)
            {
                void SubStep()
                {
                    if (ticks > 0)
                    {
                        ticks--;
                        _executor.Run(SubStep);
                    }
                    else
                    {
                        Step();
                    }
                }
                _executor.Enqueue(SubStep);
            }
            else if (curr is TickChainResult result)
            {
                _state = TickChainState.Done;
                _result = result;
            }
            else
            {
                _executor.Enqueue(Step);
            }
        }
        catch (Exception ex)
        {
            FService.PluginLog.Error("Error in TickChain:\nExecutor:\n{0}\n\nContext:\n{1}\n\nException:\n{2}", _executor, _context, ex);
            _state = TickChainState.Done;
            _ex = ex;
        }
    }

    private static IEnumerator Nop()
    {
        yield return null;
    }
}

public class TickChain<T>(IDelayedExecutionContext executor, IEnumerator coroutine) : TickChain(executor, coroutine)
{
    public new T Result => (T) base.Result!;
}

public enum TickChainState
{
    Pending,
    Running,
    Done
}

public record struct TickChainResult(object? Value);
