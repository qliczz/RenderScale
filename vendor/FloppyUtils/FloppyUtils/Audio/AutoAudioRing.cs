using System;

namespace FloppyUtils.Audio;

/// <summary>
/// <see cref="AudioRing"/> attached to an <see cref="AudioState.AudioRendererState"/>.
/// </summary>
public sealed class AutoAudioRing : IDisposable, IAudioSource
{
    private readonly AudioState.AudioRendererState _audioRenderer;
    private readonly AudioRing _ring;

    public int SampleRate => _ring.SampleRate;
    public int Channels => _ring.Channels;

    public AutoAudioRing(AudioState.AudioRendererState audioRenderer, int size = AudioRing.DefaultSize)
    {
        _audioRenderer = audioRenderer;
        _ring = new(audioRenderer.SampleRate, audioRenderer.Channels, size * audioRenderer.Channels);
        _audioRenderer.OnSubmit += OnAudioSubmit;
    }

    public void Dispose()
    {
        _audioRenderer.OnSubmit -= OnAudioSubmit;
    }

    /// <inheritdoc cref="AudioRing.Get"/>
    public Span<float> Get() => _ring.Get();

    private void OnAudioSubmit(AudioState.AudioRendererState state, Span<float> data) => _ring.Submit(data, 1);
}
