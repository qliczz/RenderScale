using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FloppyUtils.Audio.DriverWASAPI;
using FloppyUtils.Concurrency;
using FloppyUtils.Ptrs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static TerraFX.Interop.Windows.Windows;

namespace FloppyUtils.Audio;

public sealed class AudioState : IAsyncLoadable
{
    private readonly ThreadedHookSet _hooks = new();
    private readonly List<AudioDeviceHooks> _hookedVtbls = [];
    private readonly ThreadedHookProxy<PumpAudioRenderer> _pumpAudioRendererHook;

    private readonly unsafe AudioRenderer** _audioRenderers;
    private readonly unsafe int* _sampleRate;

    private DelayedExecutionThreadShortCircuit _audioThreadSC = new();

    private AudioRendererState[] _audioRendererStates = new AudioRendererState[(int) OutputIndex.Count];

    public unsafe int SampleRate => *_sampleRate;

    public unsafe AudioState()
    {
        FService.Assert(Framework.Instance()->SoundManager == SoundManager.Instance());

        AudioThread = new("Audio", () => _audioThreadSC.CanRun(Thread.CurrentThread) || FService.Framework.IsFrameworkUnloading);

        for (int i = 0; i < _audioRendererStates.Length; i++)
        {
            _audioRendererStates[i] = new(this, (OutputIndex) i);
        }

        var gip = FService.GameInteropProvider;

        FService.Assert(Sigs.CloseAudioRenderers.Sigs.Length == 3);
        Sigs.CloseAudioRenderers.Sigs[2].Scan().SameCode(Sigs.GetAudioRenderer);
        _audioRenderers = (AudioRenderer**) Sigs.CloseAudioRenderers.Sigs[0].Scan().SameAddr(Sigs.CloseAudioRenderers.Sigs[1].Scan());

        // Grab vtables from constructors using known common function start
        _hookedVtbls.Add(new AudioDeviceHooks(this, Vtbl.Get<WASAPIDevice, AudioDeviceVtbl>().Addr));
        _hookedVtbls.Add(new AudioDeviceHooks(this, Vtbl.Get<MockDevice, AudioDeviceVtbl>().Addr));

        FService.Assert(Sigs.GetSampleRateCallSite.Sigs.Length == 4);
        var getSampleRate = Sigs.GetSampleRateCallSite.Sigs[0].Scan().SameCode(Sigs.GetSampleRate.Text);
        getSampleRate.SameAddr(Sigs.GetSampleRateCallSite.Sigs[1].Scan());
        getSampleRate.SameAddr(Sigs.GetSampleRateCallSite.Sigs[2].Scan());
        getSampleRate.SameAddr(Sigs.GetSampleRateCallSite.Sigs[3].Scan());
        _sampleRate = (int*) Sigs.GetSampleRate.ResolveRelativeTo(getSampleRate);
        FService.PluginLog.Debug($"Global audio sample rate: {SampleRate}");

        // TODO: Verify PumpAudioRenderer via call sites
        // FIXME: This hook should be performed on the audio thread, or in an audio locked context!
        _hooks.Add(_pumpAudioRendererHook = gip.HookFromAddress<PumpAudioRenderer>(
            FService.SigScanner.ScanTextRaw(Sigs.PumpAudioRenderer).Log(),
            PumpAudioRendererDetour
        ).On(FService.UpdateThread));
    }

    private unsafe delegate int PumpAudioRenderer(AudioRenderer* renderer, delegate* unmanaged<nint, void> cb, out int counter);

    public DelayedExecutionContext AudioThread { get; }

    public System.Threading.Tasks.Task LoadAsync(CancellationToken cancel)
    {
        return _hooks.EnableAsync();
    }

    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        _audioThreadSC.Dispose();
        await AudioThread;

        await _hooks.DisposeAsync();

