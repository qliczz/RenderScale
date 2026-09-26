using System;
using System.Threading;
using System.Threading.Tasks;

namespace FloppyUtils;

/// <summary>
/// Exposes LoadAsync and DisposeAsync in a similar manner to <see cref="Dalamud.Plugin.IAsyncDalamudPlugin"/>.
/// </summary>
public interface IAsyncLoadable : IAsyncDisposable
{
    /// <inheritdoc cref="Dalamud.Plugin.IAsyncDalamudPlugin.LoadAsync(CancellationToken)"/>
    Task LoadAsync(CancellationToken cancellationToken);
}
