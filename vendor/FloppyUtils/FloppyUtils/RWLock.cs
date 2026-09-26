using System;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// Abomination lifted from the corpse of a 2020 project of mine.
/// </summary>
public sealed class RWLock : IDisposable
{
    private readonly Lock _selflock = new();
    private ReaderWriterLockSlim? _rwlock = new(LockRecursionPolicy.SupportsRecursion);

    public void Dispose()
    {
        if (_rwlock is null)
        {
            return;
        }

        if (_rwlock.IsReadLockHeld ||
            _rwlock.IsUpgradeableReadLockHeld ||
            _rwlock.IsWriteLockHeld)
        {
            throw new InvalidOperationException("Cannot dispose a RWLock from a codepath that is actively using RWLock");
        }

        lock (_selflock)
        {
            _rwlock.EnterWriteLock();
            _rwlock.ExitWriteLock();
            _rwlock.Dispose();
            _rwlock = null;
        }
    }

    public RLock EnterReadLock()
    {
        lock (_selflock)
        {
            return new(_rwlock);
        }
    }

    public RULock EnterUpgradeableReadLock()
    {
        lock (_selflock)
        {
            return new(_rwlock);
        }
    }

    public WLock EnterWriteLock()
    {
        lock (_selflock)
        {
            return new(_rwlock);
        }
    }

    public readonly struct RLock : IDisposable
    {
        private readonly ReaderWriterLockSlim? _rwlock;

        internal RLock(ReaderWriterLockSlim? rwlock)
        {
            _rwlock = rwlock;
            _rwlock?.EnterReadLock();
        }

        public void Dispose() => _rwlock?.ExitReadLock();
    }

    public readonly struct RULock : IDisposable
    {
        private readonly ReaderWriterLockSlim? _rwlock;

        internal RULock(ReaderWriterLockSlim? rwlock)
        {
            _rwlock = rwlock;
            _rwlock?.EnterUpgradeableReadLock();
        }

        public void Dispose() => _rwlock?.ExitUpgradeableReadLock();
    }

    public readonly struct WLock : IDisposable
    {
        private readonly ReaderWriterLockSlim? _rwlock;

        internal WLock(ReaderWriterLockSlim? rwlock)
        {
            _rwlock = rwlock;
            _rwlock?.EnterWriteLock();
        }

        public void Dispose() => _rwlock?.ExitWriteLock();
    }
}
