namespace RenderScale;

public readonly record struct ScalePlan(uint OutputWidth, uint OutputHeight, uint RenderWidth, uint RenderHeight, float Percent)
{
    public const float MinPercent = 25;
    public const float MaxPercent = 400;
    public const uint MaxDimension = 8192;
    public const ulong MaxPixels = 33_554_432;

    public static bool TryCreate(uint width, uint height, float percent, out ScalePlan plan, out string error)
    {
        plan = default;
        error = "";
        if (!float.IsFinite(percent) || percent < MinPercent || percent > MaxPercent)
            error = "请输入 25–400% 的有限数值。";
        else if (width < 64 || height < 64 || width > MaxDimension || height > MaxDimension)
            error = "当前输出尺寸无效或超出测试版范围。";
        else
        {
            // Matches the game's height-first rounding while retaining the output aspect ratio.
            var h = (ulong)Math.Round(height * (double)(percent / 100f), MidpointRounding.AwayFromZero);
            var w = width * h / height;
            if (w < 64 || h < 64 || w > MaxDimension || h > MaxDimension || w * h > MaxPixels)
                error = "目标尺寸超过测试版限制：每边 64–8192，最多约 3355 万像素。";
            else
            {
                plan = new(width, height, (uint)w, (uint)h, percent);
                return true;
            }
        }
        return false;
    }

    public bool Matches(uint width, uint height) =>
        Math.Abs((long)width - RenderWidth) <= 2 && Math.Abs((long)height - RenderHeight) <= 2;
}

public sealed class PreviewSession
{
    public DateTimeOffset? Deadline { get; private set; }
    public DateTimeOffset? MatchDeadline { get; private set; }
    public ScalePlan Plan { get; private set; }
    public bool Active { get; private set; }
    public void Begin(ScalePlan plan, DateTimeOffset now)
    {
        Plan = plan;
        Active = true;
        Deadline = now.AddSeconds(15);
        MatchDeadline = now.AddSeconds(4);
    }
    public bool Confirm(uint width, uint height)
    {
        if (!Active || !Plan.Matches(width, height)) return false;
        Deadline = null;
        return true;
    }
    public string? Check(DateTimeOffset now, uint outputWidth, uint outputHeight, uint renderWidth, uint renderHeight)
    {
        if (!Active) return null;
        if (outputWidth != Plan.OutputWidth || outputHeight != Plan.OutputHeight) return "窗口尺寸已变化，已停止覆盖。";
        if (Deadline is { } limit && now >= limit) return "预览未确认，已请求恢复。";
        if (Plan.Matches(renderWidth, renderHeight)) MatchDeadline = now.AddSeconds(4);
        else if (MatchDeadline is { } matchLimit && now >= matchLimit) return "实际渲染尺寸持续未达到目标，已请求恢复。";
        return null;
    }
    public void Stop() { Active = false; Deadline = null; MatchDeadline = null; }
}
