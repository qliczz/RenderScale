// Derived from 0x0ade/DP-CustomResolution f8113438, AGPL-3.0. See THIRD_PARTY_NOTICES.md.
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FloppyUtils;
using FloppyUtils.Concurrency;
using FloppyUtils.Graphics;
using FloppyUtils.Ptrs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using Task = System.Threading.Tasks.Task;

namespace RenderScale.States;

public sealed class GameSizeState : IAsyncLoadable
{
    public const uint MinSize = 64;

    private const int _pendingApplyTimeout = 2;

    private readonly ThreadedHookSet _hooks = new();
    private readonly ThreadedHookProxy<RenderTargetManagerApplyScaling> _renderTargetManagerApplyScalingHook;
    private readonly ThreadedHookProxy<RenderTargetManagerUpdate> _renderTargetManagerUpdate;
    private readonly ThreadedHookProxy<PrepareTexture> _prepareTextureHook;
    private readonly ThreadedHookProxy<ImmediateBindPSSRVs> _immediateBindPSSRVsHook;
    private readonly ThreadedHookProxy<ImmediateBindCSSRVs> _immediateBindCSSRVsHook;
    private readonly ThreadedHookProxy<FsrPushBlitCommands> _fsrPushBlitCommandsHook;
    private readonly ThreadedHookProxy<CreateTexture2D> _createTexture2DHook;

