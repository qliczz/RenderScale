using System;

namespace FloppyUtils;

/// <summary>
/// A set of rotating objects, to be used with f.e. Locked.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RotatingSet<T>(int size, Func<T> generator)
{
    private readonly T[] _pool = new T[size];
    private readonly Func<T> _generator = generator;

    private int _index = -1;
    private int _allocated;

    public T Next()
    {
        _index = (_index + 1) % _pool.Length;

        if (_index < _allocated)
        {
            return _pool[_index];
        }

        _allocated = _index + 1;
        return _pool[_index] = _generator();
    }
}
