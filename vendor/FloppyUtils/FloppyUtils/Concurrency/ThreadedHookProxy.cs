using Dalamud.Hooking;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FloppyUtils.Concurrency;

/// <summary>
/// Because hooks and threads are a nice combo...
/// </summary>
public class ThreadedHookProxy(IDelayedExecutionContext context, IDalamudHook inner, string? info) : IDalamudHook, IAsyncDisposable
{
    private bool _wantsDispose;
    private bool _gotDispose;
    private Task _pending = Task.CompletedTask;

    ~ThreadedHookProxy()
    {
        if (_gotDispose)
        {
            return;
        }

        try
        {
            FService.PluginLog.Error($"Failed to {(_wantsDispose ? "CALL" : "REACH")} dispose {GetType().FullName}, attached to {Context.Name}, info: {Info}");
        }
        catch
        {
        }
    }

    public IDelayedExecutionContext Context { get; } = context;

    public IDalamudHook Inner { get; } = inner;

    public string? Info { get; } = info;

    public nint Address => Inner.Address;

    public bool IsEnabled => Inner.IsEnabled;

    public bool IsDisposed => Inner.IsDisposed;

    public string BackendName => Inner.BackendName;

    public async ValueTask DisposeAsync()
    {
        _wantsDispose = true;

        await (_pending = Context.Run(() =>
        {
            Inner.Dispose();
            _gotDispose = true;
            GC.SuppressFinalize(this);
        }));
    }

    void IDisposable.Dispose()
    {
        _wantsDispose = true;
        
        _pending = Context.Run(() =>
        {
            Inner.Dispose();
            _gotDispose = true;
            GC.SuppressFinalize(this);
        });
    }

    public Task Enable() => _pending = Context.Run(Inner.Enable);

    public Task Disable() => _pending = Context.Run(Inner.Disable);

    public TaskAwaiter GetAwaiter() => _pending.GetAwaiter();
}

public class ThreadedHookProxy<T>(IDelayedExecutionContext context, Hook<T> inner, string? info)
    : ThreadedHookProxy(context, inner, info)
    where T : Delegate
{
    public new Hook<T> Inner => (Hook<T>) base.Inner;

    public T OriginalDisposeSafe => Inner.OriginalDisposeSafe;
}

public static class ThreadedHookProxyExtensions
{
    public static ThreadedHookProxy On(this IDalamudHook hook, IDelayedExecutionContext context, [CallerArgumentExpression(nameof(hook))] string? info = null)
        => new(context, hook, info);

    public static ThreadedHookProxy<T> On<T>(this Hook<T> hook, IDelayedExecutionContext context, [CallerArgumentExpression(nameof(hook))] string? info = null)
        where T : Delegate
        => new(context, hook, info);
}
