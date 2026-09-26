using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FloppyUtils.Ptrs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// Signatures which allow you to (do *this) to mark RVAs in-line
/// in the signature and parse them more easily.
/// </summary>
public readonly record struct RVASig
{
    public readonly string Full;
    public readonly string Text;
    public readonly int Start;
    public readonly int Addr;
    public readonly int End;

    public RVASig(string text)
    {
        Full = text;
        text = text.Replace("|", "");

        var start = text.IndexOf('(');
        var addr = text.IndexOf('*');
        var end = text.IndexOf(')');
        if (start == -1 || addr == -1 || end == -1)
        {
            throw new ArgumentException($"Invalid signature, must be of format \"AA (BB *CC) DD\", but got: {Full}");
        }

        Text = string.Concat(text.AsSpan(0, start), text.AsSpan(start + 1, addr - (start + 1)), text.AsSpan(addr + 1, end - (addr + 1)), text.AsSpan(end + 1));
        Start = start / 3;
        Addr = (addr - 1) / 3;
        End = (end - 2) / 3 + 1;

        if (End - Addr != 4)
        {
            throw new ArgumentException($"Invalid signature, RVA must be sizeof(int) == 4, but got {End - Addr}: {Full}");
        }
    }
}

/// <summary>
/// RVASig, but supporting (*multiple) (*of) (*those).
/// </summary>
public readonly record struct MultiRVASig
{
    public readonly string Full;
    public readonly RVASig[] Sigs;

    public MultiRVASig(string text)
    {
        Full = text;
        text = text.Replace("|", "");

        List<RVASig> sigs = [];

        do
        {
            if (!text.Contains('('))
            {
                break;
            }

            var end = text.IndexOf(')');
            sigs.Add(new(string.Concat(text.AsSpan(0, end + 1), text[(end + 1)..].Replace("(", "").Replace("*", "").Replace(")", ""))));

            text = string.Concat(text[..end].Replace("(", "").Replace("*", ""), text.AsSpan(end + 1));
        } while (true);

        Sigs = [.. sigs];
    }
}

public static unsafe class SigExtensions
{
    // Static extension methods instead of instance methods so that CallerArgumentExpression gives full context.

