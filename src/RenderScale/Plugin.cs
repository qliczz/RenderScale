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
        if (!ScalePlan.TryCreate(width, height, percent, out var plan, out var error)) { status = error; return; }
        var conflicts = Service.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded &&
            p.InternalName != "RenderScale" && (p.InternalName.Contains("CustomResolution", StringComparison.OrdinalIgnoreCase) || p.InternalName == "RezoPlugin"));
        if (conflicts) { status = "请先停用 CustomResolution / RezoPlugin，避免两者同时控制渲染尺寸。"; return; }
        var config = Service.GameConfig.System;
        if (config.GetUInt("GraphicsRezoUpscaleType") != 0)
        {
            status = "此测试版要求游戏当前使用 AMD FSR；不会替你切换 DLSS 或修改保存的画质设置。";
            return;
        }
        originalSavedScale = config.GetUInt("GraphicsRezoScale");
        originalSavedDynamic = config.GetUInt("DynamicRezoType");
        originalSavedUpscaler = config.GetUInt("GraphicsRezoUpscaleType");
        ref var runtime = ref Service.Config._;
        runtime.GameTarget.IsScale = true;
        runtime.GameTarget.Scale = Math.Max(1, percent / 100f);
        runtime.GameRenderScale = Math.Min(1, percent / 100f);
        runtime.ResolutionScalingMode = ResolutionScalingMode.FSR;
        runtime.GameTarget.IsEnabled = true;
        preview.Begin(plan, DateTimeOffset.UtcNow);
        status = "正在预览，请检查画面、HUD 与点击位置；15 秒内确认保留。";
        Service.PluginLog.Information("RenderScale preview {Percent}%: output {W}x{H}, target {RW}x{RH}", percent, width, height, plan.RenderWidth, plan.RenderHeight);
    }

    private void Keep()
    {
        if (preview.Deadline is { } end && DateTimeOffset.UtcNow >= end) { Stop("预览已过期，已请求恢复。"); return; }
        if (!preview.Confirm(renderWidth, renderHeight)) { status = "实际渲染尺寸尚未达到目标，不能确认。"; return; }
        Service.Config.Percent = preview.Plan.Percent;
        Service.PluginInterface.SavePluginConfig(Service.Config);
        status = "已保留本次比例；下次加载仍默认关闭。";
    }

    internal void Stop(string reason)
    {
        Service.Config.Runtime.GameTarget.IsEnabled = false;
        preview.Stop();
        status = reason;
    }

    private void Draw()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new Vector2(570, 440), ImGuiCond.FirstUseEver);
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
        if (ScalePlan.TryCreate(width, height, inputPercent, out var plan, out var error))
            ImGui.Text($"目标：{plan.RenderWidth} × {plan.RenderHeight}（像素数约 {Math.Pow(inputPercent / 100, 2):0.00} 倍）");
        else ImGui.TextWrapped(error);
        ImGui.SetNextItemWidth(260);
        ImGui.InputFloat("比例 %", ref inputPercent, 1, 10, "%.2f");
        foreach (var percent in new[] { 50, 75, 100, 125, 150, 200 })
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
        ImGui.TextWrapped("测试范围：25–400%，每边最多 8192。固定比例会暂时接管动态分辨率；关闭后恢复。此版仅支持游戏已选择 FSR 的情况。");
        ImGui.TextWrapped("当前尚未完成游戏内验收。首次请从 75% 和 125% 开始测试。紧急恢复：/swr off。");
        ImGui.End();
    }
}
