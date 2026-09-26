using System;

namespace FloppyUtils.Audio;

public sealed class AudioRing(int rate, int channels, int size = AudioRing.DefaultSize) : ISampleRing<float>, IAudioSource
{
    public const int DefaultSize = 1024 * 16;

    private readonly SampleRing<float> _ring = new(size);

    public int SampleRate { get; } = rate;
    public int Channels { get; } = channels;

    public Span<float> Get() => _ring.Get();

    public void Submit(Span<float> data, int stride) => _ring.Submit(data, stride);
}