    private readonly RenderEvents.Scoped _render;
    private ThreadedHookProxy<DlssEvaluate>? _dlssEvaluate;
    private nint _dlssModule;
    public DlssSession Dlss { get; } = new();
    public bool CanObserveDlss => _dlssEvaluate?.IsEnabled == true;
    public bool IsRestoring => _wasEnabled && !Service.Config._.GameTarget.IsEnabled;
    public unsafe bool HasDlssBackend => PostEffectManagerEx.Instance() != null && PostEffectManagerEx.Instance()->DLSS != null;
    private static bool UseFsrOverrides => !FService.Unloading && Service.Config._.GameTarget.IsEnabled &&
        Service.Config._.ResolutionScalingMode != ResolutionScalingMode.DLSS;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DlssEvaluate(nint context, nint handle, nint parameters, nint callback);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, string name, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);

    private uint DlssEvaluateDetour(nint context, nint handle, nint parameters, nint callback)
    {
        var token = Dlss.Token;
        var result = _dlssEvaluate!.OriginalDisposeSafe(context, handle, parameters, callback);
        Dlss.Observe(token, result, Environment.TickCount64);
        return result;
    }

    private void AttachDlssObserver()
    {
        // Take a reference to an ALREADY loaded game module; never load or replace
        // a DLSS DLL from disk. Keep the reference until its hook is disposed.
        if (!GetModuleHandleExW(0, "nvngx_dlss.dll", out _dlssModule)) return;
        try
        {
            var entry = GetProcAddress(_dlssModule, "NVSDK_NGX_D3D11_EvaluateFeature");
            if (entry == 0) throw new InvalidOperationException("DLSS D3D11 evaluation export missing");
            _dlssEvaluate = FService.GameInteropProvider.HookFromAddress<DlssEvaluate>(entry, DlssEvaluateDetour)
                .On(_render.Unscoped.RenderExecThread);
            _hooks.Add(_dlssEvaluate);
        }
        catch (Exception ex)
        {
            FreeLibrary(_dlssModule);
            _dlssModule = 0;
            Service.PluginLog.Warning(ex, "DLSS observation unavailable; DLSS previews are blocked");
        }
    }

    private ComPtr<ID3D11DeviceContext> _d3dctx;
    private ComPtr<ID3D11Device> _d3ddev;

    private readonly StringBuilder _dbg = new();

    private ComPtr<ID3D11SamplerState> _samplerMips;
    private ComPtr<ID3D11SamplerState> _samplerPoint;
    private ComPtr<ID3D11SamplerState> _samplerLinear;
    private bool _wasEnabled = false;
    private (float Scale, byte Dynamic, byte Upscale, ushort MinHeight, uint W, uint H)? _original;
    private DateTime _nextResize;
    public bool RestoreFromCurrentConfig { get; set; }

    private (int Time, bool Active) _pendingApply;

    public unsafe GameSizeState()
    {
        var pfx = PostEffectManagerEx.Instance();
        FService.PluginLog.Debug($"PostEffectManager: 0x{(long) (nint) pfx:X16}");
        FService.PluginLog.Info($"DLSS backend object: {(pfx != null && pfx->DLSS != null ? "present (not proof of evaluation)" : "absent")}");

        var dev = (DeviceEx*) Device.Instance();
        _d3dctx.Attach(dev->D3D11DeviceContext);

        _d3dctx.Get()->GetDevice(Ptr.Out(out OutComPtr<ID3D11Device> d3ddev));
        _d3ddev.Attach(d3ddev.Value);

        _render = Service.RenderEvents.CreateScoped();

        /* Writes DynamicResolutionTargetHeight to size[1] near the end,
         * but if in (gpose || main menu), only scale < 1??
         */
        _hooks.Add(_renderTargetManagerApplyScalingHook = FService.GameInteropProvider.HookFromAddress<RenderTargetManagerApplyScaling>(
            FService.SigScanner.ScanText(Sigs.RenderTargetManagerApplyScaling)
                // Call site with third arg = 0, called less frequently, currently unknown what exactly it belongs to
                .SameAddr(FService.SigScanner.ScanRVASig(new("48 89 5C 24 48| 45 33 C0| 48 8B 1D ?? ?? ?? ??| 48 8B F1| 48 89 6C 24 50| 4C 89 74 24 20| 4C 8D B1 28 04 00 00| 49 8B D6| 48 8B 83 8C 00 00 00| 49 89 06| (E8 *95 08 00 00)| 48 8B 05 ?? ?? ?? ??| B9 01 00 00 00| 0F B6 50 33| 41 8B 06|"))),
            RenderTargetManagerApplyScalingDetour
        ).On(FService.UpdateThread));

        /* Hook the RTM part of TaskUpdateGraphicsRender, as it's responsible for dynamic resolution updates.
         */
        // TODO: Add pointer verification
        _hooks.Add(_renderTargetManagerUpdate = FService.GameInteropProvider.HookFromAddress<RenderTargetManagerUpdate>(
            FService.SigScanner.ScanText(Sigs.RenderTargetManagerUpdate).Log(),
            RenderTargetManagerUpdateDetour
        ).On(FService.UpdateThread));

        /* Texture preparation used in a lot of places, but most notable for
         * writing to the mip level count of the gameplay texture and also updating the D3D11 ptrs.
         */
        _hooks.Add(_prepareTextureHook = FService.GameInteropProvider.HookFromAddress<PrepareTexture>(
            FService.SigScanner.ScanRVASig(new("48 8D 4B 20| E8 ?? ?? ?? ??| 48 89 5F 58| 48 8B 4D A7| 48 85 C9| 74 0A| 48 8B 01| FF 50 10| 4C 89 65 A7| 44 38 67 48| 74 4E| 48 8B 4F 60| 48 8D 57 38| 44 8B 4F 4C| 41 B8 01 00 00 00| 48 85 C9| 74 0B| (E8 *?? ?? ?? ??)| 84 C0| 75 2E| EB 8E| 8B 47 50| 48 8B 0D ?? ?? ?? ??| 0F BA E8 15| C7 44 24 28 01 00 00 00| 89 44 24 20| E8 ?? ?? ?? ??| 48 85 C0|"))
                .SameAddr(FService.SigScanner.ScanRVASig(new("48 89 9C 24 C8 00 00 00| 49 8D 95 8C 00 00 00| 48 8B 02| 48 8D 99| 28 04 00 00| 48 89 B4 24 D0 00 00 00| 48 89 03| 48 8B 05 ?? ?? ?? ??| 4C 89 A4 24 D8 00 00 00| 4C 89 B4 24 98 00 00 00| 4C 89 BC 24 90 00 00 00| 80 78 7A 00| 74 20| 48 8B 89 70 03 00 00| 48 85 C9| 74 14| 45 33 C9| 45 8D 41 01| (E8 *?? ?? ?? ??)| 84 C0| 0F 84 07 01 00 00|"))),
            PrepareTextureDetour
        ).On(FService.UpdateThread));

        /* The shader resource view of GameplayTexture gets read by these two.
         * The two functions should be identical with similar usage patterns, but read data from different offsets.
         * (This trips up some signature scripts for Ghidra, such as Sigga.)
         */
        // TODO: Add pointer verification
        _hooks.Add(_immediateBindPSSRVsHook = FService.GameInteropProvider.HookFromAddress<ImmediateBindPSSRVs>(
            FService.SigScanner.ScanText("48 8B C4 4C 89 40 18 48 89 48 08 53 56 48 83 EC 68 44 8B 5A 44 48 8D B1 E8 03 00 00 4C 89 68 D8").Log(),
            ImmediateBindPSSRVsDetour
        ).On(_render.Unscoped.RenderExecThread));
        _hooks.Add(_immediateBindCSSRVsHook = FService.GameInteropProvider.HookFromAddress<ImmediateBindCSSRVs>(
            FService.SigScanner.ScanText("48 8B C4 4C 89 40 18 48 89 48 08 53 56 48 83 EC 68 44 8B 5A 44 48 8D B1 E8 13 00 00 4C 89 68 D8").Log(),
            ImmediateBindCSSRVsDetour
        ).On(_render.Unscoped.RenderExecThread));

        /* Hook FSR blit submission to help fix some low VRAM mode crimes.
         */
        Service.PluginLog.Debug(Sigs.FsrPushBlitCommands);
        _hooks.Add(_fsrPushBlitCommandsHook = FService.GameInteropProvider.HookFromAddress<FsrPushBlitCommands>(
            FService.SigScanner.ScanTextRaw(Sigs.FsrPushBlitCommands).Log(),
            FsrPushBlitCommandsDetour
        ).On(_render.Unscoped.RenderTaskRenderThread));

        /* Hook CreateTexture2D to forcibly set the mipmap generation flag.
         */
        var d3ddevVtbl = _d3ddev.GetVtbl<ID3D11Device, ID3D11Device.Vtbl<ID3D11Device>>();
        _hooks.Add(_createTexture2DHook = FService.GameInteropProvider.HookFromAddress<CreateTexture2D>(
            d3ddevVtbl->CreateTexture2D,
            CreateTexture2DDetour
        ).On(FService.UpdateThread));

        var samplerDesc = D3D11_SAMPLER_DESC.DEFAULT;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR;
        _d3ddev.Get()->CreateSamplerState(&samplerDesc, Ptr.Out(out OutComPtr<ID3D11SamplerState> samplerState)).ThrowIfFailed();
        _samplerMips.Attach(samplerState.Value);

        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT;
        samplerDesc.MinLOD = 0;
        samplerDesc.MaxLOD = 0;
        _d3ddev.Get()->CreateSamplerState(&samplerDesc, Ptr.Out(out samplerState)).ThrowIfFailed();
        _samplerPoint.Attach(samplerState.Value);

        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
        _d3ddev.Get()->CreateSamplerState(&samplerDesc, Ptr.Out(out samplerState)).ThrowIfFailed();
        _samplerLinear.Attach(samplerState.Value);
    }

    private unsafe delegate void RenderTargetManagerApplyScaling(RenderTargetManagerEx* rtm, uint* size, byte unk1);
    // Native code consumes XMM1 (frame delta); preserve the second ABI argument.
    private unsafe delegate void RenderTargetManagerUpdate(RenderTargetManagerEx* rtm, float deltaTime);
    private unsafe delegate ulong PrepareTexture(Texture* tex, uint* size, byte mips, uint format);
    private unsafe delegate void ImmediateBindPSSRVs(ImmediateContextEx* im, PixelShader* ps, ImmediateSRV* data);
    private unsafe delegate void ImmediateBindCSSRVs(ImmediateContextEx* im, Shader* cs, ImmediateSRV* data);
    private unsafe delegate void FsrPushBlitCommands(PostEffectManagerEx.FsrData* fsr, Texture* swapchain, Context* ctx);
    private unsafe delegate HRESULT CreateTexture2D(ID3D11Device* d3ddev, D3D11_TEXTURE2D_DESC* desc, D3D11_SUBRESOURCE_DATA* initialData, ID3D11Texture2D** tex);

    public bool ConfigDynRezo { get; private set; }
    public ResolutionScalingMode ConfigGraphicsRezoType { get; private set; }
    public float ConfigGraphicsRezoScale { get; private set; }

    public uint CurrentTargetWidth { get; private set; }
    public uint CurrentTargetHeight { get; private set; }

    public uint CurrentRenderWidth { get; private set; }
    public uint CurrentRenderHeight { get; private set; }

    public string DebugInfo => _dbg.ToString();

    public Task LoadAsync(CancellationToken cancel)
    {
        AttachDlssObserver();
        Service.Framework.Update += Update;

        unsafe
        {
            _render.OnResizeDestroy += RTMDestroyAfterResizeDetour;
            _render.OnResizeCreate += RTMRegenAfterResizeDetour;
            _render.OnTaskRenderGraphicsRender += TaskRenderGraphicsRenderDetour;
        }

        return _hooks.EnableAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!Service.Framework.IsFrameworkUnloading)
            await FService.UpdateThread.Run(() => Update(Service.Framework));

        _render.Dispose();

        var disposeHooks = _hooks.DisposeAsync();
        await _render.Unscoped.RenderTaskRenderThread;
        await _render.Unscoped.RenderExecThread;
        await disposeHooks;
        Dlss.Stop();
        if (_dlssModule != 0) { FreeLibrary(_dlssModule); _dlssModule = 0; }

        _samplerLinear.Dispose();
        _samplerPoint.Dispose();
        _samplerMips.Dispose();

        _d3ddev.Dispose();

        Service.Framework.Update -= Update;
    }

    private unsafe void Update(IFramework framework)
    {
        var unloading = FService.Unloading;
        if (!Service.Plugin.IsReady && !unloading)
        {
            return;
        }

        _dbg.Clear();

        ref var cfg = ref Service.Config._;

        ConfigDynRezo = Service.GameConfig.System.GetUInt(SystemConfigOption.DynamicRezoType.ToString()) != 0U;
        // GameConfig starts with FSR at 0; GraphicsConfig starts with FSR at 1
        ConfigGraphicsRezoType = ResolutionScalingModeExt.FromXIVSysConf(Service.GameConfig.System.GetUInt(SystemConfigOption.GraphicsRezoUpscaleType.ToString()));
        ConfigGraphicsRezoScale = Service.GameConfig.System.GetUInt(SystemConfigOption.GraphicsRezoScale.ToString()) / 100f;

        var dev = (DeviceEx*) Device.Instance();
        var gfx = (GraphicsConfigEx*) GraphicsConfig.Instance();
        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();

        if (Service.DebugConfig.IsDebug)
        {
            _dbg.Append(@$"DR !{gfx->DynamicRezoEnable} +{gfx->DynamicRezoEnableBeyond1} _{gfx->DynamicRezoEnableCutScene} ?{gfx->DynamicRezoEnableUnkx47} ?{gfx->DynamicRezoEnableUnkx48}
GR x{gfx->GraphicsRezoScale} ?{gfx->GraphicsRezoUnk1} _{gfx->GraphicsRezoUpscaleType} ?{gfx->GraphicsRezoUnk2}
CHG {dev->RequestResolutionChange} {dev->RequestResolutionChangeUnk1} {dev->UnkRelatedToDynScale} {dev->RequestResolutionChangeUnk3}
RTM {rtm->_.Resolution_Width} x {rtm->_.Resolution_Height}
RTM H {rtm->DynamicResolutionActualTargetHeight} {rtm->DynamicResolutionTargetHeight} {rtm->DynamicResolutionMaximumHeight} {rtm->DynamicResolutionMinimumHeight}
RTM S {rtm->GraphicsRezoScaleUnkNegative} {rtm->GraphicsRezoScaleMain} !{rtm->GraphicsRezoScaleUnk1}! {rtm->GraphicsRezoScaleGlassX} {rtm->GraphicsRezoScaleGlassY} {rtm->GraphicsRezoScaleUnk2}
RR {dev->RequestRender} 0x{(long) dev->ImmediateContext->IfNonZeroSkipPostTickProcess:X16}
GFXUNK {gfx->UnkAffectsRTMGameplayTexture}
");
        }

        var enabled = !unloading && cfg.GameTarget.IsEnabled;
        CurrentRenderWidth = rtm->_.Resolution_Width;
        CurrentRenderHeight = rtm->_.Resolution_Height;
        if (!enabled && !_wasEnabled) return;
        if (enabled && !_wasEnabled)
        {
            _original = (gfx->GraphicsRezoScale, gfx->DynamicRezoEnable, gfx->GraphicsRezoUpscaleType,
                rtm->DynamicResolutionMinimumHeight, dev->Width, dev->Height);
            RestoreFromCurrentConfig = false;
        }


        var targetSize = new GameTargetSize();
        float scaleRender;
        if (enabled)
        {
            var minScale = MathF.Max(1f / targetSize.Width, 1f / targetSize.Height);
            scaleRender = MathF.Max(MathF.Max(minScale, ConfigurationV1.SizeConfig.MinScale), cfg.GameRenderScale);
        }
        else
        {
            scaleRender = !RestoreFromCurrentConfig && _original is { } original ? original.Scale : ConfigGraphicsRezoScale;
        }

        if (enabled || _wasEnabled)
        {
            gfx->GraphicsRezoScale = scaleRender;
            gfx->DynamicRezoEnable = (byte) (ConfigDynRezo || enabled ? 1 : 0);
            if (enabled)
            {
                gfx->GraphicsRezoUpscaleType = Service.Config._.ResolutionScalingMode.ToXIVGFX();
                rtm->DynamicResolutionMinimumHeight = rtm->DynamicResolutionMaximumHeight;
            }
            else
            {
                if (!RestoreFromCurrentConfig && _original is { } original)
                {
                    gfx->GraphicsRezoUpscaleType = original.Upscale;
                    gfx->DynamicRezoEnable = original.Dynamic;
                }
                else gfx->GraphicsRezoUpscaleType = ConfigGraphicsRezoType.ToXIVGFX();
                if (_original is { } saved && dev->Width == saved.W && dev->Height == saved.H)
                    rtm->DynamicResolutionMinimumHeight = saved.MinHeight;
            }
        }

        // Check if the RTM size didn't go back to the screen size when switching f.e. in / out of gpose.
        var wantsTexResize = false;
        GetScaledWidthHeight(targetSize.Width, targetSize.Height, scaleRender, out var renderWidth, out var renderHeight);
        if ((enabled || _wasEnabled) &&
            (rtm->_.Resolution_Width != renderWidth || rtm->_.Resolution_Height != renderHeight))
        {
            Service.PluginLog.Debug($"Regenerating dirty RTM - render mismatch - expected {renderWidth} x {renderHeight}, got {rtm->_.Resolution_Width} x {rtm->_.Resolution_Height}");
            wantsTexResize = true;
            if (enabled) gfx->DynamicRezoEnable = 1;
            gfx->GraphicsRezoScale = scaleRender;
        }

        CurrentRenderWidth = rtm->_.Resolution_Width;
        CurrentRenderHeight = rtm->_.Resolution_Height;

        // Check if the backing RTs are the expected size.
        var tex = rtm->GameplayTextureUnk3;
        var dlssResources = cfg.ResolutionScalingMode == ResolutionScalingMode.DLSS ||
            ConfigGraphicsRezoType == ResolutionScalingMode.DLSS || _original?.Upscale == 2;
        if (!dlssResources && (wantsTexResize || tex->AllocatedWidth != targetSize.Width || tex->AllocatedHeight != targetSize.Height) &&
            (unloading || !enabled || (!_pendingApply.Active && DateTime.UtcNow >= _nextResize)))
        {
            if (!unloading)
            {
                Service.PluginLog.Debug($"Regenerating dirty RTM - locking ImmediateContextDX11.ProcessCommands, expected target allocated size {targetSize.Width} x {targetSize.Height}, got {tex->AllocatedWidth} x {tex->AllocatedHeight}");
                using (_render.Unscoped.Lock.EnterWriteLock())
                {
                    Service.PluginLog.Debug($"Regenerating dirty RTM - locked ImmediateContextDX11.ProcessCommands");
                    var wasRequestingResolutionChange = dev->RequestResolutionChange;
                    try
                    {
                        dev->RequestResolutionChange = 1;
                        _render.Unscoped.ManualResizeDestroy();
                        _render.Unscoped.ManualResizeCreate();
                        _pendingApply = (0, true);
                        _nextResize = DateTime.UtcNow.AddMilliseconds(500);
                        rtm->GraphicsRezoScaleMain += 0.0001f;
                    }
                    finally { dev->RequestResolutionChange = wasRequestingResolutionChange; }
                }
            }
            else
            {
                dev->RequestResolutionChange = 1;
            }
        }

        CurrentTargetWidth = tex->AllocatedWidth;
        CurrentTargetHeight = tex->AllocatedHeight;
        _wasEnabled = enabled;
        if (!enabled) { _original = null; _pendingApply = default; }
    }

    private static void GetScaledWidthHeight(uint width, uint height, float scale, out uint widthS, out uint heightS)
    {
        heightS = Math.Max(1, (uint) MathF.Round(height * scale, MidpointRounding.AwayFromZero));
        widthS = Math.Max(1, (width * heightS) / Math.Max(1, height));
    }

    private unsafe void RenderTargetManagerApplyScalingDetour(RenderTargetManagerEx* rtm, uint* size, byte unk1)
    {
        Debug.Assert(rtm == RenderTargetManager.Instance());

        ref var cfg = ref Service.Config._;
        if (!UseFsrOverrides)
        {
            _renderTargetManagerApplyScalingHook.OriginalDisposeSafe(rtm, size, unk1);
            return;
        }

        var sizeNew = new GameTargetSize();
        using var scale = unk1 == 0 ? new DeviceScaleScope() : default;
        if (unk1 == 0)
        {
            Service.PluginLog.Debug($"Applying scaling, orig: {size[0]} {size[1]} {unk1}");
            size[0] = sizeNew.Width;
            size[1] = sizeNew.Height;
            _pendingApply.Active = false;
        }

        var gfx = (GraphicsConfigEx*) GraphicsConfig.Instance();
        var _DynamicRezoEnableBeyond1 = gfx->DynamicRezoEnableBeyond1;
        // gfx->DynamicRezoEnableBeyond1 = (byte) ((sizeNew.ScaleX != 1f || sizeNew.ScaleY != 1f) ? 1 : 0);

        Service.PluginLog.Debug($"Applying scaling, before: {size[0]} {size[1]} {unk1}");
        _renderTargetManagerApplyScalingHook.OriginalDisposeSafe(rtm, size, unk1);
        Service.PluginLog.Debug($"Applying scaling, after: {size[0]} {size[1]} {unk1}");

        // gfx->DynamicRezoEnableBeyond1 = _DynamicRezoEnableBeyond1;
    }

    private unsafe void RenderTargetManagerUpdateDetour(RenderTargetManagerEx* rtm, float deltaTime)
    {
        Debug.Assert(rtm == RenderTargetManager.Instance());

        ref var cfg = ref Service.Config._;
        if (!UseFsrOverrides)
        {
            _renderTargetManagerUpdate.OriginalDisposeSafe(rtm, deltaTime);
            return;
        }

        _renderTargetManagerUpdate.OriginalDisposeSafe(rtm, deltaTime);

        if (_pendingApply.Active && _pendingApply.Time < _pendingApplyTimeout && ++_pendingApply.Time >= _pendingApplyTimeout)
        {
            Service.PluginLog.Debug("Pending scaling apply timed out");
            _pendingApply.Active = false;
        }
    }

    private unsafe byte RTMDestroyAfterResizeDetour(RenderEvents.ResizeDestroyContext ctx)
    {
        ref var cfg = ref Service.Config._.GameTarget;
        if (!UseFsrOverrides)
        {
            return ctx.Invoke();
        }

        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();

        Service.PluginLog.Debug($"Destroying RTM resources");
        var rv = ctx.Invoke();
        Service.PluginLog.Debug($"After: {rv} 0x{(long) (nint) rtm->GameplayTextureUnk1->D3D11Texture2D:X16} 0x{(long) (nint) rtm->GameplayTextureUnk3->D3D11Texture2D:X16}");
        return rv;
    }

    private unsafe void RTMRegenAfterResizeDetour(RenderEvents.ResizeCreateContext ctx)
    {
        ref var cfg = ref Service.Config._.GameTarget;
        if (!UseFsrOverrides)
        {
            ctx.Invoke();
            return;
        }

        var dev = (DeviceEx*) Device.Instance();
        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();

        using var scale = new DeviceScaleScope();
        Service.PluginLog.Debug($"Regenerating RTM resources: {dev->Width} x {dev->Height}");
        ctx.Invoke();
        Service.PluginLog.Debug($"After D3D11 handles: 0x{(long) (nint) rtm->GameplayTextureUnk1->D3D11Texture2D:X16} 0x{(long) (nint) rtm->GameplayTextureUnk3->D3D11Texture2D:X16}");
        Service.PluginLog.Debug($"After size: {rtm->GameplayTextureUnk3->AllocatedWidth} {rtm->GameplayTextureUnk3->AllocatedHeight}");
    }

    private void TaskRenderGraphicsRenderDetour(RenderEvents.TaskRenderGraphicsRenderContext ctx)
    {
        ref var cfg = ref Service.Config._.GameTarget;
        if (!UseFsrOverrides)
        {
            ctx.Invoke();
            return;
        }

        using var scale = new DeviceScaleScope();
        ctx.Invoke();
    }

    private unsafe ulong PrepareTextureDetour(Texture* tex, uint* size, byte mips, uint format)
    {
        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();
        if (tex != rtm->GameplayTextureUnk1 && tex != rtm->GameplayTextureUnk3)
        {
            return _prepareTextureHook.OriginalDisposeSafe(tex, size, mips, format);
        }

        ref var cfg = ref Service.Config._;

        if (UseFsrOverrides)
        {
            var sizeNew = new GameTargetSize();
            mips = Math.Min(mips, sizeNew.Mips);
        }

        Service.PluginLog.Debug($"Preparing gameplay texture 0x{(long) (nint) tex:X16} with {mips} mips, currently 0x{(long) (nint) tex->D3D11Texture2D:X16}");
        var rv = _prepareTextureHook.OriginalDisposeSafe(tex, size, mips, format);
        Service.PluginLog.Debug($"After: 0x{rv:X16} 0x{(long) (nint) tex->D3D11Texture2D:X16} 0x{(long) (nint) tex->D3D11ShaderResourceView:X16}");
        return rv;
    }

    private unsafe void ImmediateBindPSSRVsDetour(ImmediateContextEx* im, PixelShader* ps, ImmediateSRV* data)
    {
        _immediateBindPSSRVsHook.OriginalDisposeSafe(im, ps, data);
        PostImmediateBindSRVs(false, im, ps, data);
    }

    private unsafe void ImmediateBindCSSRVsDetour(ImmediateContextEx* im, Shader* cs, ImmediateSRV* data)
    {
        _immediateBindCSSRVsHook.OriginalDisposeSafe(im, cs, data);
        PostImmediateBindSRVs(true, im, (PixelShader*) cs, data);
    }

    private unsafe void PostImmediateBindSRVs(
        bool isCS,
        ImmediateContextEx* im,
        PixelShader* ps,
        ImmediateSRV* data
    )
    {
        if (!UseFsrOverrides) return;
        Debug.Assert(im == Device.Instance()->ImmediateContext);

        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();
        ref var cfg = ref Service.Config._;

        // TODO: Identifying by the shader might be much more reliable long-term.
        if (ps->SamplerCount >= 1 && (data[0].Texture == rtm->GameplayTextureUnk1 || data[0].Texture == rtm->GameplayTextureUnk3))
        {
            // Service.PluginLog.Debug($"ImmediateBind{(isPS ? "PS" : "CS")}SRVs #{_drawCount} 0x{(long) (nint) ps}");
            var tex = data[0].Texture;

            ID3D11SamplerState* sampler = null;

            if (cfg.GameTarget.IsEnabled && cfg.ResolutionScalingMode == ResolutionScalingMode.Point)
            {
                sampler = _samplerPoint;
            }
            else if (tex->MipLevel != 1)
            {
                sampler = _samplerMips;
                _d3dctx.Get()->GenerateMips((ID3D11ShaderResourceView*) tex->D3D11ShaderResourceView);
            }
            else if (cfg.GameTarget.IsEnabled && cfg.ResolutionScalingMode == ResolutionScalingMode.Linear)
            {
                sampler = _samplerLinear;
            }

            if (sampler is not null)
            {
                if (isCS)
                {
                    _d3dctx.Get()->CSSetSamplers(0, 1, &sampler);
                }
                _d3dctx.Get()->PSSetSamplers(0, 1, &sampler);
            }
        }
    }

    private unsafe void FsrPushBlitCommandsDetour(PostEffectManagerEx.FsrData* fsr, Texture* swapchain, Context* ctx)
    {
        if (!UseFsrOverrides)
        {
            _fsrPushBlitCommandsHook.OriginalDisposeSafe(fsr, swapchain, ctx);
            return;
        }
        var srcInd = &fsr->Unk28;
        var src = *srcInd;
        if (src == swapchain || (src->AllocatedWidth == swapchain->AllocatedWidth && src->AllocatedHeight == swapchain->AllocatedHeight))
        {
            _fsrPushBlitCommandsHook.OriginalDisposeSafe(fsr, swapchain, ctx);
            return;
        }

        var vp = (RenderCommandViewport*) ctx->AllocateCommand((ulong) sizeof(RenderCommandViewport));
        if (vp != null)
        {
            *vp = new();
            vp->Type = RenderCommandType.Viewport;
            vp->ViewportRect = new() {
                Left = 0,
                Top = 0,
                Right = (int) swapchain->ActualWidth,
                Bottom = (int) swapchain->ActualHeight
            };
            vp->MinDepth = 0f;
            vp->MaxDepth = 1f;

            ctx->PushBackCommand(vp);
        }

        // Original calls this, yes, even with src not swapchain.
        ctx->SetRenderTargets(1, srcInd, null, 0, 0, 0, 0);

        // Original pushes CopyTextureRegionFast
        var copy = (RenderCommandCopyTextureRegion*) ctx->AllocateCommand((ulong) sizeof(RenderCommandCopyTextureRegion));
        if (copy != null)
        {
            *copy = new();

            copy->Type = RenderCommandTypeEx.CopyTextureRegionSlow;
            copy->SlotATexture = src;
            copy->SlotAUnkFlags = 0;
            copy->SlotARect = &copy->RectInline1;
            copy->RectInline1.Right = (int) src->AllocatedWidth;
            copy->RectInline1.Bottom = (int) src->AllocatedHeight;
            copy->SlotBTexture = swapchain;
            copy->SlotBUnkFlags = 0x80000000;
            copy->SlotBRect = &copy->RectInline2;
            copy->RectInline2.Right = (int) swapchain->AllocatedWidth;
            copy->RectInline2.Bottom = (int) swapchain->AllocatedHeight;

            ctx->PushBackCommand(copy);
        }
    }

    private unsafe HRESULT CreateTexture2DDetour(ID3D11Device* d3ddev, D3D11_TEXTURE2D_DESC* desc, D3D11_SUBRESOURCE_DATA* initialData, ID3D11Texture2D** tex)
    {
        ref var cfg = ref Service.Config._;
        if (!UseFsrOverrides)
        {
            return _createTexture2DHook.OriginalDisposeSafe(d3ddev, desc, initialData, tex);
        }

        var rtm = (RenderTargetManagerEx*) RenderTargetManager.Instance();
        if ((tex == &rtm->GameplayTextureUnk1->D3D11Texture2D || tex == &rtm->GameplayTextureUnk3->D3D11Texture2D) &&
            desc->MipLevels != 1)
        {
            desc->MiscFlags |= (uint) D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS;
        }

        var rv = _createTexture2DHook.OriginalDisposeSafe(d3ddev, desc, initialData, tex);

        if (rv.FAILED)
        {
            Service.PluginLog.Error($"CreateTexture2D(*0x{(long) (nint) tex:X16}, {(desc == null ? "?" : desc->ToDebugString())}) failed: 0x{rv.Value:X8}");
        }

        return rv;
    }

    public readonly struct GameTargetSize
    {
        public readonly uint Width;
        public readonly uint Height;
        public readonly float ScaleX;
        public readonly float ScaleY;

        public unsafe GameTargetSize(ConfigurationV1.SizeConfig cfg)
        {
            var dev = (DeviceEx*) Device.Instance();
            if (!cfg.IsEnabled || FService.Unloading)
            {
                Width = dev->Width;
                Height = dev->Height;
                ScaleX = ScaleY = 1f;
            }
            else if (cfg.IsScale)
            {
                var minScale = MathF.Max(MinSize / (float) dev->Width, MinSize / (float) dev->Height);
                var scale = MathF.Max(minScale, cfg.Scale);
                GetScaledWidthHeight(dev->Width, dev->Height, scale, out Width, out Height);
                ScaleX = ScaleY = scale;
            }
            else
            {
                Width = Math.Max(MinSize, cfg.Width);
                Height = Math.Max(MinSize, cfg.Height);
                ScaleX = Width / (float) dev->Width;
                ScaleY = Height / (float) dev->Height;
            }
        }

        public GameTargetSize() : this(Service.Config._.GameTarget)
        {
        }

        public float ScaleMin => MathF.Min(ScaleX, ScaleY);
        public float ScaleMax => MathF.Max(ScaleX, ScaleY);

        public byte Mips
        {
            get
            {
                var scale = ScaleMax;
                if (scale > 2f)
                {
                    return (byte) MathF.Ceiling(MathF.Log2(scale));
                }
                else
                {
                    return 1;
                }
            }
        }
    }

    private readonly struct DeviceScaleScope : IDisposable
    {
        private readonly unsafe DeviceEx* _dev;
        public readonly uint OrigWidth;
        public readonly uint OrigHeight;
        public readonly GameTargetSize Size;

        public unsafe DeviceScaleScope()
        {
            _dev = (DeviceEx*) Device.Instance();
            OrigWidth = _dev->Width;
            OrigHeight = _dev->Height;

            Size = new GameTargetSize();

            _dev->Width = Size.Width;
            _dev->Height = Size.Height;
        }

        public readonly unsafe void Dispose()
        {
            if (_dev != null)
            {
                _dev->Width = OrigWidth;
                _dev->Height = OrigHeight;
            }
        }
    }

    private static class Sigs
    {
        /* Called on resize when recalculating RTM Resolution_Width and Height (after first being reset to dev size).
         */
        public static readonly string RenderTargetManagerApplyScaling = new GhidraCode(@"
       1402db350 48 89 5c        MOV        qword ptr [RSP + local_res18],RBX
                 24 18
       1402db355 55              PUSH       RBP
       1402db356 56              PUSH       RSI
       1402db357 57              PUSH       RDI
       1402db358 41 56           PUSH       R14
       1402db35a 41 57           PUSH       R15
       1402db35c 48 83 ec 20     SUB        RSP,0x20
       1402db360 8b 42 04        MOV        EAX,dword ptr [RDX + 0x4]
       1402db363 0f 57 c0        XORPS      XMM0,XMM0
       1402db366 48 8b d9        MOV        RBX,RCX
       1402db369 48 8b fa        MOV        RDI,RDX
       1402db36c 48 8b 0d        MOV        RCX,qword ptr [g_Client::Graphics::Render::Gra
                 ?? ?? ?? ??
       1402db373 45 33 f6        XOR        R14D,R14D
       1402db376 45 0f b6 f8     MOVZX      R15D,R8B
       1402db37a f3 48 0f        CVTSI2SS   XMM0,RAX
                 2a c0
       1402db37f b8 ab aa        MOV        EAX,0xaaaaaaab
                 aa aa
       1402db384 f3 0f 59        MULSS      XMM0,dword ptr [RCX + 0x4c]
                 41 4c
       1402db389 f3 0f 58        ADDSS      XMM0,dword ptr [g_FloatHalf]                     = 3F000000h
                 05 ?? ??
                 ?? ??
       1402db391 f3 48 0f        CVTTSS2SI  RSI,XMM0
                 2c f0
       1402db396 44 8d 0c 36     LEA        R9D,[RSI + RSI*0x1]
       1402db39a 41 f7 e1        MUL        R9D
       1402db39d 48 8b 83        MOV        RAX,qword ptr [RBX + 0x730]
                 30 07 00 00
").Clean;

        /* Called in TaskUpdateGraphicsRender. */
        public static readonly string RenderTargetManagerUpdate = new GhidraCode(@"
       1402da250 4c 8b dc        MOV        R11,RSP
       1402da253 57              PUSH       RDI
       1402da254 48 81 ec        SUB        RSP,0xd0
                 d0 00 00 00
       1402da25b 48 8b 05        MOV        RAX,qword ptr [g_StackCookie]                    = 00002B992DDFA232h
                 ?? ?? ?? ??
       1402da262 48 33 c4        XOR        RAX,RSP
       1402da265 48 89 44        MOV        qword ptr [RSP + local_78],RAX
                 24 60
       1402da26a f3 0f 10        MOVSS      XMM0,dword ptr [RCX + 0x710]
                 81 10 07
                 00 00
       1402da272 0f 57 d2        XORPS      XMM2,XMM2
       1402da275 f3 0f 59        MULSS      XMM0,dword ptr [DAT_142142850]                   = BCh
                 05 ?? ??
                 ?? ??
       1402da27d 0f 28 d9        MOVAPS     XMM3,XMM1
       1402da280 49 89 5b 10     MOV        qword ptr [R11 + local_res10],RBX
       1402da284 48 8b d9        MOV        RBX,RCX
       1402da287 49 89 6b 18     MOV        qword ptr [R11 + local_res18],RBP
       1402da28b 0f 57 ed        XORPS      XMM5,XMM5
       1402da28e 4d 89 73 e8     MOV        qword ptr [R11 + local_18],R14
       1402da292 4c 8b 35        MOV        R14,qword ptr [g_Client::Graphics::Kernel::Dev
                 ?? ?? ?? ??
").Clean;

        /* Huge thanks to Wintermute for finding this one! "builds a copytextureregion iirc for fsr shenanifuckeries"
         * DeviceDX11::PostTick calls InitSwapChain, responsible for calling IDXGISwapChain.ResizeBuffer and and GetBuffer.
         * There are multiple call sites, this is just one of them. All are gated behind a RequestResolutionChange check.
         */
        public static readonly string FsrPushBlitCommands = new GhidraCode(@"
       140372b40 48 89 5c        MOV        qword ptr [RSP + local_res10],RBX
                 24 10
       140372b45 48 89 74        MOV        qword ptr [RSP + local_res18],RSI
                 24 18
       140372b4a 57              PUSH       RDI
       140372b4b 48 83 ec 50     SUB        RSP,0x50
       140372b4f 48 8d 79 28     LEA        RDI,[RCX + 0x28]
       140372b53 49 8b d8        MOV        RBX,R8
       140372b56 48 8b 0f        MOV        RCX,qword ptr [RDI]
       140372b59 48 8b f2        MOV        RSI,RDX
       140372b5c 48 3b ca        CMP        RCX,RDX
       140372b5f 0f 84 d9        JZ         LAB_140372c3e
                 00 00 00
       140372b65 8b 41 38        MOV        EAX,dword ptr [RCX + 0x38]
       140372b68 89 44 24 48     MOV        dword ptr [RSP + local_18[8]],EAX
       140372b6c 8b 41 3c        MOV        EAX,dword ptr [RCX + 0x3c]
       140372b6f 48 8b cb        MOV        RCX,RBX
       140372b72 48 89 6c        MOV        qword ptr [RSP + local_res8],RBP
                 24 60
       140372b77 33 ed           XOR        EBP,EBP
       140372b79 48 89 6c        MOV        qword ptr [RSP + local_18[0]],RBP
                 24 40
       140372b7e 89 44 24 4c     MOV        dword ptr [RSP + local_18[12]],EAX
       140372b82 8d 55 1c        LEA        EDX,[RBP + 0x1c]
       140372b85 e8 ?? ??        CALL       Client::Graphics::Kernel::Context.AllocateComm   undefined Context.AllocateComman
                 ?? ??
       140372b8a 48 85 c0        TEST       RAX,RAX
       140372b8d 74 24           JZ         LAB_140372bb3
       140372b8f 0f 10 44        MOVUPS     XMM0,xmmword ptr [RSP + local_18[0]]
                 24 40
       140372b94 c7 00 01        MOV        dword ptr [RAX],0x1
                 00 00 00
       140372b9a 48 8b d0        MOV        RDX,RAX
       140372b9d 48 8b cb        MOV        RCX,RBX
       140372ba0 89 68 14        MOV        dword ptr [RAX + 0x14],EBP
       140372ba3 0f 11 40 04     MOVUPS     xmmword ptr [RAX + 0x4],XMM0
       140372ba7 c7 40 18        MOV        dword ptr [RAX + 0x18],0x3f800000
                 00 00 80 3f
       140372bae e8 ?? ??        CALL       Client::Graphics::Kernel::Context.PushBackComm   undefined Context.PushBackComman
                 ?? ??
       140372bb3 89 6c 24 38     MOV        dword ptr [RSP + local_20],EBP
       140372bb7 45 33 c9        XOR        R9D,R9D
       140372bba 89 6c 24 30     MOV        dword ptr [RSP + local_28],EBP
       140372bbe 4c 8b c7        MOV        R8,RDI
       140372bc1 89 6c 24 28     MOV        dword ptr [RSP + local_30],EBP
       140372bc5 48 8b cb        MOV        RCX,RBX
       140372bc8 89 6c 24 20     MOV        dword ptr [RSP + local_38],EBP
       140372bcc 41 8d 51 01     LEA        EDX,[R9 + 0x1]
       140372bd0 e8 ?? ??        CALL       Client::Graphics::Kernel::Context.SetRenderTar   undefined Context.SetRenderTarge
                 ?? ??
       140372bd5 48 8b 3f        MOV        RDI,qword ptr [RDI]
       140372bd8 ba 78 00        MOV        EDX,0x78
                 00 00
       140372bdd 48 8b cb        MOV        RCX,RBX
       140372be0 e8 ?? ??        CALL       Client::Graphics::Kernel::Context.AllocateComm   undefined Context.AllocateComman
                 ?? ??
       140372be5 48 8b d0        MOV        RDX,RAX
       140372be8 48 85 c0        TEST       RAX,RAX
       140372beb 74 4c           JZ         LAB_140372c39
       140372bed 0f 10 44        MOVUPS     XMM0,xmmword ptr [RSP + local_18[0]]
                 24 40
       140372bf2 c7 00 0b        MOV        dword ptr [RAX],0xb
                 00 00 00
       140372bf8 48 8d 48 38     LEA        RCX,[RAX + 0x38]
       140372bfc 48 89 78 08     MOV        qword ptr [RAX + 0x8],RDI
       140372c00 0f 11 01        MOVUPS     xmmword ptr [RCX],XMM0
       140372c03 48 89 68 10     MOV        qword ptr [RAX + 0x10],RBP
       140372c07 89 68 18        MOV        dword ptr [RAX + 0x18],EBP
       140372c0a 48 89 70 20     MOV        qword ptr [RAX + 0x20],RSI
       140372c0e 89 68 28        MOV        dword ptr [RAX + 0x28],EBP
       140372c11 48 89 48 30     MOV        qword ptr [RAX + 0x30],RCX
       140372c15 48 b9 00        MOV        RCX,-0x100002000
                 e0 ff ff
                 fe ff ff ff
       140372c1f 48 89 68 48     MOV        qword ptr [RAX + 0x48],RBP
       140372c23 48 8b 05        MOV        RAX,qword ptr [DAT_142899498]                    = FFFF00001C000000h
                 ?? ?? ?? ??
       140372c2a 48 23 c1        AND        RAX,RCX
       140372c2d 48 8b cb        MOV        RCX,RBX
       140372c30 48 89 42 50     MOV        qword ptr [RDX + 0x50],RAX
       140372c34 e8 ?? ??        CALL       Client::Graphics::Kernel::Context.PushBackComm   undefined Context.PushBackComman
                 ?? ??
       140372c39 48 8b 6c        MOV        RBP,qword ptr [RSP + local_res8]
                 24 60
       140372c3e 48 8b 5c        MOV        RBX,qword ptr [RSP + local_res10]
                 24 68
       140372c43 48 8b 74        MOV        RSI,qword ptr [RSP + local_res18]
                 24 70
       140372c48 48 83 c4 50     ADD        RSP,0x50
       140372c4c 5f              POP        RDI
       140372c4d c3              RET
").Clean;
    }
}
