using Dalamud.Interface.Style;
using System;

namespace FloppyUtils.Style;

/// <summary>
/// Disposable utility to easily apply a <see cref="StyleModel"/> for some ImGui calls.
/// </summary>
public readonly struct StyleModelScope : IDisposable
{
    private readonly StyleModel? _style;

    public StyleModelScope(StyleModel? style)
    {
        _style = style;
        _style?.Push();
    }

    public void Dispose()
    {
        _style?.Pop();
    }
}
