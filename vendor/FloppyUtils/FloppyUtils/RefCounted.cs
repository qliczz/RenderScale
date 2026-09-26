using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// A poor person's anti-use-after-free tool.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RefCounted<T>(T value) : IDisposable where T : IDisposable
{
#if DEBUG
    internal readonly List<StackTrace> _accessors = [];
#endif

    internal volatile int _refs = 1;

    public T Value = value;

    public int Dispose()
    {
        var refs = Interlocked.Decrement(ref _refs);

        if (refs == 0)
        {
            Value.Dispose();
        }

        return refs;
    }

    public Access? TryShare()
    {
        while (true)
        {
            int refs = _refs;

            if (refs == 0)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs)
            {
                return new(this);
            }
        }
    }

    void IDisposable.Dispose()
    {
        Dispose();
    }

    public readonly struct Access : IDisposable
    {
#if DEBUG
        private readonly StackTrace _accessor;
#endif

        public readonly RefCounted<T> Ref;

        public Access(RefCounted<T> owner)
        {
            Ref = owner;

#if DEBUG
            _accessor = new StackTrace(0);
            lock (Ref._accessors)
            {
                Ref._accessors.Add(_accessor);
            }
#endif
        }

        public void Dispose()
        {
            Ref.Dispose();

#if DEBUG
            lock (Ref._accessors)
            {
                Ref._accessors.Remove(_accessor);
            }
#endif
        }
    }
}

public static class RefCountedExtensions
{
    // Static extension methods instead of instance methods so that CallerArgumentExpression gives full context.

    public static void Log<T>(
        this RefCounted<T> counted,
        [CallerArgumentExpression(nameof(counted))] string? ctx = null
    ) where T : IDisposable
    {
        FService.PluginLog.Debug($"{counted.GetType().FullName} {ctx} refcount at {counted._refs}");
#if DEBUG
        lock (counted._accessors)
        {
            FService.PluginLog.Debug($"{counted.GetType().FullName} {ctx} accessed by {counted._accessors.Count} accessors");
            foreach (var accessor in counted._accessors)
            {
                FService.PluginLog.Debug(accessor.ToString());
            }
        }
#endif
    }
}
