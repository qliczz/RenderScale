using Dalamud.Game.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Plugin.Services;
using FloppyUtils.Concurrency;
using FloppyUtils.Ptrs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TerraFX.Interop.Windows;

namespace FloppyUtils;

/// <summary>
/// Services and other globals. Make sure to init and dispose!
/// </summary>
public sealed class FService
{
    private static IAsyncDalamudPlugin _plugin = null!;
    private static IDalamudPluginInterface _pluginInterface = null!;
    private static bool _unloading = false;

    private static readonly HashSet<IAsyncLoadable> _loadables = [];
    private static readonly HashSet<IAsyncDisposable> _disposablesAsync = [];
    private static readonly HashSet<IDisposable> _disposables = [];

    /// <summary>
    /// To be called on plugin construction.
    /// </summary>
    /// <param name="plugin"></param>
    /// <param name="pluginInterface"></param>
    /// <returns>False on any errors which permit the plugin to stay loaded. Fatal errors still throw.</returns>
    public static bool TryInit(IAsyncDalamudPlugin plugin, IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<FService>();

        _pluginInterface = pluginInterface;

        Xrepo = new();

        if (Xrepo.WasConflictOnLoad)
        {
            NotifyLoadError("Same plugin installed twice. Remove unwanted versions!");
            return false;
        }

        if (!_pluginInterface.Manifest.CanUnloadAsync)
        {
            PluginLog.Error("CanUnloadAsync was false! FloppyUtils DEPENDS on async unload, otherwise fences WILL deadlock you eventually!");
            NotifyLoadMetadataError("Unload self-test failed.");
            return false;
        }

        MappedModuleCache = new();

        UpdateThread = new("FrameworkUpdate", () => Framework.IsInFrameworkUpdateThread || Framework.IsFrameworkUnloading);
        Framework.Update += OnFrameworkUpdate;

        AutoSingleton.GetAll<object>();

        _plugin = plugin;

        Create<FService>();

        return true;
    }

    public static bool IsReady => _plugin is not null;

    public static string PluginName => _plugin.GetType().Assembly.GetName().Name!;

    public static bool Unloading => _unloading;

    public static MappedModuleCache MappedModuleCache { get; private set; } = null!;

    public static Xrepo Xrepo { get; private set; } = null!;

    public static DelayedExecutionContext UpdateThread { get; private set; } = null!;

    [PluginService]
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    public static IFramework Framework { get; private set; } = null!;

    [PluginService]
    public static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    public static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    public static IPluginLog PluginLog { get; private set; } = null!;

    [PluginService]
    public static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    public static IKeyState KeyState { get; private set; } = null!;

    [PluginService]
    public static IGameConfig GameConfig { get; private set; } = null!;

    [PluginService]
    public static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    public static INotificationManager NotificationManager { get; private set; } = null!;

    /// <summary>
    /// Creates a new instance of the given class if no instance is given,
    /// and fills all AutoSingleton properties.
    /// </summary>
    /// <typeparam name="TService"></typeparam>
    /// <param name="service"></param>
    /// <returns></returns>
    public static TService Create<TService>(TService? service = null) where TService : class, new()
    {
        service ??= new();

        foreach (var prop in typeof(TService).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (prop.GetMethod is not { } get || prop.SetMethod is not { } set)
            {
                continue;
            }

            var inst = get.IsStatic ? null : service;
            object value;

            if (get.Invoke(inst, null) is not null)
            {
                continue;
            }
            else if (prop.PropertyType == _plugin.GetType())
            {
                value = _plugin;
            }
            else if (AutoSingleton.Get(prop.PropertyType) is { } auto)
            {
                value = auto;
            }
            else if (prop.PropertyType.GetConstructor([]) is not null)
            {
                value = Activator.CreateInstance(prop.PropertyType)!;
            }
            else
            {
                PluginLog.Warning($"Couldn't auto-set property {service.GetType().FullName}::{prop.Name} of type {prop.PropertyType.FullName}");
                continue;
            }

            set.Invoke(inst, [value]);

            if (value is IAsyncDalamudPlugin or IDalamudPlugin)
            {
            }
            else if (value is IAsyncLoadable loadable)
            {
                _loadables.Add(loadable);
                _disposablesAsync.Add(loadable);
            }
            else if (value is IAsyncDisposable disposableAsync)
            {
                _disposablesAsync.Add(disposableAsync);
            }
            else if (value is IDisposable disposable)
            {
                _disposables.Add(disposable);
            }
        }

        return service;
    }

