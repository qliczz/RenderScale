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
foreach (var percent in new[] { 25f, 33.3f, 67f, 75f, 100f })
    Check(DlssSession.Supports(percent), "DLSS allowed input range");
foreach (var percent in new[] { float.NaN, float.PositiveInfinity, 24.9f, 100.01f, 150f })
    Check(!DlssSession.Supports(percent), "DLSS supersampling or invalid input rejected");
var dlss = new DlssSession();
dlss.Begin(1000);
var token = dlss.Token;
Check(!dlss.Read(4999).ShouldStop, "DLSS settling grace");
Check(dlss.Read(5000).ShouldStop, "loaded DLL without evaluations cannot pass");
dlss.Observe(token, 1, 5000);
Check(!dlss.Read(5000).Healthy, "one successful frame cannot confirm");
dlss.Observe(token, 1, 5010);
dlss.Observe(token, 1, 5020);
Check(dlss.Read(6000).Healthy, "recent consecutive NGX successes");
Check(dlss.Read(7021).ShouldStop, "missing evaluations after confirmation stop override");
dlss.Observe(token, 0xBAD00001, 7030);
Check(!dlss.Read(7030).Healthy && dlss.Read(7030).LastResult == 0xBAD00001, "NGX failure blocks confirmation");
dlss.Begin(8000);
dlss.Observe(token, 1, 8001);
Check(dlss.Read(8001).LastResult == 0, "in-flight previous-preview result ignored");
dlss.Observe(0, 1, 8002);
Check(dlss.Read(8002).LastResult == 0, "call started before observation ignored");
var newToken = dlss.Token;
dlss.Stop();
dlss.Observe(newToken, 1, 9000);
Check(!dlss.Read(15000).ShouldStop && dlss.Read(15000).LastResult == 0, "stopped monitor inert");
Console.WriteLine($"PASS: {count} assertions");