        await System.Threading.Tasks.Task.WhenAll(_hookedVtbls.Select(h => h.DisposeAsync().AsTask()));
    }

    /// <summary>
    /// Gets the audio renderer state for a given output index.
    /// </summary>
    /// <remarks>
    /// This audio state will remain valid even if the underlying device changes.
    /// </remarks>
    /// <param name="index"></param>
    /// <returns></returns>
    public AudioRendererState GetRenderer(OutputIndex index)
    {
        return _audioRendererStates[(int) index];
    }

    /// <summary>
    /// Gets the audio renderer state for a given audio output device.
    /// </summary>
    /// <remarks>
    /// This audio state will remain valid even if the underlying device changes.
    /// This only works because each audio renderer opens its own audio device.
    /// </remarks>
    /// <param name="index"></param>
    /// <returns></returns>
    public unsafe AudioRendererState GetRenderer(AudioDevice* native)
    {
        for (int i = 0; i < _audioRendererStates.Length; i++)
        {
            var other = _audioRenderers[i];
            if (other != null && other->Device == native)
            {
                return _audioRendererStates[i];
            }
        }

        throw new KeyNotFoundException("Couldn't find native device in global audio renderer table");
    }

    private unsafe int PumpAudioRendererDetour(AudioRenderer* queue, delegate* unmanaged<nint, void> cb, out int invocs)
    {
        if (!_audioThreadSC.IsReady && !FService.Framework.IsInFrameworkUpdateThread)
        {
            _audioThreadSC.Set(Thread.CurrentThread);
        }

        AudioThread.Pump();

#if DEBUG
        return PumpAudioRendererReimpl(queue, cb, out invocs);
#else
        return _pumpAudioRendererHook.OriginalDisposeSafe(queue, cb, out invocs);
#endif
    }

#if DEBUG
    /// <summary>
    /// Reference reimplementation of <see cref="Sigs.PumpAudioRenderer"/>.
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="cb"></param>
    /// <param name="invocs"></param>
    /// <returns></returns>
    private unsafe int PumpAudioRendererReimpl(AudioRenderer* queue, delegate* unmanaged<nint, void> cb, out int invocs)
    {
        invocs = 0;

        var rv = queue->Device->Vtbl->ClientGetCurrentPadding(queue->Device, out var wanted);
        if (rv < 0 || wanted == 0)
        {
            return rv;
        }

        rv = queue->Device->Vtbl->RenderGetBuffer(queue->Device, wanted, out var target);
        if (rv < 0)
        {
            return rv;
        }

        if (queue->Available < 1 && cb != null)
        {
            cb(0);
            invocs++;
        }

        var bytesPerFrame = queue->Device->Vtbl->GetChannelCount(queue->Device) * sizeof(float);

        var left = wanted;
        while (left != 0 && queue->Available > 0)
        {
            var subOffs = queue->ReadOffs;
            ref var curr = ref queue->Ring[queue->Index];
            var subEnd = curr.Length;

            if (subEnd - subOffs >= bytesPerFrame)
            {
                var copySize = (subEnd - subOffs) / bytesPerFrame;
                var wantedCopy = left;
                if (copySize < left)
                {
                    wantedCopy = (uint) copySize;
                }
                copySize = (int) wantedCopy * bytesPerFrame;

                if (target != null)
                {
                    var from = new Span<float>(curr.Data + subOffs, copySize / sizeof(float));
                    var to = new Span<float>(target, copySize / sizeof(float));
                    from.CopyTo(to);
                    target += copySize;
                }

                left -= wantedCopy;
                subEnd = subOffs + copySize;
            }

            queue->ReadOffs = subEnd;

            if (curr.Length <= subEnd)
            {
                curr.Data = null;
                curr.Length = 0;
                queue->ReadOffs = 0;

                EnterCriticalSection(&queue->Lock);
                queue->Available -= 1;
                queue->Index = (queue->Index + 1) % AudioRenderer.Count;
                LeaveCriticalSection(&queue->Lock);

                if (cb != null)
                {
                    cb(0);
                    invocs++;
                }
            }
        }

        queue->Device->Vtbl->RenderReleaseBuffer(queue->Device, wanted - left, 0);
        return rv;
    }
