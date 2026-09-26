using System;

namespace FloppyUtils;

public interface ISampleRing<T> where T : unmanaged
{
    /// <summary>
    /// Gets all data in the ring buffer, including unset samples.
    /// </summary>
    /// <returns></returns>
    Span<T> Get();

    /// <summary>
    /// Submits new data to the ring buffer.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="stride"></param>
    void Submit(Span<T> data, int stride = 1);
}

public sealed class SampleRing<T>(int size = SampleRing<T>.DefaultSize) : ISampleRing<T> where T : unmanaged
{
    public const int DefaultSize = 1024 * 16;

    private readonly T[] _ring = new T[size];
    private readonly T[] _tmp = new T[size];
    private int _ringPos;

    public Span<T> Get()
    {
        var pos = _ringPos;

        var tmpStart = new Span<T>(_tmp, 0, _ring.Length - pos);
        var tmpEnd = new Span<T>(_tmp, tmpStart.Length, pos);
        
        var ringStart = new Span<T>(_ring, pos, tmpStart.Length);
        var ringEnd = new Span<T>(_ring, 0, pos);

        ringStart.CopyTo(tmpStart);
        ringEnd.CopyTo(tmpEnd);

        return _tmp;
    }

    public void Submit(Span<T> data, int stride = 1)
    {
        var pos = _ringPos;

        for (int i = 0; i < data.Length; i += stride)
        {
            _ring[pos] = data[i];
            pos = (pos + 1) % _ring.Length;
        }

        _ringPos = pos;
    }
}
