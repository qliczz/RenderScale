using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FloppyUtils.Concurrency;
using FloppyUtils.Ptrs;
using FloppyUtils.SourceGen.Attributes;
using System;
using System.Collections.Generic;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace FloppyUtils.Graphics;

[ScopedEvents]
public sealed partial class RenderEvents : IAsyncLoadable
{
    private readonly ThreadedHookSet _hooks = new();

    [HookEvent] // Can't lock Present because NVIDIA Smooth Motion calls it from a separate thread.
    private readonly ThreadedHookProxy<Present> _presentHook;
    [HookEvent]
    private readonly ThreadedHookProxy<PostTick> _postTickHook;
    [HookEvent]
    private readonly ThreadedHookProxy<ResizeDestroy> _resizeDestroyHook;
    [HookEvent]
    private readonly ThreadedHookProxy<ResizeCreate> _resizeCreateHook;
    [HookEvent(RLock = nameof(Lock))]
    private readonly ThreadedHookProxy<ImmediateProcessCommands> _immediateProcessCommandsHook;
    [HookEvent(RLock = nameof(Lock))]
    private readonly ThreadedHookProxy<TaskRenderGraphicsRender> _taskRenderGraphicsRenderHook;
    [HookEvent(RLock = nameof(Lock))]
    private readonly ThreadedHookProxy<TaskUpdateGraphicsRender> _taskUpdateGraphicsRenderHook;

    private ComPtr<IDXGISwapChain> _dxgiswap;
    private readonly DelayedExecutionThreadShortCircuit _renderTaskRenderThreadSC = new();
    private readonly DelayedExecutionThreadShortCircuit _renderExecThreadSC = new();
    private readonly DelayedExecutionThreadShortCircuit _presentThreadSC = new();

