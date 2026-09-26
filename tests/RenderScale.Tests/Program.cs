using RenderScale;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    count++;
}
ScalePlan Plan(uint w, uint h, float percent)
{
    Check(ScalePlan.TryCreate(w, h, percent, out var plan, out _), $"plan {w}x{h} {percent}");
    return plan;
}
var p = Plan(1980, 1080, 150);
Check(p.RenderWidth == 2970 && p.RenderHeight == 1620, "1980 is retained, not silently changed to 1920");
var down = Plan(1980, 1080, 75);
Check(down.RenderWidth == 1485 && down.RenderHeight == 810, "downsample dimensions");
Check(Plan(2560, 1440, 150).RenderWidth == 3840, "1440p to 4K");
Check(Plan(1920, 1080, 400).RenderWidth == 7680, "400 percent within pixel budget");
Check(!ScalePlan.TryCreate(1980, 1080, 400, out _, out _), "1980x1080 at 400 percent exceeds pixel budget");
foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -1f, 24.9f, 400.1f })
    Check(!ScalePlan.TryCreate(1980, 1080, invalid, out _, out _), "invalid input rejected");
Check(!ScalePlan.TryCreate(0, 0, 100, out _, out _), "zero-sized window");
Check(!ScalePlan.TryCreate(7680, 4320, 200, out _, out _), "dimension cap");
Check(!ScalePlan.TryCreate(8192, 8192, 100, out _, out _), "pixel budget cap");
var t = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
var session = new PreviewSession();
session.Begin(p, t);
Check(!session.Confirm(1980, 1080), "can't confirm unapplied settings");
Check(session.Check(t.AddSeconds(3),1980,1080,1980,1080) is null, "allow settling");
Check(session.Check(t.AddSeconds(4),1980,1080,1980,1080) is not null, "mismatch rolls back");
session.Begin(p,t);
Check(session.Check(t.AddSeconds(2),1980,1080,2970,1620) is null, "observed size matches");
Check(session.Confirm(2970,1620), "confirm observed size");
Check(session.Deadline is null && session.Active, "confirmation cancels timeout but retains monitoring");
Check(session.Check(t.AddSeconds(30),1980,1080,2970,1620) is null, "confirmed stable state");
Check(session.Check(t.AddSeconds(35),1980,1080,1980,1080) is not null, "detect reset after confirmation");
session.Begin(p,t);
Check(session.Check(t.AddSeconds(15),1980,1080,2970,1620) is not null, "unconfirmed expires despite matching");
session.Begin(p,t);
Check(session.Check(t.AddSeconds(1),1920,1080,2970,1620) is not null, "window resize terminates override");
session.Stop();
Check(!session.Active && session.Check(t,1,1,1,1) is null, "stopped session inert");
Console.WriteLine($"PASS: {count} assertions");