    /// <summary>
    /// ISigScanner.ScanText parses jumps and calls. This doesn't.
    /// </summary>
    /// <param name="scanner"></param>
    /// <param name="sig"></param>
    /// <returns></returns>
    public static nint ScanTextRaw(this ISigScanner scanner, string sig)
    {
        var sigOrig = sig;
        sig = sig.Replace(" ", string.Empty);
        if (sig.Length % 2 != 0)
        {
            throw new ArgumentException("Signature without whitespaces must be divisible by two.", nameof(sig));
        }

        var found = scanner.ScanAllText(sig, CancellationToken.None).FirstOrDefault();
        if (found != 0)
        {
            return found;
        }

        for (int sub = sig.Length - 2; sub > 0; sub -= 2)
        {
            found = scanner.ScanAllText(sig[..sub], CancellationToken.None).FirstOrDefault();
            if (found == 0)
            {
                continue;
            }

            int offsWanted = sub / 2;
            int i = 0;
            for (int offs = 0; i < sigOrig.Length && offs < offsWanted;)
            {
                var c = sigOrig[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                i += 2;
                offs++;
            }

            if (i <= sigOrig.Length)
            {
                throw new KeyNotFoundException($"Can't find a signature but found [..{sub / 2}] of [{sigOrig[..i]}]{sigOrig[i..]} @ {found.ToDebugString()}");
            }
        }

        throw new KeyNotFoundException($"Can't find a signature of {sig}");
    }

    public static nint Scan(this in RVASig sig, [CallerArgumentExpression(nameof(sig))] string? ctx = null)
        => FService.SigScanner.ScanRVASig(sig, ctx);

    /// <summary>
    /// Resolves an RVA in a given signature relative to the given real code address.
    /// </summary>
    /// <param name="sig"></param>
    /// <param name="code"></param>
    /// <param name="ctxSig"></param>
    /// <param name="ctxCode"></param>
    /// <returns></returns>
    public static nint ResolveRelativeTo(
        this in RVASig sig,
        nint code,
        [CallerArgumentExpression(nameof(sig))] string? ctxSig = null,
        [CallerArgumentExpression(nameof(code))] string? ctxCode = null
    )
        => FService.SigScanner.ResolveRVASig(code, sig, ctxSig: ctxSig, ctxCode: ctxCode);

    /// <summary>
    /// Scans for a given signature and immediately resolves the RVA in it.
    /// </summary>
    /// <param name="scanner"></param>
    /// <param name="sig"></param>
    /// <returns></returns>
    public static nint ScanRVASig(this ISigScanner scanner, in RVASig sig, [CallerArgumentExpression(nameof(sig))] string? ctx = null)
    {
        return scanner.ResolveRVASig(scanner.ScanTextRaw(sig.Text), sig, ctxSig: ctx);
    }

    /// <summary>
    /// Resolves for a given RVA based on the given base address and signature.
    /// </summary>
    /// <param name="scanner"></param>
    /// <param name="code"></param>
    /// <param name="sig"></param>
    /// <returns></returns>
    public static nint ResolveRVASig(
        this ISigScanner scanner,
        nint code,
        in RVASig sig,
        [CallerArgumentExpression(nameof(sig))] string? ctxCode = null,
        [CallerArgumentExpression(nameof(sig))] string? ctxSig = null
    )
    {
        code.SameCode(sig.Text);

        if (!Ptr.For<int>(code + sig.Addr).ToOrig().TryRead(out var rva))
        {
            throw new ArgumentException($"Couldn't read original bytes which would've been at {code.ToDebugString()}");
        }

        var addr = FService.SigScanner.ResolveRelativeAddress(code + sig.End, rva);

        if (ctxSig?.Contains(sig.Full) ?? false)
        {
            ctxSig = "...";
        }

        FService.PluginLog.Debug($"ResolveRVASig({ctxSig}={sig.Text}) -> {ctxCode}=0x{(long) code:X16} + {sig.Start} + {sig.Addr - sig.Start} + {sig.End - sig.Addr} + 0x{rva:X8} -> {addr.ToDebugString()}");

        return addr;
    }

    /// <summary>
    /// Verifies if the code at the given address matches the given signature.
    /// For more info: <see cref="PtrExtensions.SameAddr(nint, nint, string?, string?)"/>
    /// </summary>
    /// <param name="code"></param>
    /// <param name="text"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static nint SameCode(this nint code, string text)
    {
        int len = 0;
        for (int i = 0; i < text.Length - 1;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            i += 2;
            len++;
        }

        Span<byte> bytes = stackalloc byte[len];
        if (!Ptr.For<byte>(code).ToOrig().TryRead(bytes))
        {
            throw new ArgumentException($"Couldn't read original bytes which would've been at {code.ToDebugString()}");
        }

        int offs = 0;
        for (int i = 0; i < text.Length - 1;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c != '?')
            {
                var wanted = byte.Parse(text.Substring(i, 2), NumberStyles.HexNumber);
                var got = bytes[offs];
                if (wanted != got)
                {
                    throw new ArgumentException($"Code does not match sig @ {code.ToDebugString()}: at offs {offs}: wanted {wanted:X2} vs got {got:X2};\nwanted {text.Replace(" ", "")}\ngot    {Convert.ToHexString(bytes)}");
                }
            }

            i += 2;
            offs++;
        }

        return code;
    }

    /// <summary>
    /// Enables the given hook... wait, why isn't this in Dalamud?
    /// Enable is not part of IDalamudHook, but a virtual method in DalamudHook.
    /// </summary>
    /// <param name="hook"></param>
    public static void Enable(this IDalamudHook hook)
    {
        hook.GetType().GetMethod("Enable")!.Invoke(hook, []);
    }

    /// <summary>
    /// Disable the given hook... wait, why isn't this in Dalamud?
    /// Disable is not part of IDalamudHook, but a virtual method in DalamudHook.
    /// </summary>
    /// <param name="hook"></param>
    public static void Disable(this IDalamudHook hook)
    {
        hook.GetType().GetMethod("Disable")!.Invoke(hook, []);
    }
}
