using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FloppyUtils;

namespace RenderScale;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly PreviewSession preview = new();
    private bool initialized;
    private bool open;
    private float inputPercent = 100;
    private uint width, height, renderWidth, renderHeight;
    private string status = "已加载，尚未启用。";
    private string? blocked;
    private bool usingDlss;
    private uint originalSavedScale, originalSavedDynamic, originalSavedUpscaler;
    public bool IsReady { get; private set; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();
        Service.Config = pluginInterface.GetPluginConfig() as Configuration ?? new();
        Service.Config.Runtime = new();
        inputPercent = float.IsFinite(Service.Config.Percent) ? Math.Clamp(Service.Config.Percent, 25, 400) : 100;
        Service.CommandManager.AddHandler("/swr", new CommandInfo(OnCommand)
        { HelpMessage = "渲染比例：/swr 打开；/swr 150 预览 150%；/swr keep 保留；/swr off 恢复。" });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        blocked = Compatibility.Check();
        if (blocked is not null) return;
        try
        {
            initialized = FService.TryInit(this, pluginInterface);
            if (!initialized) { blocked = "渲染库初始化失败或存在重复插件，请查看卫月日志。"; return; }
            FService.Create<Service>();
        }
        catch (Exception ex)
        {
            blocked = "渲染接口初始化失败，未启用比例覆盖。请查看卫月日志。";
            Service.PluginLog.Error(ex, "RenderScale initialization failed");
        }
    }

    public async Task LoadAsync(CancellationToken cancel)
    {
        if (!initialized || blocked is not null) return;
        try
        {
            await FService.BeginLoadAsync(cancel);
            await FService.UpdateThread.Run(() =>
            {
                Service.Framework.Update += Update;
                IsReady = true;
            });
        }
        catch (Exception ex)
        {
            blocked = "渲染接口加载失败，未启用比例覆盖。";
            Service.PluginLog.Error(ex, "RenderScale load failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The backend must see disabled target sizing while restoring / disposing.
        // Otherwise a >100% target could remain allocated after hooks are removed.
        if (initialized)
            await FService.UpdateThread.Run(() => Stop("插件卸载，已请求恢复。"));
        Service.PluginInterface.UiBuilder.Draw -= Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi -= Open;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Service.CommandManager.RemoveHandler("/swr");
        Service.Framework.Update -= Update;
        IsReady = false;
        if (initialized) await FService.BeginDisposeAsync();
    }

    private void Open() => open = true;
    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (args.Length == 0) { Open(); return; }
        if (args.Equals("off", StringComparison.OrdinalIgnoreCase)) { actions.Enqueue(() => Stop("已停止覆盖，已请求恢复原值；请核对实际尺寸。")); return; }
        if (args.Equals("keep", StringComparison.OrdinalIgnoreCase)) { actions.Enqueue(Keep); return; }
        if (float.TryParse(args.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            inputPercent = percent;
            actions.Enqueue(() => Apply(percent));
            Open();
        }
        else Service.ChatGui.PrintError("用法：/swr 150、/swr keep 或 /swr off。数值表示百分比。");
    }

    private unsafe void Update(IFramework framework)
    {
        if (!IsReady) return;
        var device = Device.Instance();
        if (device == null) return;
        width = device->Width;
        height = device->Height;
        renderWidth = Service.GameSize.CurrentRenderWidth;
        renderHeight = Service.GameSize.CurrentRenderHeight;
        if (preview.Active)
        {
            var config = Service.GameConfig.System;
            if (config.GetUInt("GraphicsRezoScale") != originalSavedScale ||
                config.GetUInt("DynamicRezoType") != originalSavedDynamic ||
                config.GetUInt("GraphicsRezoUpscaleType") != originalSavedUpscaler)
            {
                Service.GameSize.RestoreFromCurrentConfig = true;
                Stop("游戏渲染设置已变化，停止覆盖并采用新设置。");
            }
            else if (usingDlss && Service.GameSize.Dlss.Read(Environment.TickCount64).ShouldStop)
                Stop("未持续检测到 DLSS 成功调用，已请求恢复。请检查游戏 DLSS 设置。最近返回码见下方。");
            else if (preview.Check(DateTimeOffset.UtcNow, width, height, renderWidth, renderHeight) is { } reason)
                Stop(reason);
        }
        while (actions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                Stop("操作失败，已请求恢复，请查看卫月日志。");
                Service.PluginLog.Error(ex, "RenderScale operation failed");
            }
        }
    }

    private void Apply(float percent)
    {
        if (!IsReady || blocked is not null) return;
        if (Service.GameSize.IsRestoring) { status = "正在恢复上一组参数，请稍后重试。"; return; }
        if (!ScalePlan.TryCreate(width, height, percent, out var plan, out var error)) { status = error; return; }
        var conflicts = Service.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded &&
            p.InternalName != "RenderScale" && (p.InternalName.Contains("CustomResolution", StringComparison.OrdinalIgnoreCase) || p.InternalName == "RezoPlugin"));
        if (conflicts) { status = "请先停用 CustomResolution / RezoPlugin，避免两者同时控制渲染尺寸。"; return; }
        var config = Service.GameConfig.System;
        var savedUpscaler = config.GetUInt("GraphicsRezoUpscaleType");
        if (savedUpscaler > 1) { status = "无法识别当前缩放器，未启用。"; return; }
        var dlss = savedUpscaler == 1;
        if (dlss && !DlssSession.Supports(percent))
        {
            status = "DLSS 测试路径只开放 25–100%。高于 100% 所需的 DLSS 输出重建与最终缩小尚未实现，不会自动切换 FSR。";
            return;
        }
        if (dlss && (!Service.GameSize.HasDlssBackend || !Service.GameSize.CanObserveDlss))
        {
            status = "未找到可检测的游戏 DLSS 接口。请先在游戏设置中启用 DLSS，再重新加载此插件。";
            return;
        }
        originalSavedScale = config.GetUInt("GraphicsRezoScale");
        originalSavedDynamic = config.GetUInt("DynamicRezoType");
        originalSavedUpscaler = config.GetUInt("GraphicsRezoUpscaleType");
        ref var runtime = ref Service.Config._;
        runtime.GameTarget.IsScale = true;
        runtime.GameTarget.Scale = Math.Max(1, percent / 100f);
        runtime.GameRenderScale = Math.Min(1, percent / 100f);
        runtime.ResolutionScalingMode = dlss ? ResolutionScalingMode.DLSS : ResolutionScalingMode.FSR;
        runtime.GameTarget.IsEnabled = true;
        usingDlss = dlss;
        if (dlss) Service.GameSize.Dlss.Begin(Environment.TickCount64);
        else Service.GameSize.Dlss.Stop();
        preview.Begin(plan, DateTimeOffset.UtcNow);
        status = "正在预览，请检查画面、HUD 与点击位置；15 秒内确认保留。";
        Service.PluginLog.Information("RenderScale preview {Mode} {Percent}%: output {W}x{H}, target {RW}x{RH}", dlss ? "DLSS" : "FSR", percent, width, height, plan.RenderWidth, plan.RenderHeight);
    }

    private void Keep()
    {
        if (preview.Deadline is { } end && DateTimeOffset.UtcNow >= end) { Stop("预览已过期，已请求恢复。"); return; }
        if (usingDlss && !Service.GameSize.Dlss.Read(Environment.TickCount64).Healthy)
        { status = "尚未持续检测到 DLSS 成功调用，不能确认。"; return; }
        if (!preview.Confirm(renderWidth, renderHeight)) { status = "实际渲染尺寸尚未达到目标，不能确认。"; return; }
        Service.Config.Percent = preview.Plan.Percent;
        Service.PluginInterface.SavePluginConfig(Service.Config);
        status = "已保留本次比例；下次加载仍默认关闭。";
    }

    internal void Stop(string reason)
    {
        if (preview.Active && IsReady)
            Service.PluginLog.Information("RenderScale stopped: {Reason}; observed {W}x{H}; DLSS result 0x{Result:X8}",
                reason, renderWidth, renderHeight, Service.GameSize.Dlss.Read(Environment.TickCount64).LastResult);
        Service.Config.Runtime.GameTarget.IsEnabled = false;
        preview.Stop();
        if (IsReady) Service.GameSize.Dlss.Stop();
        status = reason;
    }

    private void Draw()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new Vector2(620, 530), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("斯温·渲染比例（测试版）###RenderScale", ref open)) { ImGui.End(); return; }
        ImGui.TextWrapped("只调整 3D 场景比例，窗口与 HUD 保持原尺寸。画面、帧率及显存占用会随比例变化。");
        ImGui.Separator();
        if (blocked is not null)
        {
            ImGui.TextWrapped(blocked);
            ImGui.TextWrapped("已停止渲染接口初始化；不会尝试写入未知版本的游戏内存。");
            ImGui.End();
            return;
        }
        ImGui.Text($"输出尺寸：{width} × {height}");
        ImGui.Text($"实际 3D 渲染：{renderWidth} × {renderHeight}");
        var selectedDlss = Service.GameConfig.System.GetUInt("GraphicsRezoUpscaleType") == 1;
        ImGui.Text(selectedDlss ? "游戏当前选择：DLSS（实验路径 25–100%）" : "游戏当前选择：FSR（实验路径 25–400%）");
        if (IsReady && selectedDlss)
        {
            var sample = Service.GameSize.Dlss.Read(Environment.TickCount64);
            ImGui.Text(sample.Healthy ? "DLSS：近期有连续成功调用（仍需检查画面）" : "DLSS：尚未确认近期连续成功调用");
            ImGui.Text($"本次预览最近 NGX 返回码：0x{sample.LastResult:X8}（0 表示尚无记录）");
        }
        if (ScalePlan.TryCreate(width, height, inputPercent, out var plan, out var error))
            ImGui.Text($"目标：{plan.RenderWidth} × {plan.RenderHeight}（像素数约 {Math.Pow(inputPercent / 100, 2):0.00} 倍）");
        else ImGui.TextWrapped(error);
        ImGui.SetNextItemWidth(260);
        ImGui.InputFloat("比例 %", ref inputPercent, 1, 10, "%.2f");
        foreach (var percent in selectedDlss ? new[] { 25, 50, 58, 67, 75, 100 } : new[] { 50, 75, 100, 125, 150, 200 })
        {
            if (ImGui.SmallButton($"{percent}%")) inputPercent = percent;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.BeginDisabled(!IsReady);
        if (ImGui.Button("预览 15 秒")) { var value = inputPercent; actions.Enqueue(() => Apply(value)); }
        ImGui.SameLine();
        if (ImGui.Button("停止并恢复")) actions.Enqueue(() => Stop("已停止覆盖，已请求恢复原值；请核对实际尺寸。"));
        ImGui.EndDisabled();
        if (preview.Deadline is { } deadline)
        {
            ImGui.Text($"剩余 {Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalSeconds):0} 秒");
            if (ImGui.Button("画面正常，保留本次比例")) actions.Enqueue(Keep);
        }
        ImGui.Separator();
        ImGui.TextWrapped(status);
        ImGui.TextWrapped("跟随游戏已选择的缩放器，不自动切换。固定比例暂时接管动态分辨率；关闭后恢复。DLSS 不重建输出纹理，实际输入比例能否被游戏接受须实测。");
        ImGui.TextWrapped("尚未完成游戏内验收。DLSS 首次从 67% 开始，并将游戏的 DLSS 应用条件设为始终启用。100% 不代表已确认 DLAA；高于 100% 暂未实现。紧急恢复：/swr off。");
        ImGui.End();
    }
}
