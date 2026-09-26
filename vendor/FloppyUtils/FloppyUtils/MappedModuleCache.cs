using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using ERROR = TerraFX.Interop.Windows.ERROR;

namespace FloppyUtils;

/// <summary>
/// Asynchronously self-clearing process module memory mapping helper.
/// </summary>
public sealed unsafe partial class MappedModuleCache : IDisposable
{
    private static readonly LifetimeHelper.Manager _lifetimeManager = new();

    private readonly LifetimeHelper _lifetime = _lifetimeManager.Create();
    private readonly nint _dllNotificationCookie;

    // TODO: Look into https://github.com/mkrebser/ConcurrentSortedDictionary
    private readonly RWLock _lock = new();
    private readonly SortedDictionary<Key, Info> _all = new(KeyComparer.Instance);

    public MappedModuleCache()
    {
        LdrRegisterDllNotification(
            0,
            (void*) _lifetime.Set(_OnDllNotification, this).Value,
            _lifetime.Handle,
            out _dllNotificationCookie
        );
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void LdrDllNotification(uint NotificationReason, ref LDR_DLL_NOTIFICATION_DATA NotificationData, nint Context);

    private delegate uint GetNameProc(char* namePtr, uint nameSize);

    public void Dispose()
    {
        LdrUnregisterDllNotification(_dllNotificationCookie);
        _lifetime.Dispose();

        using (_lock.EnterWriteLock())
        {
            foreach (var info in _all.Values)
            {
                info.Copy?.Dispose();
            }
        }
    }

    public Info? GetAt(nint addr)
    {
        using (_lock.EnterReadLock())
        {
            if (_all.TryGetValue(new(addr, 0), out var found))
            {
                return found;
            }
        }

        HMODULE _module;
        if (!GetModuleHandleExW(
            GET.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            (char*) addr,
            &_module
        ))
        {
            // Fall back to non-module generic filename info.
            // Old versions of Wine do not support GetMappedFileName. We are not concerned about old versions of Wine.
            var name = GetName(MAX.MAX_PATH, (namePtr, nameSize) =>
                K32GetMappedFileNameW(
                    (HANDLE) Process.GetCurrentProcess().Handle,
                    (void*) addr,
                    namePtr,
                    nameSize
                )
            );
            return name is not null ? new(name, null, 0, 0, null) : null;
        }

        var module = _module;

        try
        {
            MODULEINFO modinfo;
            K32GetModuleInformation(
                (HANDLE) Process.GetCurrentProcess().Handle,
                module,
                &modinfo,
                (uint) sizeof(MODULEINFO)
            );

            var info = new Info(
                GetName(MAX.MAX_PATH, (namePtr, nameSize) =>
                    GetModuleFileNameW(
                        module,
                        namePtr,
                        nameSize
                    )
                ) ?? throw new NullReferenceException("Expected module file name, got none"),
                GetName(MAX.MAX_PATH, (namePtr, nameSize) =>
                    K32GetModuleBaseNameW(
                        (HANDLE) Process.GetCurrentProcess().Handle,
                        module,
                        namePtr,
                        nameSize
                    )
                ),
                (nint) modinfo.lpBaseOfDll,
                (int) modinfo.SizeOfImage,
                null
            );

            try
            {
                info = info with { Copy = new(info.FileName) };
            }
            catch (Exception e)
            {
                // File suddenly doesn't exist? Messed up contents? Locked? ¯\_(ツ)_/¯
                FService.PluginLog.Warning($"ModuleMapper failed to read from {info.FileName}: {e}");
            }

            using (_lock.EnterWriteLock())
            {
                if (_all.TryGetValue(new(addr, 0), out var found))
                {
                    info.Copy?.Dispose();
                    return found;
                }

                _all[new(info.BaseAddress, info.ModuleMemorySize)] = info;

                return info;
            }
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private string? GetName(int nameSize, GetNameProc getName)
    {
        var nameStack = stackalloc char[nameSize + 1];
        char[]? nameHeap = null;
        uint rv;

        do
        {
            SetLastError(0);
            if (nameHeap is not null)
            {
                fixed (char* namePtr = nameHeap)
                {
                    rv = getName(namePtr, (uint) nameSize);
                }
            }
            else
            {
                rv = getName(nameStack, (uint) nameSize);
            }

            if (rv == 0)
            {
                return null;
            }

            if (GetLastError() != ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                break;
            }

            nameSize *= 2;
            nameHeap = new char[nameSize + 1];
        }
        while (true);

        return nameHeap is not null ? new string(nameHeap, 0, (int) rv) : new string(nameStack, 0, (int) rv);
    }

    private static readonly LdrDllNotification _OnDllNotification = OnDllNotification;
    private static void OnDllNotification(uint notificationReason, ref LDR_DLL_NOTIFICATION_DATA notificationData, nint context)
    {
        if (!_lifetimeManager.TryGet(context, out var lifetime) || !lifetime.TryGet(_OnDllNotification, out MappedModuleCache cache))
        {
            return;
        }

        if ((LDR_DLL_NOTIFICATION_REASON) notificationReason != LDR_DLL_NOTIFICATION_REASON.LDR_DLL_NOTIFICATION_REASON_UNLOADED)
        {
            return;
        }

        Info found;
        using (cache._lock.EnterReadLock())
        {
            if (!cache._all.TryGetValue(new((nint) notificationData.DllBase, 0), out found))
            {
                found = default;
            }
        }
        using (cache._lock.EnterWriteLock())
        {
            found.Copy?.Dispose();
            cache._all.Remove(new((nint) notificationData.DllBase, 0));
        }
    }

    [LibraryImport("ntdll", SetLastError = false)]
    private static partial int LdrRegisterDllNotification(uint Flags, void* NotificationFunction, nint Context, out nint Cookie);

    [LibraryImport("ntdll", SetLastError = false)]
    private static partial int LdrUnregisterDllNotification(nint Cookie);

    public readonly record struct Info(string FileName, string? ModuleName, nint BaseAddress, int ModuleMemorySize, MappedModule? Copy);

    private readonly record struct Key(nint BaseAddress, nint ModuleMemorySize);

    // Union of LDR_DLL_LOADED_NOTIFICATION_DATA and LDR_DLL_UNLOADED_NOTIFICATION_DATA,
    // but both are the same...
    [StructLayout(LayoutKind.Sequential)]
    private struct LDR_DLL_NOTIFICATION_DATA
    {
        public uint Flags;
        public UNICODE_STRING* FullDllName;
        public UNICODE_STRING* BaseDllName;
        public void* DllBase;
        public uint SizeOfImage;
    }

    private enum LDR_DLL_NOTIFICATION_REASON
    {
        LDR_DLL_NOTIFICATION_REASON_LOADED = 1,
        LDR_DLL_NOTIFICATION_REASON_UNLOADED = 2
    }

    private sealed class KeyComparer : IComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(Key x, Key y)
        {
            if (x.BaseAddress + x.ModuleMemorySize < y.BaseAddress)
            {
                return (x.BaseAddress + x.ModuleMemorySize).CompareTo(y.BaseAddress);
            }
            if (x.BaseAddress > y.BaseAddress + y.ModuleMemorySize)
            {
                return x.BaseAddress.CompareTo(y.BaseAddress + y.BaseAddress);
            }
            return 0;
        }
    }
}
