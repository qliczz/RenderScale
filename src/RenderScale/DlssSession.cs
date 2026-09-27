namespace RenderScale;

// NGX EvaluateFeature success is exactly 1. A loaded DLL or saved DLSS option
// alone does not establish that the game is evaluating DLSS frames.
public sealed class DlssSession
{
    private readonly object gate = new();
    private long generation;
    private bool active;
    private long started, lastCall;
    private int successes;
    private uint result;

    public static bool Supports(float percent) => float.IsFinite(percent) && percent >= 25 && percent <= 100;
    public long Token { get { lock (gate) return active ? generation : 0; } }

    public void Begin(long now)
    {
        lock (gate)
        {
            generation++;
            active = true;
            started = now;
            lastCall = 0;
            successes = 0;
            result = 0;
        }
    }

    public void Stop() { lock (gate) active = false; }

    public void Observe(long token, uint value, long now)
    {
        lock (gate)
        {
            // Ignore evaluations that began before this preview, including a
            // call that was in flight when the previous preview ended.
            if (!active || token == 0 || token != generation) return;
            result = value;
            lastCall = now;
            successes = value == 1 ? Math.Min(successes + 1, 3) : 0;
        }
    }

    public DlssSample Read(long now)
    {
        lock (gate)
        {
            var healthy = active && successes >= 3 && result == 1 && now >= lastCall && now - lastCall <= 2000;
            return new(healthy, active && now - started >= 4000 && !healthy, result);
        }
    }
}

public readonly record struct DlssSample(bool Healthy, bool ShouldStop, uint LastResult);
