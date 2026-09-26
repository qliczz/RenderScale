using FloppyUtils.Ptrs;
using System;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;

namespace FloppyUtils.Audio.DriverWASAPI;

internal static class DriverWASAPIVtbls
{
    static DriverWASAPIVtbls()
    {
        // Grab the two ctor funcs from CreateDeviceForRenderer - they're similar to each other, hence going through unique callsite
        FService.Assert(Sigs.CreateDeviceForRenderer.Sigs.Length == 2);
        var deviceWASAPICtor = Sigs.CreateDeviceForRenderer.Sigs[0].Scan();
        var deviceMockCtor = Sigs.CreateDeviceForRenderer.Sigs[1].Scan();

        // Verify both constructors call the same base ctor func
        FService.Assert(Sigs.AudioDeviceCtorCommon.Sigs.Length == 2);
        Sigs.AudioDeviceCtorCommon.Sigs[0].ResolveRelativeTo(deviceWASAPICtor).SameAddr(Sigs.AudioDeviceCtorCommon.Sigs[0].ResolveRelativeTo(deviceMockCtor));

        // Grab vtables from constructors using known common function start
        Vtbl.Setup<WASAPIDevice, AudioDeviceVtbl>(Sigs.AudioDeviceCtorCommon.Sigs[1].ResolveRelativeTo(deviceWASAPICtor))
            .AddBase<AudioDevice>();

        Vtbl.Setup<MockDevice, AudioDeviceVtbl>(Sigs.AudioDeviceCtorCommon.Sigs[1].ResolveRelativeTo(deviceMockCtor))
            .AddBase<AudioDevice>();
    }

