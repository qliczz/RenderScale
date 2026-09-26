using Dalamud.Hooking;
using FloppyUtils.Ptrs;
using Iced.Intel;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Tools;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Iced.Intel.AssemblerRegisters;

namespace FloppyUtils;

/// <summary>
/// Extremely unsafe and possibly slow mid-func callback capturing all registers.
/// </summary>
public sealed unsafe class MidFuncCallback : IDalamudHook
{
    private static readonly LifetimeHelper.Manager _lifetimeManager = new();
    private static readonly int _reg64Size = sizeof(nint);
    private static readonly int _regXMMSize = sizeof(int) * 4;

    private readonly LifetimeHelper _lifetime = _lifetimeManager.Create();
    private readonly Action<Registers> _callback;
    private readonly int _stackDepth;
    private readonly int _stackAlign;
    private readonly bool _hasXMM;
    private readonly AsmHook _hook;

    public MidFuncCallback(
        nint startAddress,
        Span<byte> orig,
        int byteOffs,
        Action<Registers> callback,
        Behaviour behavior = Behaviour.ExecuteFirst,
        [CallerArgumentExpression(nameof(callback))] string? ctx = null
    )
    {
        _callback = callback;

        ParseOrig(
            startAddress,
            orig,
            byteOffs,
            behavior,
            out var address,
            out _stackDepth,
            out _hasXMM
        );

        var argHandle = rcx;
        var argStack = rdx;
        var asm = new Assembler(nint.Size * 8);

        // No matter if x64 regs (8), XMM regs (16), or the call (16):
        // Aligning once early keeps things aligned later anyway.
        _stackAlign = (16 + (_stackDepth % 16)) % 16;
        if (_stackAlign != 0)
        {
            asm.sub(rsp, _stackAlign);
        }

        // Is taking a snapshot of every register slow and brutally extreme?
        // Yup, but eh, the flexibility is worth it!
        asm.sub(rsp, (1 + Register.R15 - Register.RAX) * _reg64Size);
        for (Register r = Register.R15; r >= Register.RAX; --r)
        {
            asm.mov(rsp + (r - Register.RAX) * _reg64Size, new AssemblerRegister64(r));
        }

        if (_hasXMM)
        {
            asm.sub(rsp, (1 + Register.XMM15 - Register.XMM0) * _regXMMSize);
            for (Register r = Register.XMM15; r >= Register.XMM0; --r)
            {
                asm.movd(rsp + (r - Register.XMM0) * _regXMMSize, new AssemblerRegisterXMM(r));
            }
        }

        asm.mov(argHandle, _lifetime.Handle);
        asm.mov(argStack, rsp);
        asm.mov(rax, _lifetime.Set(_DetourTarget, this).Value);

        asm.sub(rsp, 32);
        asm.call(rax);
        asm.add(rsp, 32);

        if (_hasXMM)
        {
            for (Register r = Register.XMM15; r >= Register.XMM0; --r)
            {
                asm.movd(new AssemblerRegisterXMM(r), rsp + (r - Register.XMM0) * _regXMMSize);
            }
            asm.add(rsp, (1 + Register.XMM15 - Register.XMM0) * _regXMMSize);
        }

        for (Register r = Register.R15; r >= Register.RAX; --r)
        {
            asm.mov(new AssemblerRegister64(r), rsp + (r - Register.RAX) * _reg64Size);
        }
        asm.add(rsp, (1 + Register.R15 - Register.RAX) * _reg64Size);

        if (_stackAlign != 0)
        {
            asm.add(rsp, _stackAlign);
        }

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            var writer = new StreamCodeWriter(stream);
            asm.Assemble(writer, 0);
            bytes = stream.ToArray();
        }

