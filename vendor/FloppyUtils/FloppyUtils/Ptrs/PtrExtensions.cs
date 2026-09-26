using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace FloppyUtils.Ptrs;

public static unsafe class PtrExtensions
{
    /// <summary>
    /// Verifies if two addresses are equal, throwing an exception on failure,
    /// and allowing for easy inline chaining when calling functions taking addresses.
    /// </summary>
    /// <param name="addr"></param>
    /// <param name="other"></param>
    /// <param name="addrCtx"></param>
    /// <param name="otherCtx"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static nint SameAddr(
        this nint addr,
        nint other,
        [CallerArgumentExpression(nameof(addr))] string? addrCtx = null,
        [CallerArgumentExpression(nameof(other))] string? otherCtx = null
    )
    {
        if (addr != other)
        {
            throw new ArgumentException($"Addresses do not match: {addr.ToDebugString()} vs {other.ToDebugString()} | {addrCtx} vs {otherCtx} | off by: {addr - other} (0x{addr - other:X})");
        }

        return addr;
    }

    /// <summary>
    /// Verifies that the given address is not null.
    /// </summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public static nint NotNull(this nint addr)
    {
        if (addr == 0)
        {
            throw new NullReferenceException();
        }

        return addr;
    }

    /// <summary>
    /// Logs the given address with context.
    /// </summary>
    /// <param name="addr"></param>
    /// <param name="ctx"></param>
    /// <returns></returns>
    public static nint Log(this nint addr, [CallerArgumentExpression(nameof(addr))] string? ctx = null)
    {
        FService.PluginLog.Debug(addr.ToDebugCtxString(ctx));
        return addr;
    }

    /// <summary>
    /// Gets a debug string for the given address.
    /// </summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    public static string ToDebugString(this nint addr)
    {
        var sb = new StringBuilder();
        var walked = new HashSet<nint>();

        Start:
        if (!walked.Add(addr))
        {
            sb.Append("!loop");
            return sb.ToString();
        }

        if (Ptr.For<nint>(addr).ToOrig() is { IsValid: true } orig)
        {
            sb.Append(orig);
        }
        else
        {
            sb.Append("0x").Append(((long) addr).ToString("X16"));
        }

        if (Vtbl.Find(addr, out var ctx) is not null)
        {
            sb.Append('<').Append(ctx).Append('>');
        }

        if (Ptr.For<nint>(addr).TryRead(out var value))
        {
            sb.Append('=');
            addr = value;
            goto Start;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets a debug string for the given address, including context.
    /// </summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    public static string ToDebugCtxString(this nint addr, [CallerArgumentExpression(nameof(addr))] string? ctx = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(ctx);
        return $"{ctx} -> {addr.ToDebugString()}";
    }
}