    private static class Sigs
    {
        /* Called in two places, one of which in a loop with GetAudioRenderer.
         * Creates and initializes an audio device (destroying any existing one),
         * first attempting WASAPI, then falling back to mock.
         */
        public static readonly MultiRVASig CreateDeviceForRenderer = new(new GhidraCode(@"
       141d812f0 48  89  5c       MOV        qword ptr [RSP  + local_res8 ],RBX
                 24  08
       141d812f5 48  89  74       MOV        qword ptr [RSP  + local_res10 ],RSI
                 24  10
       141d812fa 57              PUSH       RDI
       141d812fb 48  83  ec       SUB        RSP ,0x20
                 20
       141d812ff 48  8b  d9       MOV        RBX ,RCX
       141d81302 e8  ??  ??       CALL       sub_141D81E00                                    undefined sub_141D81E00()
                 ??  ??
       141d81307 48  8b  4b       MOV        RCX ,qword ptr [RBX  + 0x28 ]
                 28
       141d8130b 33  ff           XOR        EDI ,EDI
       141d8130d 48  85  c9       TEST       RCX ,RCX
       141d81310 74  0c           JZ         LAB_141d8131e
       141d81312 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81315 8d  57  01       LEA        EDX ,[RDI  + 0x1 ]
       141d81318 ff  10           CALL       qword ptr [RAX ]
       141d8131a 48  89  7b       MOV        qword ptr [RBX  + 0x28 ],RDI
                 28
       141d8131e b9  28  01       MOV        ECX ,0x28 // 7.4 0x28 -> 0x128
                 00  00
       141d81323 e8  ??  ??       CALL       sub_141D6EDD0                                    undefined sub_141D6EDD0()
                 ??  ??
       141d81328 48  85  c0       TEST       RAX ,RAX
       141d8132b 74  0d           JZ         LAB_141d8133a
       141d8132d 48  8b  c8       MOV        RCX ,RAX
       141d81330 (e8  *??  ??       CALL       sub_141D80D30_AudioSinkRealCtor                  undefined sub_141D80D30_AudioSin
                 ??  ??)
       141d81335 48  8b  c8       MOV        RCX ,RAX
       141d81338 eb  03           JMP        LAB_141d8133d
       141d8133a 48  8b  cf       MOV        RCX ,RDI
       141d8133d 48  89  4b       MOV        qword ptr [RBX  + 0x28 ],RCX
                 28
       141d81341 48  8d  53       LEA        RDX ,[RBX  + 0x8 ]
                 08
       141d81345 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d81348 ff  50  08       CALL       qword ptr [RAX  + 0x8 ]
       141d8134b 85  c0           TEST       EAX ,EAX
       141d8134d 79  3e           JNS        LAB_141d8138d
       141d8134f 48  8b  4b       MOV        RCX ,qword ptr [RBX  + 0x28 ]
                 28
       141d81353 48  85  c9       TEST       RCX ,RCX
       141d81356 74  0a           JZ         LAB_141d81362
       141d81358 48  8b  01       MOV        RAX ,qword ptr [RCX ]
       141d8135b ba  01  00       MOV        EDX ,0x1
                 00  00
       141d81360 ff  10           CALL       qword ptr [RAX ]
       141d81362 b9  30  00       MOV        ECX ,0x30
                 00  00
       141d81367 e8  ??  ??       CALL       sub_141D6EDD0                                    undefined sub_141D6EDD0()
                 ??  ??
       141d8136c 48  85  c0       TEST       RAX ,RAX
       141d8136f 74  0b           JZ         LAB_141d8137c
       141d81371 48  8b  c8       MOV        RCX ,RAX
       141d81374 (e8  *??  ??       CALL       sub_141D80CD0_AudioSinkDummyCtor                 undefined sub_141D80CD0_AudioSin
                 ??  ??)
       141d81379 48  8b  f8       MOV        RDI ,RAX
       141d8137c 48  89  7b       MOV        qword ptr [RBX  + 0x28 ],RDI
                 28
       141d81380 48  8d  53       LEA        RDX ,[RBX  + 0x8 ]
                 08
       141d81384 48  8b  07       MOV        RAX ,qword ptr [RDI ]
       141d81387 48  8b  cf       MOV        RCX ,RDI
       141d8138a ff  50  08       CALL       qword ptr [RAX  + 0x8 ]
       141d8138d 48  8b  5c       MOV        RBX ,qword ptr [RSP  + local_res8 ]
                 24  30
       141d81392 48  8b  74       MOV        RSI ,qword ptr [RSP  + local_res10 ]
                 24  38
       141d81397 48  83  c4       ADD        RSP ,0x20
                 20
       141d8139b 5f              POP        RDI
       141d8139c c3              RET
").Sig);

        /* Common code for both device ctors, both calling the base ctor and
         * then setting up their respective proper vtbls.
         */
        public static readonly MultiRVASig AudioDeviceCtorCommon = new(new GhidraCode(@"
       141d80cd0 40  53           PUSH       RBX
       141d80cd2 48  83  ec       SUB        RSP ,0x20
                 20
       141d80cd6 48  8b  d9       MOV        RBX ,RCX
       141d80cd9 (e8  *??  ??       CALL       sub_141D80B20                                    undefined sub_141D80B20()
                 ??  ??)
       141d80cde (48  8d  05       LEA        RAX ,[PTR_sub_141D80FC0_1423cd118_vtable_AudioS  = 141d80fc0
                 *??  ??  ?? 
                 ??)
").Sig);
    }
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct AudioRenderer
{
    public const int Count = 12;

    [FieldOffset(0x8)]
    public AudioRendererDeviceParams x8;

    [FieldOffset(0x28)]
    public AudioDevice* Device;

    // Data is 0x38 + i * 0x10
    // Length is 0x40 + i * 0x10
    public Span<Buffer> Ring => Ptr.For(ref this).Offs<Buffer>(0x38).Span(Count);

    [FieldOffset(0xF8)]
    public int ReadOffs;

    [FieldOffset(0xFC)]
    public int Available;

    [FieldOffset(0x100)]
    public int Index;

    [FieldOffset(0x108)]
    public CRITICAL_SECTION Lock;

    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    public struct Buffer
    {
        [FieldOffset(0x0)]
        public byte* Data;

        [FieldOffset(0x8)]
        public int Length;
    }
}

// TODO: Map out further. Might not be part of Sd::Driver::WASAPI
[StructLayout(LayoutKind.Explicit, Size = 0x20)]
public unsafe struct AudioRendererDeviceParams
{
    public Span<int> All => Ptr.For(ref this).Offs<int>(0).Span(sizeof(AudioRendererDeviceParams) / sizeof(int));

    // Seen values: 48000
    [FieldOffset(0x0)]
    public uint SampleRate;

    // Seen values: 256
    // WASAPI init copies this into its own x24
    // Mock init copies this to its own xC
    [FieldOffset(0x4)]
    public uint x4;

    // Seen values: 8
    [FieldOffset(0x8)]
    public uint Channels;

    // Seen values: 15
    [FieldOffset(0xC)]
    public uint xC;

    // Seen values: -1
    [FieldOffset(0x10)]
    public uint x10;

    // Seen values: -1
    [FieldOffset(0x14)]
    public uint x14;

    // Seen values: 2 (main), 0x0801 (controller)
    // WASAPI init checks == 0x801 for a sizeable chunk that checks for "Wireless Controller" or "DUALSHOCK", and then later == 2 for AUDCLNT_STREAMFLAGS_EVENTCALLBACK?
    // Mock init checks != 0x801 and then later == 2
    [FieldOffset(0x18)]
    public uint x18;

    // Seen values: 0
    // Not sure if this still belongs to this, or if there's anything else between this and AudioDevice* Device
    // Might just be padding though.
    [FieldOffset(0x1C)]
    public uint x1C;
}

[VtblSetup(typeof(DriverWASAPIVtbls))]
[StructLayout(LayoutKind.Explicit)]
public unsafe struct AudioDevice
{
    [FieldOffset(0x0)]
    public AudioDeviceVtbl* Vtbl;
}

[VtblSetup(typeof(DriverWASAPIVtbls))]
[StructLayout(LayoutKind.Explicit)]
public unsafe struct WASAPIDevice
{
    [FieldOffset(0x0)]
    public AudioDevice _;

    [FieldOffset(0x8)]
    public WAVEFORMATEX* Format;

    [FieldOffset(0x10)]
    public IAudioClient* Client;

    [FieldOffset(0x18)]
    public IAudioRenderClient* RenderClient;

    [FieldOffset(0x20)]
    public int BufferSize;

    [FieldOffset(0x24)]
    public int x24;
}

[VtblSetup(typeof(DriverWASAPIVtbls))]
[StructLayout(LayoutKind.Explicit)]
public struct MockDevice
{
    [FieldOffset(0x0)]
    public AudioDevice _;

    // Set to Queue->x8.x0 on init
    [FieldOffset(0x8)]
    public uint SampleRate;

    // Set to Queue->x8.x4 on init
    [FieldOffset(0xC)]
    public uint xC;

    // Set to 0 on init
    [FieldOffset(0x10)]
    public float CurrentPaddingStart;

    // Set to (float) Queue->x8.x0 on init
    [FieldOffset(0x14)]
    public float CurrentPaddingEnd;

    // Defaults to 4, but if Queue->x8.18 != 0x801, set to Queue->x8.x8 on init
    [FieldOffset(0x18)]
    public uint Channels;

    [FieldOffset(0x20)]
    public SRWLOCK Lock;

    [FieldOffset(0x28)]
    [MarshalAs(UnmanagedType.U1)]
    public bool IsPlaying;

    // Set to Queue->x8.x18 == 2
    [FieldOffset(0x29)]
    [MarshalAs(UnmanagedType.U1)]
    public bool x29;
}

[VtblSetup(typeof(DriverWASAPIVtbls))]
[StructLayout(LayoutKind.Explicit)]
public unsafe struct AudioDeviceVtbl
{
    [FieldOffset(0x0)]
    public delegate* unmanaged<AudioDevice*, void> Dtor;

    [FieldOffset(0x8)]
    public delegate* unmanaged<AudioDevice*, AudioRendererDeviceParams*, int> Init;

    [FieldOffset(0x10)]
    public delegate* unmanaged<AudioDevice*, void> Start;

    [FieldOffset(0x18)]
    public delegate* unmanaged<AudioDevice*, void> Stop;

    [FieldOffset(0x20)]
    public delegate* unmanaged<AudioDevice*, uint, out byte*, int> RenderGetBuffer;

    [FieldOffset(0x28)]
    public delegate* unmanaged<AudioDevice*, uint, nint, int> RenderReleaseBuffer;

    // WASAPI has got a no-op.
    // Mock subtracts CurrentPaddingStart based on (float) SampleRate * (float) param2
    [FieldOffset(0x30)]
    public delegate* unmanaged<AudioDevice*, uint, void> x30;

    // Mock sets param2 to (int) ((float) CurrentPaddingEnd - (float) CurrentPaddingStart) and returns 0
    [FieldOffset(0x38)]
    public delegate* unmanaged<AudioDevice*, out uint, int> ClientGetCurrentPadding;

    [FieldOffset(0x40)]
    public delegate* unmanaged<AudioDevice*, int> GetSampleRate;

    // WASAPI returns Format->nChannels
    // Mock returns Channels
    [FieldOffset(0x48)]
    public delegate* unmanaged<AudioDevice*, int> GetChannelCount;

    // WASAPI returns x24
    // Mock returns xC
    [FieldOffset(0x50)]
    public delegate* unmanaged<AudioDevice*, int> x50;
}