    public unsafe RenderEvents()
    {
        RenderTaskRenderThread = new("RenderTaskRender", () => _renderTaskRenderThreadSC.CanRun(Thread.CurrentThread) || FService.Framework.IsFrameworkUnloading);
        RenderExecThread = new("RenderExec", () => _renderExecThreadSC.CanRun(Thread.CurrentThread) || FService.Framework.IsFrameworkUnloading);
        PresentThread = new("Present", () => _presentThreadSC.CanRun(Thread.CurrentThread) || FService.Framework.IsFrameworkUnloading);
        PostTickThread = new("PostTick", () => FService.Framework.IsFrameworkUnloading, () => !_renderTaskRenderThreadSC.IsReady || !_renderExecThreadSC.IsReady || !_presentThreadSC.IsReady || FService.Framework.IsFrameworkUnloading || FService.Framework.IsInFrameworkUpdateThread);

        var dev = (DeviceEx*) Device.Instance();

        var dxgiswap = ComPtr.AddRef(dev->SwapChain->DXGISwapChain);

        // We're is in an ugly hook arms race between Dalamud (ImGui), ReShade, OptiScaler, and possibly other tools too. Oops.
        PeelResult = ComPeeler.PeelIUnknown(&dxgiswap, sizeof(IDXGISwapChain.Vtbl<IDXGISwapChain>));
        _dxgiswap.Attach(dxgiswap);

        _hooks.Add(_presentHook = FService.GameInteropProvider.HookFromAddress<Present>(
            _dxgiswap.GetVtbl<IDXGISwapChain, IDXGISwapChain.Vtbl<IDXGISwapChain>>()->Present,
            OnPresentDetour
        ).On(FService.UpdateThread));
        InitPresentEvent((ctx, dxgiswap, syncInterval, flags) => _presentHook.OriginalDisposeSafe(dxgiswap, syncInterval, flags));

        FService.Assert(Sigs.DevicePostTickCall.Sigs.Length == 3);

        FService.Assert(Ptr.For<nint>(Sigs.DevicePostTickCall.Sigs[0].Scan()).TryRead(out var verifyDevicePostTickCallRenderManager));
        verifyDevicePostTickCallRenderManager.SameAddr((nint) Manager.Instance());

        FService.Assert(Ptr.For<nint>(Sigs.DevicePostTickCall.Sigs[1].Scan()).TryRead(out var verifyDevicePostTickCallDevice));
        verifyDevicePostTickCallDevice.SameAddr((nint) Device.Instance());

        _hooks.Add(_postTickHook = FService.GameInteropProvider.HookFromAddress<PostTick>(
            Sigs.DevicePostTickCall.Sigs[2].Scan(),
            OnPostTickDetour
        ).On(FService.UpdateThread));
        InitPostTickEvent((ctx, dev) => _postTickHook.OriginalDisposeSafe(dev));

        _hooks.Add(_resizeDestroyHook = FService.GameInteropProvider.HookFromAddress<ResizeDestroy>(
            ((nint) dev->_.OnResizeDestroy->Entries[0].Function).SameAddr(Sigs.RTMInitializeInstallResizeCallbacks.Sigs[0].Scan()),
            OnResizeDestroyDetour
        ).On(FService.UpdateThread));
        InitResizeDestroyEvent(ctx => _resizeDestroyHook.OriginalDisposeSafe());

        _hooks.Add(_resizeCreateHook = FService.GameInteropProvider.HookFromAddress<ResizeCreate>(
            ((nint) dev->_.OnResizeCreate->Entries[0].Function).SameAddr(Sigs.RTMInitializeInstallResizeCallbacks.Sigs[1].Scan()),
            OnResizeCreateDetour
        ).On(FService.UpdateThread));
        InitResizeCreateEvent(ctx => _resizeCreateHook.OriginalDisposeSafe());

        _hooks.Add(_immediateProcessCommandsHook = FService.GameInteropProvider.HookFromAddress<ImmediateProcessCommands>(
            Sigs.ImmediateProcessCommandsViaDevicePostTick.Scan().SameAddr(Sigs.ImmediateProcessCommandsViaRenderThreadRun.Scan()),
            OnImmediateProcessCommandsDetour
        ).On(PostTickThread));
        InitImmediateProcessCommandsEvent((ctx, im, cmds, count) => _immediateProcessCommandsHook.OriginalDisposeSafe(im, cmds, count));

        var tm = TaskManager.Instance();
        var rm = Manager.Instance();

        Task* taskRenderGraphicsRenderTask = null;
        int taskRenderGraphicsRenderPrio = -1;
        FService.Assert(Sigs.TaskRenderGraphicsRenderEntry.Sigs.Length == 2);

        for (int prio = (int) tm->TaskCount - 1; prio >= 0; --prio)
        {
            for (var task = &tm->TaskList[prio].Task; task != null; task = task->Next)
            {
                if (task->Runner == null || task->Func == null)
                {
                    continue;
                }

                nint managerPtr;
                nint real;
                nint devicePtr;
                try
                {
                    managerPtr = Sigs.TaskRenderGraphicsRenderEntry.Sigs[0].ResolveRelativeTo((nint) task->Func);
                    real = Sigs.TaskRenderGraphicsRenderEntry.Sigs[1].ResolveRelativeTo((nint) task->Func);
                    devicePtr = Sigs.TaskRenderGraphicsRenderReal.ResolveRelativeTo(real);
                }
                catch (ArgumentException)
                {
                    // FService.PluginLog.Debug($"TaskRenderGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} candidate dropped: {ex.Message}");
                    continue;
                }

                if (!Ptr.For<nint>(managerPtr).TryRead(out var manager) || manager != (nint) rm)
                {
                    FService.PluginLog.Debug($"TaskRenderGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} was a candidate but failed Render::Manager check: wanted {((nint) rm).ToDebugString()} vs got {manager.ToDebugString()}");
                    continue;
                }

                if (!Ptr.For<nint>(devicePtr).TryRead(out var device) || device != (nint) dev)
                {
                    FService.PluginLog.Debug($"TaskRenderGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} was a candidate but failed Device check: wanted {((nint) dev).ToDebugString()} vs got {device.ToDebugString()}");
                    continue;
                }

                FService.PluginLog.Debug($"TaskRenderGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} -> {((nint) task->Func).ToDebugString()}");
                taskRenderGraphicsRenderTask = task;
                taskRenderGraphicsRenderPrio = prio;
                break;
            }
        }

        FService.Assert(taskRenderGraphicsRenderTask != null);
        FService.Assert(taskRenderGraphicsRenderTask->Func != null);
        FService.Assert(taskRenderGraphicsRenderPrio != -1);

        _hooks.Add(_taskRenderGraphicsRenderHook = FService.GameInteropProvider.HookFromAddress<TaskRenderGraphicsRender>(
            taskRenderGraphicsRenderTask->Func,
            OnTaskRenderGraphicsRenderDetour
        ).On(FService.UpdateThread));
        InitTaskRenderGraphicsRenderEvent(ctx => _taskRenderGraphicsRenderHook.OriginalDisposeSafe());

        // TaskUpdateGraphicsRender should be N prios before TaskRenderGraphicsRender, according to setup.
        Task* taskUpdateGraphicsRenderTask = null;
        FService.Assert(Sigs.TaskRenderGraphicsRenderEntry.Sigs.Length == 2);

        for (int prio = taskRenderGraphicsRenderPrio - 1; prio >= 0; --prio)
        {
            for (var task = &tm->TaskList[prio].Task; task != null; task = task->Next)
            {
                if (task->Runner == null || task->Func == null)
                {
                    continue;
                }

                nint managerPtr;
                try
                {
                    managerPtr = Sigs.TaskUpdateGraphicsRenderEntry.ResolveRelativeTo((nint) task->Func);
                }
                catch (ArgumentException)
                {
                    // FService.PluginLog.Debug($"TaskUpdateGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} candidate dropped: {ex.Message}");
                    continue;
                }

                if (!Ptr.For<nint>(managerPtr).TryRead(out var manager) || manager != (nint) rm)
                {
                    FService.PluginLog.Debug($"TaskUpdateGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} was a candidate but failed Render::Manager check: wanted {((nint) rm).ToDebugString()} vs got {manager.ToDebugString()}");
                    continue;
                }

                FService.PluginLog.Debug($"TaskUpdateGraphicsRender Task Prio 0x{prio:X2} @ 0x{(long) (nint) task:X16} -> {((nint) task->Func).ToDebugString()}");
                taskUpdateGraphicsRenderTask = task;
                break;
            }
        }

        FService.Assert(taskUpdateGraphicsRenderTask != null);
        FService.Assert(taskUpdateGraphicsRenderTask->Func != null);

        _hooks.Add(_taskUpdateGraphicsRenderHook = FService.GameInteropProvider.HookFromAddress<TaskUpdateGraphicsRender>(
            taskUpdateGraphicsRenderTask->Func,
            OnTaskUpdateGraphicsRenderDetour
        ).On(FService.UpdateThread));
        InitTaskUpdateGraphicsRenderEvent((ctx, framework) => _taskUpdateGraphicsRenderHook.OriginalDisposeSafe(framework));

        OnTaskRenderGraphicsRender += InternalOnTaskRenderGraphicsRender;
        OnImmediateProcessCommands += InternalOnImmediateProcessCommands;
        OnPresent += InternalOnPresent;
        OnPostTick += InternalOnPostTick;
    }

