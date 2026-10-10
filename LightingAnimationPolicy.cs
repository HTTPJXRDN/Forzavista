namespace ForzavistaFreeRoam;

internal static class LightingAnimationPolicy
{
    internal const double FrameMilliseconds = 16;
    internal static TimeSpan Interval(DrlColorSelection selection, bool fading, bool colorPaused) =>
        TimeSpan.FromMilliseconds(Math.Min(fading ? FrameMilliseconds : 150,
            colorPaused ? 150 : selection.FrameInterval.TotalMilliseconds));
}

// A short rolling diagnostic, logged at most once per five seconds, never per
// frame. Uses monotonic time; no scheduler timing is inferred from requested Hz.
internal sealed class LightingFrameTiming
{
    private double? _first, _last;
    private int _frames;
    private double _maxGap, _work, _maxWork;
    internal void Record(double now, double workMilliseconds)
    {
        if (!double.IsFinite(now) || !double.IsFinite(workMilliseconds) || workMilliseconds < 0 ||
            _last is { } last && now < last) throw new ArgumentOutOfRangeException(nameof(now));
        _first ??= now;
        if (_last is { } previous) _maxGap = Math.Max(_maxGap, (now - previous) * 1000);
        _last = now; _frames++; _work += workMilliseconds; _maxWork = Math.Max(_maxWork, workMilliseconds);
    }
    internal bool Due(double now) => _first is { } first && now - first >= 5;
    internal string? Finish()
    {
        if (_frames == 0) return null;
        double elapsed = _last!.Value - _first!.Value;
        string result = FormattableString.Invariant($"frames={_frames}; measuredFps={(elapsed > 0 ? (_frames - 1) / elapsed : 0):0.0}; maxGapMs={_maxGap:0.0}; meanWorkMs={_work / _frames:0.0}; maxWorkMs={_maxWork:0.0}");
        _first = _last = null; _frames = 0; _maxGap = _work = _maxWork = 0;
        return result;
    }
}
