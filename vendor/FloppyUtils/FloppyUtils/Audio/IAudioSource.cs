using System;

namespace FloppyUtils.Audio;

public interface IAudioSource
{
    int SampleRate { get; }
    int Channels { get; }
    Span<float> Get();
}