    private unsafe delegate int Present(IDXGISwapChain* dxgiswap, uint syncInterval, uint flags);
    private unsafe delegate void PostTick(DeviceEx* dev);
    private delegate byte ResizeDestroy();
    private delegate void ResizeCreate();
    private unsafe delegate void ImmediateProcessCommands(ImmediateContextEx* im, RenderCommandBufferGroup* cmds, uint count);
    private delegate void TaskRenderGraphicsRender();
    private unsafe delegate void TaskUpdateGraphicsRender(Framework* framework);

    /* Must be a RW lock as ImmediateProcessCommands on the render thread can be invoked
     * while the main thread is busy with TaskRenderGraphicsRender, which would normally be fine,
     * except that locking on the main thread invokes ClassicSTAThreadWaitForHandles, which in turn
     * executes CCliModalLoop, which executes PeekMessageW, which... I'm not sure yet why it deadlocks, but it does.
     * I truly wish I had more time to debug this further, but for now...
     * FIXME: Abolish the RenderEvents lock entirely in the future.
     * -jade
     */
    public RWLock Lock { get; } = new();

    public DelayedExecutionContext RenderTaskRenderThread { get; }

    public DelayedExecutionContext RenderExecThread { get; }

    // Strictly speaking the same as UpdateThread, but with some extra guarantees about the render thread.
    // ... except for when NVIDIA Smooth Motion runs this on a separate thread, but we can account for that.
    public DelayedExecutionContext PresentThread { get; }

    public DelayedExecutionContext PostTickThread { get; }

    public ComPeeler.Result PeelResult { get; }

    public unsafe IDXGISwapChain* RawSwapChain => _dxgiswap.Get();

    public async System.Threading.Tasks.Task LoadAsync(CancellationToken cancel)
    {
        await _hooks;
    }

    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        var disposeHooks = _hooks.DisposeAsync();

        _renderTaskRenderThreadSC.Dispose();
        _renderExecThreadSC.Dispose();
        _presentThreadSC.Dispose();

