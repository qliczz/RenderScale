using Dalamud.IoC;
using System;
using System.Runtime.Loader;

namespace FloppyUtils;

/// <summary>
/// Helper to obtain internal Dalamud services. Should only be used as a last resort!
/// If you REALLY need to use this, ask in the Dalamud Discord for alternatives first.
/// (Yes, this even applies to myself.)
/// </summary>
public static class DalamudInternalServiceHelper
{
    public static AssemblyLoadContext ALC => AssemblyLoadContext.GetLoadContext(typeof(PluginServiceAttribute).Assembly) ?? AssemblyLoadContext.Default;

    public static object? Get(string name)
    {
        try
        {
            if (typeof(PluginServiceAttribute).Assembly.GetType("Dalamud.Service`1") is not { } serviceContainerContainer)
            {
                FService.PluginLog.Warning("Couldn't find the service container type.");
                return null;
            }

            if (typeof(PluginServiceAttribute).Assembly.GetType(name) is not { } serviceType)
            {
                FService.PluginLog.Warning($"Couldn't find the service type: {name}");
                return null;
            }

            serviceContainerContainer = serviceContainerContainer.MakeGenericType(serviceType);

            if (serviceContainerContainer.GetMethod("Get")?.Invoke(null, []) is not object value)
            {
                FService.PluginLog.Warning($"Couldn't find the service instance: {name}");
                return null;
            }

            return value;
        }
        catch (Exception e)
        {
            FService.PluginLog.Warning($"Couldn't find the service: {name}: {e}");
            return null;
        }
    }
}
