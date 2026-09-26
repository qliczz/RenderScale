using System;
using System.Threading;

namespace FloppyUtils.Concurrency;

/// <summary>
/// Helper for <see cref="DelayedExecutionContext"/> to allow for checking
/// if a given thread is permitted to run queued tasks immediately as opposed
/// to having to await a fully delayed queued execution.
/// </summary>
public sealed class DelayedExecutionThreadShortCircuit : IDisposable
{
    private bool _disposed;
    private Thread? _thread;
    private bool _threadSet;

    public bool IsReady => _threadSet && !_disposed;

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Sets the currently expected thread to be able to run actions immediately on.
    /// </summary>
    /// <param name="thread"></param>
    public void Set(Thread? thread)
    {
        _thread = thread;
        _threadSet = true;
    }

    /// <summary>
    /// Whether the given thread can run actions immediately.
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool CanRun(Thread other)
    {
        return _disposed || !_threadSet || (_threadSet && _thread == other);
    }
}
