namespace ForzavistaFreeRoam;

internal enum DrlColorMode
{
    Original,
    Fixed,
    Rainbow,
    Hazards,
    CustomCycle
}

/// <summary>A display-space color choice. Intensity and alpha remain owned by the lamp.</summary>
internal readonly record struct DrlColorSelection
{
    internal const double DefaultRainbowPeriodSeconds = 3;
    internal const double DefaultBrightnessPercent = 100;
    internal const double MaximumBrightnessPercent = 400;
    internal const float MaximumEmissionChannel = (float)(MaximumBrightnessPercent / 100);
    internal const double DefaultStrobeFlashesPerSecond = 4;
    internal const double MinimumStrobeFlashesPerSecond = 1;
    internal const double MaximumStrobeFlashesPerSecond = 20;
    internal const double HazardFlashesPerSecond = 1.5;

    internal DrlColorSelection(DrlColorMode mode, byte r, byte g, byte b,
        double rainbowPeriodSeconds = DefaultRainbowPeriodSeconds, bool strobe = false,
        double brightnessPercent = DefaultBrightnessPercent,
        double strobeFlashesPerSecond = DefaultStrobeFlashesPerSecond, bool nativeStrobe = false, DrlColorCycle? cycle = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!double.IsFinite(rainbowPeriodSeconds) || rainbowPeriodSeconds < 1 || rainbowPeriodSeconds > 120)
            throw new ArgumentOutOfRangeException(nameof(rainbowPeriodSeconds), "A rainbow cycle must last between 1 and 120 seconds.");
        if ((mode is DrlColorMode.Original or DrlColorMode.Hazards) && strobe)
            throw new ArgumentException("Choose a fixed color or RGB before enabling DRL strobe.", nameof(strobe));
        if (!double.IsFinite(brightnessPercent) || brightnessPercent is < 0 or > MaximumBrightnessPercent)
            throw new ArgumentOutOfRangeException(nameof(brightnessPercent));
        if ((mode == DrlColorMode.CustomCycle) != (cycle is not null))
            throw new ArgumentException("Only a custom cycle must have an ordered palette.", nameof(cycle));
        ValidateStrobeSpeed(strobeFlashesPerSecond);
        Mode = mode;
        R = r;
        G = g;
        B = b;
        RainbowPeriodSeconds = rainbowPeriodSeconds;
        Strobe = strobe;
        BrightnessPercent = brightnessPercent;
        StrobeFlashesPerSecond = strobeFlashesPerSecond;
        NativeStrobe = nativeStrobe;
        Cycle = cycle;
    }

    internal DrlColorMode Mode { get; }
    internal DrlColorCycle? Cycle { get; }
    internal bool IsCycling => Mode is DrlColorMode.Rainbow or DrlColorMode.CustomCycle;
    internal byte R { get; }
    internal byte G { get; }
    internal byte B { get; }
    internal double RainbowPeriodSeconds { get; }
    internal bool Strobe { get; }
    internal double BrightnessPercent { get; }
    internal double StrobeFlashesPerSecond { get; }
    internal bool NativeStrobe { get; }
    internal double StrobeHalfPeriodSeconds => .5 / StrobeFlashesPerSecond;
    internal TimeSpan FrameInterval => TimeSpan.FromMilliseconds(Mode == DrlColorMode.Original ? 150 :
        Mode == DrlColorMode.Hazards ? 50 : Strobe && !NativeStrobe ? Math.Min(IsCycling ? LightingAnimationPolicy.FrameMilliseconds : 50, 250 / StrobeFlashesPerSecond) : IsCycling ? LightingAnimationPolicy.FrameMilliseconds : 150);

    internal static DrlColorSelection Stock => new(DrlColorMode.Original, 255, 255, 255);
    internal static DrlColorSelection Fixed(byte r, byte g, byte b) => new(DrlColorMode.Fixed, r, g, b);
    internal static DrlColorSelection Rainbow(double periodSeconds = DefaultRainbowPeriodSeconds) =>
        new(DrlColorMode.Rainbow, 255, 0, 0, periodSeconds);
    internal static DrlColorSelection CustomCycle(DrlColorCycle cycle, double periodSeconds = DefaultRainbowPeriodSeconds) =>
        new(DrlColorMode.CustomCycle, 255, 0, 0, periodSeconds, cycle: cycle);
    internal static DrlColorSelection Hazards => new(DrlColorMode.Hazards, 255, 160, 0);

    internal DrlColorSelection WithStrobe(bool strobe) =>
        new(Mode, R, G, B, RainbowPeriodSeconds, strobe, BrightnessPercent, StrobeFlashesPerSecond, NativeStrobe, Cycle);

    internal DrlColorSelection WithRainbowPeriod(double periodSeconds) =>
        new(Mode, R, G, B, periodSeconds, Strobe, BrightnessPercent, StrobeFlashesPerSecond, NativeStrobe, Cycle);

    internal DrlColorSelection WithBrightness(double percent) =>
        new(Mode, R, G, B, RainbowPeriodSeconds, Strobe, percent, StrobeFlashesPerSecond, NativeStrobe, Cycle);

    internal DrlColorSelection WithStrobeSpeed(double flashesPerSecond) =>
        new(Mode, R, G, B, RainbowPeriodSeconds, Strobe, BrightnessPercent, flashesPerSecond, NativeStrobe, Cycle);

    internal DrlColorSelection WithNativeStrobe(bool native) =>
        new(Mode, R, G, B, RainbowPeriodSeconds, Strobe, BrightnessPercent, StrobeFlashesPerSecond, native, Cycle);

    internal static void ValidateStrobeSpeed(double flashesPerSecond)
    {
        if (!double.IsFinite(flashesPerSecond) || flashesPerSecond < MinimumStrobeFlashesPerSecond ||
            flashesPerSecond > MaximumStrobeFlashesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(flashesPerSecond));
    }

    internal double RemapStrobeElapsed(double elapsedSeconds, double newFlashesPerSecond)
    {
        ValidateStrobeSpeed(StrobeFlashesPerSecond);
        ValidateStrobeSpeed(newFlashesPerSecond);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        // Keep the fractional ON/OFF phase while changing speed; RGB has its own clock.
        return elapsedSeconds % (1 / StrobeFlashesPerSecond) * StrobeFlashesPerSecond / newFlashesPerSecond;
    }

    internal bool HasSameBaseColor(DrlColorSelection other) =>
        Mode == other.Mode && R == other.R && G == other.G && B == other.B &&
        RainbowPeriodSeconds == other.RainbowPeriodSeconds && Equals(Cycle, other.Cycle);

    /// <summary>Use a monotonic elapsed time from the activation of this choice.</summary>
    internal (byte R, byte G, byte B) GetRgb(double elapsedSeconds = 0)
    {
        var rgb = GetDisplayRgb(elapsedSeconds);
        return (ToByte(rgb.R), ToByte(rgb.G), ToByte(rgb.B));
    }

    private (double R, double G, double B) GetDisplayRgb(double elapsedSeconds)
    {
        if (!IsCycling)
            return (R / 255.0, G / 255.0, B / 255.0);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!double.IsFinite(RainbowPeriodSeconds) || RainbowPeriodSeconds < 1 || RainbowPeriodSeconds > 120)
            throw new InvalidOperationException("The rainbow period is invalid.");
        double phase = (elapsedSeconds % RainbowPeriodSeconds) / RainbowPeriodSeconds;
        if (Mode == DrlColorMode.CustomCycle) return Cycle!.GetDisplayRgb(phase);
        double hue = phase * 360;
        return FromHsvDisplay(hue, 1, 1);
    }

    internal (float R, float G, float B) GetLinearRgb(double elapsedSeconds = 0)
    {
        // Material frames retain floating-point interpolation. Byte rounding is
        // only for picker/preview RGB, not for the automatic rainbow fade.
        var rgb = GetDisplayRgb(elapsedSeconds);
        var scale = (float)(BrightnessPercent / 100);
        return (ToLinearDisplay(rgb.R) * scale, ToLinearDisplay(rgb.G) * scale, ToLinearDisplay(rgb.B) * scale);
    }

    /// <summary>Custom strobe blacks RGB. Native strobe leaves RGB steady for the game's trigger49.</summary>
    internal (float R, float G, float B) GetFrameLinearRgb(double elapsedSeconds = 0,
        double? strobeElapsedSeconds = null)
    {
        double strobeElapsed = strobeElapsedSeconds ?? elapsedSeconds;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 ||
            !double.IsFinite(strobeElapsed) || strobeElapsed < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (Mode == DrlColorMode.Hazards && elapsedSeconds % (1 / HazardFlashesPerSecond) >= .5 / HazardFlashesPerSecond)
            return (0f, 0f, 0f);
        if (Strobe && !NativeStrobe && strobeElapsed % (StrobeHalfPeriodSeconds * 2) >= StrobeHalfPeriodSeconds)
            return (0f, 0f, 0f);
        return GetLinearRgb(elapsedSeconds);
    }

    internal static float ToLinear(byte value) => ToLinearDisplay(value / 255.0);

    private static float ToLinearDisplay(double display)
    {
        return (float)(display <= 0.04045 ? display / 12.92 : Math.Pow((display + 0.055) / 1.055, 2.4));
    }

    internal static (byte R, byte G, byte B) FromHsv(double hue, double saturation, double value)
    {
        var rgb = FromHsvDisplay(hue, saturation, value);
        return (ToByte(rgb.R), ToByte(rgb.G), ToByte(rgb.B));
    }

    private static (double R, double G, double B) FromHsvDisplay(double hue, double saturation, double value)
    {
        if (!double.IsFinite(hue) || !double.IsFinite(saturation) || !double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(hue), "HSV values must be finite.");
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double second = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double offset = value - chroma;
        var components = hue switch
        {
            < 60 => (chroma, second, 0.0),
            < 120 => (second, chroma, 0.0),
            < 180 => (0.0, chroma, second),
            < 240 => (0.0, second, chroma),
            < 300 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };
        return (components.Item1 + offset, components.Item2 + offset, components.Item3 + offset);
    }

    internal static (double Hue, double Saturation, double Value) ToHsv(byte r, byte g, byte b)
    {
        double red = r / 255.0, green = g / 255.0, blue = b / 255.0;
        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;
        double hue = delta == 0 ? 0 : max == red ? 60 * ((green - blue) / delta % 6)
            : max == green ? 60 * ((blue - red) / delta + 2) : 60 * ((red - green) / delta + 4);
        return (((hue % 360) + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
