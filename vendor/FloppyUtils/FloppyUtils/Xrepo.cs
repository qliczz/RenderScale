using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace FloppyUtils;

/// <summary>
/// Utility to allow for multiple variants of the same plugin to be installed
/// from multiple sources without stepping on each other's feet too much.
/// In case both an official and external repo version is installed,
/// always prefer the newer version, or whichever is loaded first.
/// External repo variants should use the tag orig:InternalNameHere
/// </summary>
public sealed class Xrepo
{
    public Xrepo()
    {
        Self = Parse(FService.PluginInterface);

        foreach (var plugin in FService.PluginInterface.InstalledPlugins)
        {
            if (plugin is null || plugin.InternalName == Self.InternalName ||
                plugin.IsOutdated)
            {
                continue;
            }

            var other = Parse(plugin);
            if (other.InternalName != Self.OriginalName &&
                other.OriginalName != Self.OriginalName)
            {
                continue;
            }

            if (other.IsDev && !other.IsLoaded)
            {
                continue;
            }

            if (other.IsLoaded)
            {
                FService.PluginLog.Warning($"Another plugin of same root plugin already loaded: {other}");
                Conflict();
                break;
            }

            if (Self.Version < other.Version)
            {
                FService.PluginLog.Warning($"Another plugin of same root plugin found with newer version: {other}");
                Conflict();
                break;
            }

            if (other.InternalName == Self.OriginalName)
            {
                FService.PluginLog.Warning($"Original plugin of same root plugin found: {other}");
                Conflict();
                break;
            }
        }
    }

    public Info Self { get; }

    public bool WasConflictOnLoad { get; private set; }

    private void Conflict()
    {
        WasConflictOnLoad = true;
    }

    private static Info Parse(IDalamudPluginInterface plugin) => new(
        plugin.InternalName,
        Parse(plugin.Manifest.Tags) ?? plugin.InternalName,
        plugin.IsDev ? new Version(int.MaxValue, 0) : plugin.Manifest.AssemblyVersion,
        plugin.IsDev,
        true
    );

    private static Info Parse(IExposedPlugin plugin) => new(
        plugin.InternalName,
        Parse(plugin.Manifest.Tags) ?? plugin.InternalName,
        plugin.IsDev && plugin.IsLoaded ? new Version(int.MaxValue, 0) : plugin.Version,
        plugin.IsDev,
        plugin.IsLoaded
    );

    private static string? Parse(List<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (tag.StartsWith("orig:"))
            {
                return tag["orig:".Length..];
            }
        }

        return null;
    }

    public readonly record struct Info(string InternalName, string OriginalName, Version Version, bool IsDev, bool IsLoaded)
    {
        public bool IsOriginal => InternalName == OriginalName;
    }
}
