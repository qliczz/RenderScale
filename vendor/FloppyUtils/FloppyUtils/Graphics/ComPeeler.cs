// Based on https://github.com/goatcorp/Dalamud/blob/c93f04f0e47da91d5425f04f8fe44a804cffcc7a/Dalamud/Interface/ImGuiBackend/Helpers/ReShadePeeler.cs
using FloppyUtils.Ptrs;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FloppyUtils.Graphics;

/// <summary>
/// Peels ReShade and co off stuff.
/// </summary>
public static unsafe class ComPeeler
{
    public static Result PeelIUnknown<T>(ComPtr<T>* comptr, nint vtblSize)
        where T : unmanaged, IUnknown.Interface
    {
        FService.PluginLog.Debug($"ComPeeler.PeelIUnknown<{typeof(T).Name}>({((nint) comptr->Get()).ToDebugString()}, {vtblSize})");
        Result result = 0;
        Result candidate = 0;
        while (comptr->Get() != null && (candidate = IsWrappedComObject(comptr->Get())) != 0)
        {
            // Expectation: the pointer to the underlying object should come early after the overriden vtable.
            for (nint i = 8; i <= 0x20; i += 8)
            {
                var ppObjectBehind = (nint) comptr->Get() + i;

                // Is the thing directly pointed from the address an actual something in the memory?
                if (!IsValidReadableMemoryAddress(ppObjectBehind, 8))
                    continue;

                var pObjectBehind = *(nint*) ppObjectBehind;

                // Is the address of vtable readable?
                if (!IsValidReadableMemoryAddress(pObjectBehind, sizeof(nint)))
                    continue;
                var pObjectBehindVtbl = *(nint*) pObjectBehind;

                // Is the vtable itself readable?
                if (!IsValidReadableMemoryAddress(pObjectBehindVtbl, vtblSize))
                    continue;

                // Are individual functions in vtable executable?
                var valid = true;
                for (var j = 0; valid && j < vtblSize; j += sizeof(nint))
                    valid &= IsValidExecutableMemoryAddress(*(nint*) (pObjectBehindVtbl + j), 1);
                if (!valid)
                    continue;

                // Interpret the object as an IUnknown.
                // Note that `using` is not used, and `Attach` is used. We do not alter the reference count yet.
                var punk = default(ComPtr<IUnknown>);
                punk.Attach((IUnknown*) pObjectBehind);

                // Is the IUnknown object also the type we want?
                using var comptr2 = default(ComPtr<T>);
                if (punk.As(&comptr2).FAILED)
                    continue;

                comptr2.Swap(comptr);
                result |= candidate;
                break;
            }

            if (result == 0)
                break;
        }

        FService.PluginLog.Debug($"ComPeeler.PeelIUnknown<{typeof(T).Name}>({((nint) comptr->Get()).ToDebugString()}, {vtblSize}) = {result}");
        return result;
    }