#endif

    /// <summary>
    /// Audio output indices. On desktop, only two audio outputs are ever present.
    /// </summary>
    public enum OutputIndex
    {
        Unknown = -1,
        Main,
        Controller,
        Count
    }

    public sealed class AudioRendererState
    {
        private readonly AudioState _state;
        private readonly OutputIndex _index;

        private nint _buffer;

        internal AudioRendererState(AudioState audioState, OutputIndex i)
        {
            _state = audioState;
            _index = i;
        }

        public AudioState AudioState => _state;
        public OutputIndex Index => _index;

        public unsafe AudioRenderer* Queue => _state._audioRenderers[(int) _index];
        public unsafe AudioDevice* Device
        {
            get
            {
                var queue = Queue;
                return queue == null ? (AudioDevice*) null : queue->Device;
            }
        }

        public unsafe int SampleRate => Device->Vtbl->GetSampleRate(Device);
        public unsafe int Channels => Device->Vtbl->GetChannelCount(Device);

        public event SubmitHandler? OnSubmit;

        public delegate void SubmitHandler(AudioRendererState state, Span<float> data);

        internal unsafe void OnGetBuffer(byte* value)
        {
            Interlocked.Exchange(ref _buffer, (nint) value);
        }

        internal unsafe void OnReleaseBuffer(uint avail)
        {
            var buffer = Interlocked.Exchange(ref _buffer, 0);
            if (buffer == 0)
            {
                return;
            }

            var data = new Span<float>((void*) buffer, (int) avail * Channels);
            OnSubmit?.Invoke(this, data);
        }
    }

    private sealed class AudioDeviceHooks : IAsyncLoadable
    {
        private readonly AudioState _state;
        private unsafe readonly AudioDeviceVtbl* _vtbl;
        private readonly ThreadedHookSet _hooks = new();

#if DEBUG
        private readonly ThreadedHookProxy<Init> _initHook;
#endif
        private readonly ThreadedHookProxy<RenderGetBuffer> _renderGetBufferHook;
        private readonly ThreadedHookProxy<RenderReleaseBuffer> _renderReleaseBufferHook;

        private bool _disposed = false;

        public unsafe AudioDeviceHooks(AudioState audioState, AudioDeviceVtbl* vtbl)
        {
            _state = audioState;
            _vtbl = vtbl;

            var gip = FService.GameInteropProvider;

#if DEBUG
            _hooks.Add(_initHook = gip.HookFromAddress<Init>(vtbl->Init, InitDetour).On(audioState.AudioThread));
#endif

            _hooks.Add(_renderGetBufferHook = gip.HookFromAddress<RenderGetBuffer>(vtbl->RenderGetBuffer, RenderGetBufferDetour).On(audioState.AudioThread));
            _hooks.Add(_renderReleaseBufferHook = gip.HookFromAddress<RenderReleaseBuffer>(vtbl->RenderReleaseBuffer, RenderReleaseBufferDetour).On(audioState.AudioThread));
        }

#if DEBUG
        private unsafe delegate int Init(AudioDevice* device, AudioRendererDeviceParams* args);
#endif
        private unsafe delegate int RenderGetBuffer(AudioDevice* device, uint count, out byte* buf);
        private unsafe delegate int RenderReleaseBuffer(AudioDevice* device, uint avail, int flags);

        public System.Threading.Tasks.Task LoadAsync(CancellationToken cancel)
        {
            return _hooks.EnableAsync();
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            _disposed = true;
            return _hooks.DisposeAsync();
        }

#if DEBUG
        private unsafe int InitDetour(AudioDevice* device, AudioRendererDeviceParams* args)
        {
            FService.PluginLog.Debug($"Initializing audio device: {((nint) device).ToDebugString()} using vtbl {((nint) device->Vtbl).ToDebugString()}");
            FService.PluginLog.Debug($"args @ {((nint) args).ToDebugString()}:");
            for (int i = 0; i < args->All.Length; i++)
            {
                var arg = args->All[i];
                FService.PluginLog.Debug($"args[{i}] (0x{i * 4:X2}) = {arg} (0x{arg:X8}):");
            }
            return _initHook.OriginalDisposeSafe(device, args);
        }
#endif

        private unsafe int RenderGetBufferDetour(AudioDevice* device, uint count, out byte* buf)
        {
            var rv = _renderGetBufferHook.OriginalDisposeSafe(device, count, out buf);

#if DEBUG
            if (!Vtbl.Get<AudioDevice, AudioDeviceVtbl>().Is(device->Vtbl))
            {
                FService.PluginLog.Warning($"Encountered new audio device vtbl: {((nint) device->Vtbl).ToDebugString()}");
            }
#endif

            if (!_disposed)
            {
                _state.GetRenderer(device).OnGetBuffer(buf);
            }

            return rv;
        }

        private unsafe int RenderReleaseBufferDetour(AudioDevice* device, uint avail, int flags)
        {
            if (!_disposed)
            {
                _state.GetRenderer(device).OnReleaseBuffer(avail);
            }

            return _renderReleaseBufferHook.OriginalDisposeSafe(device, avail, flags);
        }
    }

    private static class Sigs
    {
        /* The global audio renderer table is referred to by the function calling PumpAudioRenderer and
         * by this handle destroying function. It also calls GetAudioRenderer.
         */
        public static readonly MultiRVASig CloseAudioRenderers = new(new GhidraCode(@"
       141d7ffa0 48  83  ec       SUB        RSP ,0x28
                 28
       141d7ffa4 48  8b  0d       MOV        RCX ,qword ptr [DAT_142dccd10 ]
                 ??  ??  ?? 
                 ??
       141d7ffab 48  85  c9       TEST       RCX ,RCX
       141d7ffae 74  0a           JZ         LAB_141d7ffba
       141d7ffb0 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d7ffb3 ba  01  00       MOV        EDX ,0x1
                 00  00
       141d7ffb8 ff  10           CALL       qword ptr [RAX ]
       141d7ffba b9  01  00       MOV        ECX ,0x1
                 00  00
       141d7ffbf e8  ??  ??       CALL       sub_141D80470                                    undefined sub_141D80470()
                 ??  ??
       141d7ffc4 48  8d  0d       LEA        RCX ,[DAT_142dccc90 ]
                 ??  ??  ?? 
                 ??
       141d7ffcb e8  ??  ??       CALL       sub_141D67C80                                    undefined sub_141D67C80()
                 ??  ??
       141d7ffd0 48  8b  0d       MOV        RCX ,qword ptr [DAT_142753d40 ]                   = FFFFFFFFFFFFFFFFh
                 ??  ??  ?? 
                 ??
       141d7ffd7 ff  15  ??       CALL       qword ptr [-> KERNEL32.DLL::CloseHandle ]        = 02715598
                 ??  ??  ??
       141d7ffdd (48  8d  0d       LEA        RCX ,[DAT_142dccd00_AudioGlobalSinks ]
                 *??  ??  ?? 
                 ??)
       141d7ffe4 e8  ??  ??       CALL       ?get_app_type@__scrt_winmain_policy@@SA?AW4_cr   undefined ?get_app_type@__scrt_w
                 ??  ??
       141d7ffe9 85  c0           TEST       EAX ,EAX
       141d7ffeb 7e  47           JLE        LAB_141d80034
       141d7ffed 48  89  5c       MOV        qword ptr [RSP  + local_res8 ],RBX
                 24  30
       141d7fff2 33  db           XOR        EBX ,EBX
       141d7fff4 48  89  7c       MOV        qword ptr [RSP  + local_8 ],RDI
                 24  20
       141d7fff9 8b  f8           MOV        EDI ,EAX
       141d7fffb 0f  1f  44       NOP        dword ptr [RAX  + RAX *0x1 ]
                 00  00
       141d80000 48  8b  d3       MOV        RDX ,RBX
       141d80003 (48  8d  0d       LEA        RCX ,[DAT_142dccd00_AudioGlobalSinks ]
                 *??  ??  ?? 
                 ??)
       141d8000a (e8  *??  ??       CALL       sub_141D80E80_GetAudioSink                       undefined sub_141D80E80_GetAudio
                 ??  ??)
       141d8000f 48  8b  08       MOV        RCX ,qword ptr [RAX ]
       141d80012 48  85  c9       TEST       RCX ,RCX
       141d80015 74  0a           JZ         LAB_141d80021
       141d80017 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d8001a ba  01  00       MOV        EDX ,0x1
                 00  00
       141d8001f ff  10           CALL       qword ptr [RAX ]
       141d80021 48  ff  c3       INC        RBX
       141d80024 48  83  ef       SUB        RDI ,0x1
                 01
       141d80028 75  d6           JNZ        LAB_141d80000
       141d8002a 48  8b  7c       MOV        RDI ,qword ptr [RSP  + local_8 ]
                 24  20
       141d8002f 48  8b  5c       MOV        RBX ,qword ptr [RSP  + local_res8 ]
                 24  30
       141d80034 48  8b  0d       MOV        RCX ,qword ptr [hEvent ]                          = FFFFFFFFFFFFFFFFh
                 ??  ??  ?? 
                 ??
       141d8003b ff  15  ??       CALL       qword ptr [-> KERNEL32.DLL::CloseHandle ]        = 02715598
                 ??  ??  ??
       141d80041 33  c0           XOR        EAX ,EAX
       141d80043 48  83  c4       ADD        RSP ,0x28
                 28
       141d80047 c3              RET
").Sig);

        /* Basically ((void**) GlobalAudioRenderers)[i], used to grab renderer ptrs instead of using it directly.
         * Called by the destroy function, and by the function which calls PumpAudioRenderer.
         */
        public static readonly string GetAudioRenderer = new GhidraCode(@" 
       141d80e80 48  8d  04       LEA        RAX ,[RCX  + RDX *0x8 ]
                 d1
       141d80e84 c3              RET
").Clean;

        /* Some part of a reverb effect function, calling GetSampleRate a lot.
         */
        public static readonly MultiRVASig GetSampleRateCallSite = new(new GhidraCode(@"
       141d73102 (e8 *?? ??        CALL       sub_141D6C1E0_GetAudioSampleRate                 undefined sub_141D6C1E0_GetAudio
                 ?? ??)
       141d73107 41 8b 8e        MOV        ECX,dword ptr [R14 + 0x9f8]
                 f8 09 00 00
       141d7310e f3 0f 10        MOVSS      XMM0,dword ptr [RDI + -0xc4]=>DAT_1423cc2a0      = 3FA00000h
                 87 3c ff                                                                    = 4102B852h
                 ff ff
       141d73116 f3 0f 59 c6     MULSS      XMM0,XMM6
       141d7311a 66 0f 6e c8     MOVD       XMM1,EAX
       141d7311e 0f 5b c9        CVTDQ2PS   XMM1,XMM1
       141d73121 f3 0f 59 c1     MULSS      XMM0,XMM1
       141d73125 f3 0f 2c c0     CVTTSS2SI  EAX,XMM0
       141d73129 2b c8           SUB        ECX,EAX
       141d7312b 49 8b 86        MOV        RAX,qword ptr [R14 + 0xa08]
                 08 0a 00 00
       141d73132 89 0c 06        MOV        dword ptr [RSI + RAX*0x1],ECX
       141d73135 (e8 *?? ??        CALL       sub_141D6C1E0_GetAudioSampleRate                 undefined sub_141D6C1E0_GetAudio
                 ?? ??)
       141d7313a 41 8b 8e        MOV        ECX,dword ptr [R14 + 0x9f8]
                 f8 09 00 00
       141d73141 f3 0f 10 0f     MOVSS      XMM1,dword ptr [RDI]=>DAT_1423cc364              = 3F3D70A4h
       141d73145 f3 0f 59 ce     MULSS      XMM1,XMM6
       141d73149 66 0f 6e c0     MOVD       XMM0,EAX
       141d7314d 0f 5b c0        CVTDQ2PS   XMM0,XMM0
       141d73150 f3 0f 59 c8     MULSS      XMM1,XMM0
       141d73154 f3 0f 2c c1     CVTTSS2SI  EAX,XMM1
       141d73158 2b c8           SUB        ECX,EAX
       141d7315a 49 8b 86        MOV        RAX,qword ptr [R14 + 0xa08]
                 08 0a 00 00
       141d73161 89 4c 06 04     MOV        dword ptr [RSI + RAX*0x1 + 0x4],ECX
       141d73165 (e8 *?? ??        CALL       sub_141D6C1E0_GetAudioSampleRate                 undefined sub_141D6C1E0_GetAudio
                 ?? ??)
       141d7316a 41 8b 8e        MOV        ECX,dword ptr [R14 + 0x9f8]
                 f8 09 00 00
       141d73171 f3 0f 10        MOVSS      XMM1,dword ptr [RDI + -0xc4]=>DAT_1423cc2a0      = 3FA00000h
                 8f 3c ff 
                 ff ff
       141d73179 f3 0f 59 ce     MULSS      XMM1,XMM6
       141d7317d 66 0f 6e c0     MOVD       XMM0,EAX
       141d73181 0f 5b c0        CVTDQ2PS   XMM0,XMM0
       141d73184 f3 0f 59 c8     MULSS      XMM1,XMM0
       141d73188 f3 0f 2c c1     CVTTSS2SI  EAX,XMM1
       141d7318c 2b c8           SUB        ECX,EAX
       141d7318e 49 8b 86        MOV        RAX,qword ptr [R14 + 0xa08]
                 08 0a 00 00
       141d73195 89 4c 06 08     MOV        dword ptr [RSI + RAX*0x1 + 0x8],ECX
       141d73199 (e8 *?? ??        CALL       sub_141D6C1E0_GetAudioSampleRate                 undefined sub_141D6C1E0_GetAudio
                 ?? ??)
       141d7319e f3 0f 10 0f     MOVSS      XMM1,dword ptr [RDI]=>DAT_1423cc364              = 3F3D70A4h
       141d731a2 48 8d 76 10     LEA        RSI,[RSI + 0x10]
       141d731a6 41 8b 8e        MOV        ECX,dword ptr [R14 + 0x9f8]
                 f8 09 00 00
       141d731ad 48 83 c7 04     ADD        RDI,0x4
       141d731b1 f3 0f 59 ce     MULSS      XMM1,XMM6
       141d731b5 66 0f 6e c0     MOVD       XMM0,EAX
       141d731b9 0f 5b c0        CVTDQ2PS   XMM0,XMM0
       141d731bc f3 0f 59 c8     MULSS      XMM1,XMM0
       141d731c0 f3 0f 2c c1     CVTTSS2SI  EAX,XMM1
       141d731c4 2b c8           SUB        ECX,EAX
       141d731c6 49 8b 86        MOV        RAX,qword ptr [R14 + 0xa08]
                 08 0a 00 00
       141d731cd 89 4c 06 fc     MOV        dword ptr [RSI + RAX*0x1 + -0x4],ECX
       141d731d1 48 3b fd        CMP        RDI,RBP
       141d731d4 0f 8c 28        JL         LAB_141d73102
                 ff ff ff
").Sig);

        /* Returns the value of the static sample rate var, which is rarely accessed directly.
         * Called in a LOT of places, including ICoreEffect functions.
         */
        public static readonly RVASig GetSampleRate = new(new GhidraCode(@"
       141d6c1e0 (8b 05 *??        MOV        EAX,dword ptr [DAT_142dcc66c_AudioSampleRate]
                 ?? ?? ??)
       141d6c1e6 c3              RET
").Sig);

        /* Called in two places, at least one of which (didn't check the other one) is
         * called by some function referenced by functions called by the audio init function.
         * Oof.
         */
        public static readonly string PumpAudioRenderer = new GhidraCode(@"
       141d81a40 40  53           PUSH       RBX
       141d81a42 55              PUSH       RBP
       141d81a43 41  54           PUSH       R12
       141d81a45 41  56           PUSH       R14
       141d81a47 48  83  ec       SUB        RSP ,0x38
                 38
       141d81a4b 48  8b  d9       MOV        RBX ,RCX
       141d81a4e 4c  8b  e2       MOV        R12 ,RDX
       141d81a51 48  8b  49       MOV        RCX ,qword ptr [RCX  + 0x28 ]
                 28
       141d81a55 48  8d  54       LEA        RDX => local_res8 ,[RSP  + 0x60 ]
                 24  60
       141d81a5a 4d  8b  f0       MOV        R14 ,R8
       141d81a5d 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81a60 ff  50  38       CALL       qword ptr [RAX  + 0x38 ]
       141d81a63 33  ed           XOR        EBP ,EBP
       141d81a65 41  89  2e       MOV        dword ptr [R14 ],EBP
       141d81a68 85  c0           TEST       EAX ,EAX
       141d81a6a 0f  88  b0       JS         LAB_141d81c20
                 01  00  00
       141d81a70 8b  54  24       MOV        EDX ,dword ptr [RSP  + local_res8 ]
                 60
       141d81a74 85  d2           TEST       EDX ,EDX
       141d81a76 0f  84  a4       JZ         LAB_141d81c20
                 01  00  00
       141d81a7c 48  8b  4b       MOV        RCX ,qword ptr [RBX  + 0x28 ]
                 28
       141d81a80 4c  8d  44       LEA        R8 => local_res20 ,[RSP  + 0x78 ]
                 24  78
       141d81a85 48  89  7c       MOV        qword ptr [RSP  + local_28 ],RDI
                 24  30
       141d81a8a 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81a8d ff  50  20       CALL       qword ptr [RAX  + 0x20 ]
       141d81a90 89  44  24       MOV        dword ptr [RSP  + local_res18 ],EAX
                 70
       141d81a94 8b  f8           MOV        EDI ,EAX
       141d81a96 85  c0           TEST       EAX ,EAX
       141d81a98 0f  88  7d       JS         LAB_141d81c1b
                 01  00  00
       141d81a9e 48  89  74       MOV        qword ptr [RSP  + local_res10 ],RSI
                 24  68
       141d81aa3 8b  74  24       MOV        ESI ,dword ptr [RSP  + local_res8 ]
                 60
       141d81aa7 4c  89  6c       MOV        qword ptr [RSP  + local_30 ],R13
                 24  28
       141d81aac 39  ab  fc       CMP        dword ptr [RBX  + 0xfc ],EBP
                 00  00  00
       141d81ab2 7f  0d           JG         LAB_141d81ac1
       141d81ab4 4d  85  e4       TEST       R12 ,R12
       141d81ab7 74  08           JZ         LAB_141d81ac1
       141d81ab9 33  c9           XOR        ECX ,ECX
       141d81abb 41  ff  d4       CALL       R12
       141d81abe 41  ff  06       INC        dword ptr [R14 ]
       141d81ac1 48  8b  4b       MOV        RCX ,qword ptr [RBX  + 0x28 ]
                 28
       141d81ac5 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81ac8 ff  50  48       CALL       qword ptr [RAX  + 0x48 ]
       141d81acb 44  8b  e8       MOV        R13D ,EAX
       141d81ace 41  c1  e5       SHL        R13D ,0x2
                 02
       141d81ad2 85  f6           TEST       ESI ,ESI
       141d81ad4 0f  84  22       JZ         LAB_141d81bfc
                 01  00  00
       141d81ada 4c  89  7c       MOV        qword ptr [RSP  + local_38 ],R15
                 24  20
       141d81adf 90              NOP
       141d81ae0 83  bb  fc       CMP        dword ptr [RBX  + 0xfc ],0x0
                 00  00  00 
                 00
       141d81ae7 0f  8e  06       JLE        LAB_141d81bf3
                 01  00  00
       141d81aed 4c  63  83       MOVSXD     R8 ,dword ptr [RBX  + 0x100 ]
                 00  01  00 
                 00
       141d81af4 4c  63  8b       MOVSXD     R9 ,dword ptr [RBX  + 0xf8 ]
                 f8  00  00 
                 00
       141d81afb 4d  8b  d0       MOV        R10 ,R8
       141d81afe 49  8d  40       LEA        RAX ,[R8  + 0x4 ]
                 04
       141d81b02 48  03  c0       ADD        RAX ,RAX
       141d81b05 8b  0c  c3       MOV        ECX ,dword ptr [RBX  + RAX *0x8 ]
       141d81b08 8b  c1           MOV        EAX ,ECX
       141d81b0a 41  2b  c1       SUB        EAX ,R9D
       141d81b0d 41  3b  c5       CMP        EAX ,R13D
       141d81b10 7c  4f           JL         LAB_141d81b61
       141d81b12 48  8b  4c       MOV        RCX ,qword ptr [RSP  + local_res20 ]
                 24  78
       141d81b17 99              CDQ
       141d81b18 41  f7  fd       IDIV       R13D
       141d81b1b 44  8b  fe       MOV        R15D ,ESI
       141d81b1e 3b  f0           CMP        ESI ,EAX
       141d81b20 44  0f  47       CMOVA      R15D ,EAX
                 f8
       141d81b24 41  8b  ef       MOV        EBP ,R15D
       141d81b27 41  0f  af       IMUL       EBP ,R13D
                 ed
       141d81b2b 48  85  c9       TEST       RCX ,RCX
       141d81b2e 74  28           JZ         LAB_141d81b58
       141d81b30 4d  03  d2       ADD        R10 ,R10
       141d81b33 44  8b  c5       MOV        R8D ,EBP
       141d81b36 49  8b  d1       MOV        RDX ,R9
       141d81b39 8b  fd           MOV        EDI ,EBP
       141d81b3b 4a  03  54       ADD        RDX ,qword ptr [RBX  + R10 *0x8  + 0x38 ]
                 d3  38
       141d81b40 e8  ??  ??       CALL       MemCpy                                           undefined MemCpy()
                 ??  ??
       141d81b45 48  01  7c       ADD        qword ptr [RSP  + local_res20 ],RDI
                 24  78
       141d81b4a 44  8b  8b       MOV        R9D ,dword ptr [RBX  + 0xf8 ]
                 f8  00  00 
                 00
       141d81b51 44  8b  83       MOV        R8D ,dword ptr [RBX  + 0x100 ]
                 00  01  00 
                 00
       141d81b58 41  2b  f7       SUB        ESI ,R15D
       141d81b5b 41  8d  0c       LEA        ECX ,[R9  + RBP *0x1 ]
                 29
       141d81b5f 33  ed           XOR        EBP ,EBP
       141d81b61 49  63  d0       MOVSXD     RDX ,R8D
       141d81b64 89  8b  f8       MOV        dword ptr [RBX  + 0xf8 ],ECX
                 00  00  00
       141d81b6a 48  8d  42       LEA        RAX ,[RDX  + 0x4 ]
                 04
       141d81b6e 48  03  c0       ADD        RAX ,RAX
       141d81b71 3b  0c  c3       CMP        ECX ,dword ptr [RBX  + RAX *0x8 ]
       141d81b74 7c  75           JL         LAB_141d81beb
       141d81b76 48  03  d2       ADD        RDX ,RDX
       141d81b79 48  8d  8b       LEA        RCX ,[RBX  + 0x108 ]
                 08  01  00 
                 00
       141d81b80 48  89  6c       MOV        qword ptr [RBX  + RDX *0x8  + 0x38 ],RBP
                 d3  38
       141d81b85 48  63  83       MOVSXD     RAX ,dword ptr [RBX  + 0x100 ]
                 00  01  00 
                 00
       141d81b8c 48  83  c0       ADD        RAX ,0x4
                 04
       141d81b90 48  03  c0       ADD        RAX ,RAX
       141d81b93 89  2c  c3       MOV        dword ptr [RBX  + RAX *0x8 ],EBP
       141d81b96 89  ab  f8       MOV        dword ptr [RBX  + 0xf8 ],EBP
                 00  00  00
       141d81b9c e8  ??  ??       CALL       EnterCriticalSection_0                           void EnterCriticalSection_0(LPCR
                 ??  ??
       141d81ba1 44  8b  83       MOV        R8D ,dword ptr [RBX  + 0x100 ]
                 00  01  00 
                 00
       141d81ba8 48  8d  8b       LEA        RCX ,[RBX  + 0x108 ]
                 08  01  00 
                 00
       141d81baf ff  8b  fc       DEC        dword ptr [RBX  + 0xfc ]
                 00  00  00
       141d81bb5 41  ff  c0       INC        R8D
       141d81bb8 b8  ab  aa       MOV        EAX ,0x2aaaaaab
                 aa  2a
       141d81bbd 41  f7  e8       IMUL       R8D
       141d81bc0 d1  fa           SAR        EDX ,0x1
       141d81bc2 8b  c2           MOV        EAX ,EDX
       141d81bc4 c1  e8  1f       SHR        EAX ,0x1f
       141d81bc7 03  d0           ADD        EDX ,EAX
       141d81bc9 8d  04  52       LEA        EAX ,[RDX  + RDX *0x2 ]
       141d81bcc c1  e0  02       SHL        EAX ,0x2
       141d81bcf 44  2b  c0       SUB        R8D ,EAX
       141d81bd2 44  89  83       MOV        dword ptr [RBX  + 0x100 ],R8D
                 00  01  00 
                 00
       141d81bd9 e8  ??  ??       CALL       LeaveCriticalSection_0                           void LeaveCriticalSection_0(LPCR
                 ??  ??
       141d81bde 4d  85  e4       TEST       R12 ,R12
       141d81be1 74  08           JZ         LAB_141d81beb
       141d81be3 33  c9           XOR        ECX ,ECX
       141d81be5 41  ff  d4       CALL       R12
       141d81be8 41  ff  06       INC        dword ptr [R14 ]
       141d81beb 85  f6           TEST       ESI ,ESI
       141d81bed 0f  85  ed       JNZ        LAB_141d81ae0
                 fe  ff  ff
       141d81bf3 8b  7c  24       MOV        EDI ,dword ptr [RSP  + local_res18 ]
                 70
       141d81bf7 4c  8b  7c       MOV        R15 ,qword ptr [RSP  + local_38 ]
                 24  20
       141d81bfc 48  8b  4b       MOV        RCX ,qword ptr [RBX  + 0x28 ]
                 28
       141d81c00 45  33  c0       XOR        R8D ,R8D
       141d81c03 8b  54  24       MOV        EDX ,dword ptr [RSP  + local_res8 ]
                 60
       141d81c07 2b  d6           SUB        EDX ,ESI
       141d81c09 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81c0c ff  50  28       CALL       qword ptr [RAX  + 0x28 ]
       141d81c0f 4c  8b  6c       MOV        R13 ,qword ptr [RSP  + local_30 ]
                 24  28
       141d81c14 8b  c7           MOV        EAX ,EDI
       141d81c16 48  8b  74       MOV        RSI ,qword ptr [RSP  + local_res10 ]
                 24  68
       141d81c1b 48  8b  7c       MOV        RDI ,qword ptr [RSP  + local_28 ]
                 24  30
       141d81c20 48  83  c4       ADD        RSP ,0x38
                 38
       141d81c24 41  5e           POP        R14
       141d81c26 41  5c           POP        R12
       141d81c28 5d              POP        RBP
       141d81c29 5b              POP        RBX
       141d81c2a c3              RET
").Clean;
    }
}