        await RenderTaskRenderThread;
        await RenderExecThread;
        await PresentThread;
        await PostTickThread;

        await disposeHooks;

        _dxgiswap.Dispose();

        Lock.Dispose();
    }

    private unsafe void InternalOnTaskRenderGraphicsRender(TaskRenderGraphicsRenderContext ctx)
    {
        if (!_renderTaskRenderThreadSC.IsReady && !FService.Framework.IsInFrameworkUpdateThread)
        {
            _renderTaskRenderThreadSC.Set(Thread.CurrentThread);
        }

        RenderTaskRenderThread.Pump();

        ctx.Invoke();
    }

    private unsafe void InternalOnImmediateProcessCommands(ImmediateProcessCommandsContext ctx, ImmediateContextEx* im, RenderCommandBufferGroup* cmds, uint count)
    {
        if (!_renderExecThreadSC.IsReady && !FService.Framework.IsInFrameworkUpdateThread)
        {
            _renderExecThreadSC.Set(Thread.CurrentThread);
        }

        RenderExecThread.Pump();

        ctx.Invoke(im, cmds, count);
    }

    private unsafe int InternalOnPresent(PresentContext ctx, IDXGISwapChain* dxgiswap, uint syncInterval, uint flags)
    {
        _presentThreadSC.Set(Thread.CurrentThread);

        PresentThread.Pump();

        return ctx.Invoke(dxgiswap, syncInterval, flags);
    }

    private unsafe void InternalOnPostTick(PostTickContext ctx, DeviceEx* dev)
    {
        PostTickThread.Pump();

        ctx.Invoke(dev);
    }

    private static class Sigs
    {
        /* DeviceDX11::PostTick is called by Framework::Tick.
         */
        public static readonly MultiRVASig DevicePostTickCall = new(new GhidraCode(@"
       1400d0efc 48  8b  8b       MOV        RCX ,qword ptr [RBX  + 0x2b88 ]
                 88  2b  00 
                 00
       1400d0f03 48  85  c9       TEST       RCX ,RCX
       1400d0f06 74  05           JZ         LAB_1400d0f0d
       1400d0f08 e8  ??  ??       CALL       nullsub_59                                       undefined nullsub_59()
                 ??  ??
       1400d0f0d (48  8b  0d       MOV        RCX ,qword ptr [g_Client::Graphics::Render::Man
                 *??  ??  ?? 
                 ??)
       1400d0f14 e8  ??  ??       CALL       nullsub_19                                       undefined nullsub_19()
                 ??  ??
       1400d0f19 (48  8b  0d       MOV        RCX ,qword ptr [g_Client::Graphics::Kernel::Dev
                 *??  ??  ?? 
                 ??)
       1400d0f20 (e8  *??  ??       CALL       Client::Graphics::Kernel::DeviceDX11.PostTick    undefined DeviceDX11.PostTick()
                 ??  ??)
       1400d0f25 80  7b  08       CMP        byte ptr [RBX  + 0x8 ],0x0
                 00
").Sig);

        /* Device.RequestResolutionChange and other triggers run some callbacks,
         * registered to the device in RTM init and run in PostTick.
         */
        public static readonly MultiRVASig RTMInitializeInstallResizeCallbacks = new("89 81 2C 04 00 00| B8 56 55 55 55| 41 F7 E8| 45 33 C0| 8B C2| C1 E8 1F| 03 D0| 66 89 91 ?? ?? ?? ??| 48 8D 15 ?? ?? ?? ??| 48 8B CB| E8 ?? ?? ?? ??| 45 33 C0| 89 87 60 04 00 00| 48 8D 15 ?? ?? ?? ??| 48 8B CB| E8 ?? ?? ?? ??| 45 33 C0| 89 87 64 04 00 00| (48 8D 15 *?? ?? ?? ??)| 48 8B CB| E8 ?? ?? ?? ??| 45 33 C0| 89 87 68 04 00 00| (48 8D 15 *?? ?? ?? ??)| 48 8B CB| E8 ?? ?? ?? ??| 89 87 6C 04 00 00| 41 BD 01 00 00 00| 48 8B 15 ?? ?? ?? ??| 41 8B CD| 41 BF 50 14 00 00| C7 44 24 28 03 00 00 00| 41 B9 50 82 00 00| C7 44 24 20 08 00 00 00| 45 8B C5| 0F B6 42 33| 2B C8|");

        /* ImmediateContextDX11.ProcessCommands can run in either DeviceX11.PostTick,
         * or in RenderThread.Run, which is prone to race conditions and crashes with manual DX operations.
         * While we can f.e. regen RTMs after ProcessCommands is done, it would still leave us
         * with some other race conditions.
         *
         * Identifiable by being called by DeviceDX11.PostTick and RenderThread.Run
         */
        public static readonly RVASig ImmediateProcessCommandsViaDevicePostTick = new("8B 86 88 00 00 00| 85 C0| 75 0C| C7 86 88 00 00 00 01 00 00 00| EB 05| 83 F8 01| 75 1A| 44 8B 86 10 09 00 00| 48 8B 96 08 09 00 00| 48 8B 8E C0 0A 0E 00| (E8 *?? ?? ?? ??)| 48 8B 8E B0 0A 0E 00| 48 8B 01| FF 90 70 03 00 00| 83 BE 88 00 00 00 01| 75 47|");
        public static readonly RVASig ImmediateProcessCommandsViaRenderThreadRun = new("48 8B 4B 28| BA FF FF FF FF| FF 15 ?? ?? ?? ??| 0F B6 43 20| 84 C0| 75 3C| 48 8B 0D ?? ?? ?? ??| 83 B9 88 00 00 00 01| 75 1A| 44 8B 81 10 09 00 00| 48 8B 91 08 09 00 00| 48 8B 89 C0 0A 0E 00| (E8 *9E F1 FF FF) 48 8B 4B 30| FF 15 ?? ?? ?? ??|");

        /* TaskRenderGraphicsRender is registered in the task manager task list by Framework.Setup.
         * Specifically, it's to a function which first places Client::Graphics::Render::Manager into RCX before jumping to this.
         * Hooking this is relevant for post-processing, and can be validated easily by enabling TSCMAA at >1x scale.
         * Prios can change between patches, and the task list can update mid execution.
         */
        public static readonly MultiRVASig TaskRenderGraphicsRenderEntry = new(new GhidraCode(@"
       1400d32a0 (48  8b  0d       MOV        RCX ,qword ptr [g_Client::Graphics::Render::Man
                 *??  ??  ?? 
                 ??)
       1400d32a7 (e9  *??  ??       JMP        sub_1402AF7E0
                 ??  ??)
").Sig);

        public static readonly RVASig TaskRenderGraphicsRenderReal = new(new GhidraCode(@"
       1402af7e0 40  53           PUSH       RBX
       1402b9f22 57              PUSH       RDI
       1402b9f23 41 54           PUSH       R12
       1402af7e4 41  55           PUSH       R13
       1402af7e6 48  83  ec       SUB        RSP ,0x58
                 58
       1402af7ea 65  48  8b       MOV        RAX ,qword ptr GS :[offset  ThreadLocalStoragePoi  = 00000000
                 04  25  ?? 
                 ??  ??  ??
       1402af7f3 ??  8b  e9       MOV        RBP ,RCX
       1402af7f6 8b  15  ??       MOV        EDX ,dword ptr [_tls_index ]
                 ??  ??  ??
       1402af7fc (48  8b  1d       MOV        RBX ,qword ptr [g_Client::Graphics::Kernel::Dev
                 *??  ??  ?? 
                 ??)
       1402af803 4c  8b  ??       MOV        R13 ,qword ptr [RAX  + RDX *0x8 ]
                 d0
       1402af807 b8  ??  ??       MOV        EAX ,0x203c4
                 ??  ??
       1402af80c 42  80  3c       CMP        byte ptr [RAX  + R13 *0x1 ],0x0
                 ??  00
       1402af811 75  05           JNZ        LAB_1402af818
       1402af813 e8  ??  ??       CALL       __dyn_tls_on_demand_init                         undefined __dyn_tls_on_demand_in
                 ??  ??
").Sig);

        /* Same as TaskRenderGraphicsRenderEntry but for TaskUpdateGraphicsRender and with more params.
         */
        public static readonly RVASig TaskUpdateGraphicsRenderEntry = new(new GhidraCode(@"
       1400d4240 f3 0f 10        MOVSS      XMM1,dword ptr [RCX + 0x16c0]
                 89 c0 16
                 00 00
       1400d4248 (48 8b 0d        MOV        RCX,qword ptr [g_Client::Graphics::Render::Man
                 *?? ?? ?? ??)
       1400d424f e9 ?? ??        JMP        sub_1402B9A60                                    undefined sub_1402B9A60()
                 ?? ??
").Sig);
    }
}