    private static bool BelongsInReShadeDll(nint ptr)
    {
        if (FService.MappedModuleCache.GetAt(ptr) is not { } module)
        {
            return false;
        }

        fixed (byte* pfn0 = "CreateDXGIFactory"u8)
        fixed (byte* pfn1 = "D2D1CreateDevice"u8)
        fixed (byte* pfn2 = "D3D10CreateDevice"u8)
        fixed (byte* pfn3 = "D3D11CreateDevice"u8)
        fixed (byte* pfn4 = "D3D12CreateDevice"u8)
        fixed (byte* pfn5 = "glBegin"u8)
        fixed (byte* pfn6 = "vkCreateDevice"u8)
        {
            if (GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn0) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn1) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn2) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn3) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn4) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn5) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn6) == null)
            {
                return false;
            }
        }

        var fileInfo = FileVersionInfo.GetVersionInfo(module.FileName);

        if (fileInfo.FileDescription == null)
        {
            return false;
        }

        if (!fileInfo.FileDescription.Contains("GShade", StringComparison.OrdinalIgnoreCase) &&
            !fileInfo.FileDescription.Contains("ReShade", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FService.PluginLog.Info("Peeled ReShade");
        return true;
    }

    private static bool BelongsInOptiScalerDll(nint ptr)
    {
        if (FService.MappedModuleCache.GetAt(ptr) is not { } module)
        {
            return false;
        }

        fixed (byte* pfn0 = "CreateDXGIFactory"u8)
        fixed (byte* pfn1 = "D3D12CreateDevice"u8)
        fixed (byte* pfn2 = "FtpGetFileA"u8)
        fixed (byte* pfn3 = "PlaySoundW"u8)
        fixed (byte* pfn4 = "SymAddSymbolW"u8)
        fixed (byte* pfn5 = "VerFindFileW"u8)
        fixed (byte* pfn6 = "mmioWrite"u8)
        {
            if (GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn0) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn1) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn2) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn3) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn4) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn5) == null ||
                GetProcAddress((HMODULE) module.BaseAddress, (sbyte*) pfn6) == null)
            {
                return false;
            }
        }

        var fileInfo = FileVersionInfo.GetVersionInfo(module.FileName);

        if (fileInfo.FileDescription == null)
        {
            return false;
        }

        if (!fileInfo.FileDescription.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FService.PluginLog.Info("Peeled OptiScaler");
        return true;
    }

    private static bool BelongsInNvPresentDll(nint ptr)
    {
        if (FService.MappedModuleCache.GetAt(ptr) is { ModuleName: { } } module &&
            module.ModuleName.Contains("NvPresent", StringComparison.OrdinalIgnoreCase))
        {
            FService.PluginLog.Info("Peeled NvPresent");
            return true;
        }

        return false;
    }

    private static Result IsWrappedComObject<T>(T* obj)
        where T : unmanaged, IUnknown.Interface
    {
        if (!IsValidReadableMemoryAddress((nint) obj, sizeof(nint)))
            return 0;

        try
        {
            var vtbl = (nint**) Marshal.ReadIntPtr((nint) obj);
            if (!IsValidReadableMemoryAddress((nint) vtbl, sizeof(nint) * 3))
                return 0;

            Result candidate = 0;
            for (var i = 0; i < 3; i++)
            {
                var pfn = Marshal.ReadIntPtr((nint) (vtbl + i));
                if (!IsValidExecutableMemoryAddress(pfn, 1))
                    return 0;

                if (BelongsInReShadeDll(pfn))
                    candidate |= Result.ReShade;
                else if (BelongsInOptiScalerDll(pfn))
                    candidate |= Result.OptiScaler;
                else if (BelongsInNvPresentDll(pfn))
                    candidate |= Result.NvPresent;
                else
                    return 0;
            }

            return candidate;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsValidReadableMemoryAddress(nint p, nint size)
    {
        while (size > 0)
        {
            if (!IsValidUserspaceMemoryAddress(p))
                return false;

            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery((void*) p, &mbi, (nuint) sizeof(MEMORY_BASIC_INFORMATION)) == 0)
                return false;

            if (mbi is not
                {
                    State: MEM.MEM_COMMIT,
                    Protect: PAGE.PAGE_READONLY or PAGE.PAGE_READWRITE or PAGE.PAGE_EXECUTE_READ
                    or PAGE.PAGE_EXECUTE_READWRITE,
                })
                return false;

            var regionSize = (nint) ((mbi.RegionSize + 0xFFFUL) & ~0x1000UL);
            var checkedSize = ((nint) mbi.BaseAddress + regionSize) - p;
            size -= checkedSize;
            p += checkedSize;
        }

        return true;
    }

    private static bool IsValidExecutableMemoryAddress(nint p, nint size)
    {
        while (size > 0)
        {
            if (!IsValidUserspaceMemoryAddress(p))
                return false;

            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery((void*) p, &mbi, (nuint) sizeof(MEMORY_BASIC_INFORMATION)) == 0)
                return false;

            if (mbi is not
                {
                    State: MEM.MEM_COMMIT,
                    Protect: PAGE.PAGE_EXECUTE or PAGE.PAGE_EXECUTE_READ or PAGE.PAGE_EXECUTE_READWRITE
                    or PAGE.PAGE_EXECUTE_WRITECOPY,
                })
                return false;

            var regionSize = (nint) ((mbi.RegionSize + 0xFFFUL) & ~0x1000UL);
            var checkedSize = ((nint) mbi.BaseAddress + regionSize) - p;
            size -= checkedSize;
            p += checkedSize;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidUserspaceMemoryAddress(nint p)
    {
        // https://learn.microsoft.com/en-us/windows-hardware/drivers/gettingstarted/virtual-address-spaces
        // A 64-bit process on 64-bit Windows has a virtual address space within the 128-terabyte range
        // 0x000'00000000 through 0x7FFF'FFFFFFFF.
        return p >= 0x10000 && p <= unchecked((nint) 0x7FFF_FFFFFFFFUL);
    }

    [Flags]
    public enum Result
    {
        ReShade     = 0b001,
        OptiScaler  = 0b010,
        NvPresent   = 0b100
    }
}