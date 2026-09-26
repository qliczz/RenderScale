using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FloppyUtils.Graphics;
using RenderScale.States;

namespace RenderScale;

public sealed class Service
{
    public static Plugin Plugin { get; private set; } = null!;
    public static RenderEvents RenderEvents { get; private set; } = null!;
    public static GameSizeState GameSize { get; private set; } = null!;
    public static Configuration Config { get; internal set; } = null!;
    public static DebugConfiguration DebugConfig { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
}