        _hook = new(
            address,
            bytes,
            $"{callback.GetType()} {ctx}",
            behavior == Behaviour.ExecuteAfter ? AsmHookBehaviour.ExecuteAfter : AsmHookBehaviour.ExecuteFirst
        );
        FService.PluginLog.Info($"MidFuncCallback({address.ToDebugString()} -> {ctx}) stackDepth {_stackDepth} stackAlign {_stackAlign} hasXMM {_hasXMM} handle {_lifetime.Handle.ToDebugString()}");
    }

    public MidFuncCallback(
        nint startAddress,
        int byteOffs,
        Action<Registers> callback,
        Behaviour behavior = Behaviour.ExecuteFirst,
        [CallerArgumentExpression(nameof(callback))] string? ctx = null
    ) : this(startAddress, ReadFor(startAddress, byteOffs), byteOffs, callback, behavior, ctx)
    {
    }

    public nint Address => _hook.Address;

    public bool IsEnabled => _hook.IsEnabled;

    public bool IsDisposed => _hook.IsDisposed;

    public string BackendName => _hook.BackendName;

    public void Dispose()
    {
        _hook.Dispose();
        _lifetime.Dispose();
    }

    public void Enable()
    {
        _hook.Enable();
    }

    public void Disable()
    {
        _hook.Disable();
    }

    public static bool TryDiffOrigStart(
        nint startAddress,
        [NotNullWhen(true)] out byte[]? orig,
        out int offs,
        int scanSize = 64
    )
    {
        var addr = Ptr.For<byte>(startAddress);
        var mem = new byte[scanSize];
        orig = new byte[scanSize];

        if (!addr.TryRead(mem) || !addr.ToOrig().TryRead(orig))
        {
            offs = 0;
            orig = null;
            return false;
        }

        var dbg = startAddress.ToDebugString();
        FService.PluginLog.Debug($"TryDiffOrig {dbg} in mem: {Convert.ToHexString(mem)}");
        FService.PluginLog.Debug($"TryDiffOrig {dbg} in DLL: {Convert.ToHexString(orig)}");

        offs = 0;
        for (; offs < scanSize && mem[offs] != orig[offs]; offs++)
        {
        }

        return true;
    }

    private static void ParseOrig(
        nint startAddress,
        Span<byte> orig,
        int hookByteScan,
        Behaviour behavior,
        out nint address,
        out int stackDepth,
        out bool hasXMM
    )
    {
        // TODO: Figure out how big the hook is going to be more reliably than this...
        var options = new AsmHookOptions();
        if (options.hookLength == -1)
        {
            options.hookLength = Utilities.GetHookLength((nuint) startAddress, options.MaxOpcodeSize, nint.Size == 8);
        }

        if (behavior == Behaviour.HookAfter)
        {
            // FIXME: This assumes all hooks are as small as Reloaded hooks!
            // TryDiffOrigStart would be amazing... if Reloaded's activate patch was revertible.
            // Alternatively, parse the existing hook in memory, and apply any changes after it.
            hookByteScan += options.hookLength;
        }

        // TODO: Follow jumps (assuming both branches follow same stack alignment rules), add length guardrails!
        var decoder = Decoder.Create(nint.Size * 8, orig.ToArray(), (ulong) startAddress);
        int hookByteOffs = 0;

        // Windows x64 funcs start with return ptr pushed on stack by call itself.
        stackDepth = -8;
        hasXMM = false;

        int stackByteOffs = 0;
        int stackByteScan = hookByteScan;

        if (behavior == Behaviour.ExecuteAfter)
        {
            stackByteScan += options.hookLength;
        }

        int offs = 0;
        int scan = Math.Max(hookByteScan, stackByteScan);

        if (scan != 0)
        {
            foreach (var instr in decoder)
            {
                if (hookByteOffs < hookByteScan)
                {
                    hookByteOffs += instr.Length;
                }

                if (stackByteOffs < stackByteScan)
                {
                    stackByteOffs += instr.Length;

                    switch (instr)
                    {
                        case { OpCount: 2, Op0Kind: OpKind.Register, Op0Register: Register.RSP }:
                            switch (instr.Mnemonic)
                            {
                                case Mnemonic.Sub:
                                    stackDepth -= (int) instr.GetImmediate(1);
                                    break;
                                case Mnemonic.Add:
                                    stackDepth += (int) instr.GetImmediate(1);
                                    break;
                                default:
                                    throw new NotSupportedException($"Cannot parse instr: {instr}");
                            }
                            break;
                        default:
                            stackDepth += instr.StackPointerIncrement;
                            break;
                    }
                }

                for (int op = 0; op < instr.OpCount && !hasXMM; op++)
                {
                    hasXMM |=
                        instr.GetOpKind(op) == OpKind.Register &&
                        instr.GetOpRegister(op) is { } reg &&
                        Register.XMM0 <= reg && reg <= Register.XMM15;
                }

                offs += instr.Length;
                if (offs >= scan)
                {
                    break;
                }
            }
        }

        address = startAddress + hookByteOffs;
    }

    private static Span<byte> ReadFor(nint address, int offs, int scanSize = 64)
    {
        var mem = new byte[offs + scanSize];
        if (!Ptr.For<byte>(address).ToOrig().TryRead(mem) && !Ptr.For<byte>(address).TryRead(mem))
        {
            throw new Exception($"Couldn't read {address.ToDebugString()}");
        }

        return mem;
    }

    private delegate void Detour(nint handle, void* stack);
    private static readonly Detour _DetourTarget = DetourTarget;
    private static void DetourTarget(nint handle, void* stack)
    {
        if (!_lifetimeManager.TryGet(handle, out var lifetime) || !lifetime.TryGet(_DetourTarget, out MidFuncCallback handler))
        {
            return;
        }

        var regs = new Registers(stack, handler);

        regs.X64[Register.RSP] += handler._stackAlign + regs.X64.Length * _reg64Size;

        handler._callback(regs);

        regs.X64[Register.RSP] -= handler._stackAlign + regs.X64.Length * _reg64Size;
    }

    public readonly ref struct Registers(void* ptr, MidFuncCallback handler)
    {
        private readonly MidFuncCallback _handler = handler;

        public void* Ptr { get; } = ptr;

        public readonly nint RIP => _handler.Address;

        public readonly RegRange<nint> X64 => new(
            (void*) (
                (nint) Ptr -
                XMM.Length * _regXMMSize
            ),
            Register.RAX,
            Register.R15
        );

        public readonly RegRange<Vector4> XMM => !_handler._hasXMM ? default : new(
            (void*) (
                (nint) Ptr
            ),
            Register.XMM0,
            Register.XMM15
        );

        private static readonly Register[] _shadow = [Register.RCX, Register.RDX, Register.R8, Register.R9];
        public readonly RegShadow<nint> Shadow => new(
            (void*) (
                X64[Register.RSP] - _handler._stackDepth
            ),
            _shadow
        );

        public readonly ref struct RegRange<T>(void* ptr, Register first, Register last) where T : unmanaged
        {
            public void* Ptr { get; } = ptr;
            public Register First { get; } = first;
            public Register Last { get; } = last;
            public int Length { get; } = 1 + (last - first);

            public Span<T> Span => new(Ptr, Length);

            public ref T this[int index]
            {
                get => ref Span[index];
            }

            public ref T this[Register index]
            {
                get => ref Span[index - First];
            }
        }

        public readonly ref struct RegShadow<T>(void* ptr, Span<Register> regs) where T : unmanaged
        {
            public void* Ptr { get; } = ptr;
            public Span<Register> Regs { get; } = regs;
            public int Length { get; } = regs.Length;

            public Span<T> Span => new(Ptr, Length);

            public ref T this[int index]
            {
                get => ref Span[index];
            }

            public ref T this[Register index]
            {
                get => ref Span[Regs.IndexOf(index)];
            }
        }
    }

    public enum Behaviour
    {
        /// <inheritdoc cref="AsmHookBehaviour.ExecuteFirst"/>
        ExecuteFirst = 0,

        /// <inheritdoc cref="AsmHookBehaviour.ExecuteAfter"/>
        ExecuteAfter = 1,

        /// <summary>
        /// <see cref="ExecuteAfter"/> but after any existing jumps and hooks.
        /// </summary>
        HookAfter = 2,
    }
}
