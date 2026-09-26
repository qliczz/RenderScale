using Dalamud.Hooking;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FloppyUtils.Concurrency;

/// <summary>
/// Represents a collection of threaded hooks.
/// </summary>
public class ThreadedHookSet : IAsyncDisposable
{
    private readonly HashSet<ThreadedHookProxy> _hooks = [];

    public bool Add(ThreadedHookProxy hook) => _hooks.Add(hook);

    public bool Remove(ThreadedHookProxy hook) => _hooks.Remove(hook);

    public Task EnableAsync()
    {
        var tasks = new List<Task>();

        foreach (var hook in _hooks)
        {
            tasks.Add(hook.Enable());
        }

        return Task.WhenAll(tasks);
    }

    public Task DisableAsync()
    {
        var tasks = new List<Task>();

        foreach (var hook in _hooks)
        {
            tasks.Add(hook.Disable());
        }

        return Task.WhenAll(tasks);
    }

    public async ValueTask DisposeAsync()
    {
        var tasks = new List<Task>();

        foreach (var hook in _hooks)
        {
            tasks.Add(hook.DisposeAsync().AsTask());
        }

        await Task.WhenAll(tasks);
    }

    public TaskAwaiter GetAwaiter()
    {
        var tasks = new List<Task>();

        foreach (var hook in _hooks)
        {
            tasks.Add(Task.Run(async () => await hook));
        }

        return Task.WhenAll(tasks).GetAwaiter();
    }
}