    /// <summary>
    /// Initializes all registered instances of IAsyncLoadable.
    /// To be called in plugin LoadAsync.
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public static Task BeginLoadAsync(CancellationToken cancel)
    {
        var tasks = new List<Task>();

        foreach (var loadable in _loadables)
        {
            tasks.Add(loadable.LoadAsync(cancel));
        }

        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// To be called on plugin disposal.
    /// </summary>
    /// <returns></returns>
    public static IFloppyServiceDisposal BeginDisposeAsync()
    {
        return new FloppyServiceDisposal();
    }

    public static void PrintChat(string msg)
    {
        ChatGui.Print(new XivChatEntry
        {
            Message = msg,
            Type = PluginInterface.GeneralChatType
        });
    }

    public static void PrintChatErr(string msg)
    {
        ChatGui.PrintError(msg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Assert([DoesNotReturnIf(false)] bool cond, [CallerArgumentExpression(nameof(cond))] string? ctx = null)
    {
        if (!cond)
        {
            throw new Exception($"Assert failed: {ctx ?? "???"}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertEqualExpected<T>(
        T? got,
        T? wanted,
        [CallerArgumentExpression(nameof(got))] string? ctxGot = null,
        [CallerArgumentExpression(nameof(wanted))] string? ctxWanted = null)
        where T : IEquatable<T>
    {
        if (wanted == null ? got != null : !wanted.Equals(got))
        {
            throw new Exception($"Assert failed: {ctxGot ?? "got???"} == {ctxWanted ?? "ctxWanted???"} (got: {got}) (wanted: {wanted})");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertEqualExpected(
        nint got,
        nint wanted,
        [CallerArgumentExpression(nameof(got))] string? ctxGot = null,
        [CallerArgumentExpression(nameof(wanted))] string? ctxWanted = null)
    {
        if (wanted != got)
        {
            throw new Exception($"Assert failed: {ctxGot ?? "got???"} == {ctxWanted ?? "ctxWanted???"} (got: {got.ToDebugString()}) (wanted: {wanted.ToDebugString()})");
        }
    }

    private static void OnFrameworkUpdate(IFramework framework)
    {
        UpdateThread.Pump();
    }

    private static void NotifyLoadError(string content)
    {
        // An initial duration of 15 might seem absurd, but we're dealing with end users here!
        NotificationManager.AddNotification(new Notification()
        {
            Title = _pluginInterface.Manifest.Name,
            Content = content,
            Type = NotificationType.Error,
            InitialDuration = TimeSpan.FromSeconds(15),
            Minimized = false
        });
    }

    private static void NotifyLoadMetadataError(string content)
    {
        if (_pluginInterface.IsDev)
        {
            NotifyLoadError($"{content}\nCheck the log and fix your metadata.");
        }
        else if (_pluginInterface.SourceRepository == SpecialPluginSource.MainRepo)
        {
            NotifyLoadError($"{content}\nPlease send feedback, thank you!");
        }
        else
        {
            NotifyLoadError($"{content}\nThis is an issue with the 3PP repo you got this from.");
        }
    }

    public interface IFloppyServiceDisposal
    {
        TaskAwaiter GetAwaiter();
    }

    private readonly struct FloppyServiceDisposal : IFloppyServiceDisposal
    {
        private readonly List<Task> _tasks = [];

        public FloppyServiceDisposal()
        {
            _unloading = true;

            foreach (var disposableAsync in _disposablesAsync)
            {
                _tasks.Add(disposableAsync.DisposeAsync().AsTask());
            }

            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
        }

        public TaskAwaiter GetAwaiter()
        {
            return End().GetAwaiter();
        }

        private async Task End()
        {
            if (_plugin is null)
            {
                return;
            }

            await Task.WhenAll(_tasks);

            _plugin = null!;
            _pluginInterface = null!;

            MappedModuleCache.Dispose();

            if (Framework.IsFrameworkUnloading)
            {
                // Possibly dangerous, but alas...
                UpdateThread.Pump();
            }
            else
            {
                await UpdateThread;
            }

            Framework.Update -= OnFrameworkUpdate;
        }
    }
}
