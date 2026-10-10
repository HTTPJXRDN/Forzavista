using System.Collections.ObjectModel;

namespace ForzavistaFreeRoam;

internal readonly record struct DrlCycleColor(byte R, byte G, byte B)
{
    internal string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Ordered, immutable display-space stops. The last stop fades back to the first.</summary>
internal sealed class DrlColorCycle : IEquatable<DrlColorCycle>
{
    internal const int MaximumColors = 12;
    private readonly DrlCycleColor[] _colors;
    internal ReadOnlyCollection<DrlCycleColor> Colors { get; }

    internal DrlColorCycle(IEnumerable<DrlCycleColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _colors = colors.Take(MaximumColors + 1).ToArray();
        if (_colors.Length is < 2 or > MaximumColors)
            throw new ArgumentException($"Choose 2 to {MaximumColors} colors for a cycle.", nameof(colors));
        Colors = Array.AsReadOnly(_colors);
    }

    internal static DrlColorCycle Default => new([new(255, 0, 0), new(0, 0, 255), new(128, 0, 255)]);

    internal (double R, double G, double B) GetDisplayRgb(double phase)
    {
        if (!double.IsFinite(phase) || phase < 0) throw new ArgumentOutOfRangeException(nameof(phase));
        double position = phase % 1 * _colors.Length;
        int index = (int)position;
        double blend = position - index;
        var a = _colors[index];
        var b = _colors[(index + 1) % _colors.Length];
        // Interpolate selected RGB stops, without taking an HSV tour through other hues.
        return ((a.R + (b.R - a.R) * blend) / 255,
            (a.G + (b.G - a.G) * blend) / 255,
            (a.B + (b.B - a.B) * blend) / 255);
    }

    public bool Equals(DrlColorCycle? other) => other is not null && _colors.SequenceEqual(other._colors);
    public override bool Equals(object? obj) => obj is DrlColorCycle other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var color in _colors) hash.Add(color);
        return hash.ToHashCode();
    }
}
