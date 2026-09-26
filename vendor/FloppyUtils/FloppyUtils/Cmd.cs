using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using System;

namespace FloppyUtils;

/// <summary>
/// Abstract command class, one of a thousand ways to handle Dalamud command registration.
/// </summary>
public abstract class Cmd : IDisposable
{
    private ICommandManager? _commandManager;
    private string? _commandString;

    public abstract string Name { get; }
    public string FullName => _commandString ?? $"/{Name}";

    public abstract string HelpMessage { get; }

    public void Register(ICommandManager commandManager)
    {
        if (_commandManager is not null)
        {
            Dispose();
        }

        _commandManager = commandManager;
        _commandString = FullName;

        commandManager.AddHandler(FullName, new CommandInfo(Handle)
        {
            HelpMessage = HelpMessage
        });
    }

    public abstract void Run(string arguments);

    public void Dispose()
    {
        _commandManager?.RemoveHandler(FullName);
        _commandManager = null;

        GC.SuppressFinalize(this);
    }

    private void Handle(string command, string arguments)
    {
        if (command != _commandString)
        {
            return;
        }

        Run(arguments);
    }
}
