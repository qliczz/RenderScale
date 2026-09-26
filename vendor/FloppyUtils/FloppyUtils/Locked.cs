using System;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// A poor person's anti-race-condition tool.
/// Was used in Tracer together with RotatingSet to f.e. display data
/// on the UI thread while updating whatever could be updated on the framework thread.
/// Definitely has got room for improvement.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class Locked<T>(T value)
{
    private readonly Lock _lock = new();

    public T Value = value;

    public Access WaitOpen()
    {
        _lock.Enter();
        return new(this, true);
    }

    public bool TryOpen(out Access access)
    {
        access = new(this, _lock.TryEnter());
        return access.IsLocked;
    }

    public readonly struct Access : IDisposable
    {
        private readonly Locked<T> _owner;
        private readonly bool _locked;

        internal Access(Locked<T> owner, bool locked)
        {
            _owner = owner;
            _locked = locked;
        }

        public Locked<T> Locked => _owner;

        public T Value => _owner.Value;

        public bool IsLocked => _locked;

        public void Dispose()
        {
            if (_locked)
            {
                _owner._lock.Exit();
            }
        }
    }
}
