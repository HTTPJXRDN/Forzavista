using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzavistaFreeRoam;

/// <summary>Portable choices only: no car identity, process pointers or captured lamp buffers.</summary>
internal sealed record LightingPreset
{
    public string Name { get; init; } = "";
    public DrlColorMode Mode { get; init; } = DrlColorMode.Fixed;
    public byte R { get; init; } = 255;
    public byte G { get; init; } = 255;
    public byte B { get; init; } = 255;
    public DrlCycleColor[] Colors { get; init; } = DrlColorCycle.Default.Colors.ToArray();
    public bool UseCustomCycle { get; init; }
    public double CycleSeconds { get; init; } = DrlColorSelection.DefaultRainbowPeriodSeconds;
    public double BrightnessPercent { get; init; } = DrlColorSelection.DefaultBrightnessPercent;
    public bool Strobe { get; init; }
    public double StrobeFlashesPerSecond { get; init; } = DrlColorSelection.DefaultStrobeFlashesPerSecond;
    public bool NativeStrobe { get; init; } = true;
    public bool FadeEnabled { get; init; }
    public double FadeSeconds { get; init; } = LightFadePolicy.DefaultSeconds;

    internal LightingPreset Validated()
    {
        string name = (Name ?? "").Trim();
        if (name.Length is < 1 or > 64 || (Name ?? "").Any(char.IsControl))
            throw new InvalidDataException("Preset names need 1 to 64 characters.");
        if (Mode is not (DrlColorMode.Original or DrlColorMode.Fixed or DrlColorMode.Rainbow or DrlColorMode.CustomCycle))
            throw new InvalidDataException("This preset has an unsupported color mode.");
        if (!double.IsFinite(CycleSeconds) || CycleSeconds is < 1 or > 12 ||
            !double.IsFinite(FadeSeconds) || FadeSeconds is < .1 or > 5)
            throw new InvalidDataException("This preset has an invalid cycle or fade duration.");
        try
        {
            var palette = new DrlColorCycle(Colors);
            var copy = this with { Name = name, Colors = palette.Colors.ToArray() };
            _ = copy.ToSelection(); // Validate brightness and strobe, including Original + strobe.
            return copy;
        }
        catch (ArgumentException error) { throw new InvalidDataException("This preset has invalid lighting settings.", error); }
    }

    internal DrlColorSelection ToSelection() => new(Mode, R, G, B, CycleSeconds, Strobe,
        BrightnessPercent, StrobeFlashesPerSecond, NativeStrobe,
        Mode == DrlColorMode.CustomCycle ? new DrlColorCycle(Colors) : null);

    internal string Description => (Mode switch
    {
        DrlColorMode.Original => "Original DRL color",
        DrlColorMode.Fixed => $"#{R:X2}{G:X2}{B:X2}",
        DrlColorMode.Rainbow => $"Full rainbow · {CycleSeconds:0.0}s / cycle",
        _ => string.Join(" → ", Colors.Select(c => c.Hex)) + $" · {CycleSeconds:0.0}s / cycle"
    }) + (Mode != DrlColorMode.Original ? $"\n{BrightnessPercent:0}% brightness" : "") +
        (Strobe ? NativeStrobe ? " · native strobe" : $" · {StrobeFlashesPerSecond:0} flashes / sec" : "") +
        (FadeEnabled ? $"\nFade on / off: {FadeSeconds:0.0}s" : "");
}

internal static class LightingPresetStore
{
    internal const int MaximumPresets = 100;
    private sealed record Library(int SchemaVersion, LightingPreset[] Presets);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<DrlColorMode>(allowIntegerValues: false) }
    };

    internal static List<LightingPreset> Load(string path)
    {
        if (!File.Exists(path)) return Defaults();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The preset file is too large.");
        var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(path), Options);
        if (library is null || library.SchemaVersion != 1 || library.Presets is null)
            throw new InvalidDataException("The preset file has an unsupported format.");
        return Validate(library.Presets);
    }

    internal static void Save(string path, IEnumerable<LightingPreset> presets)
    {
        var library = new Library(1, Validate(presets).ToArray());
        string json = JsonSerializer.Serialize(library, Options);
        if (File.Exists(path)) _ = Load(path); // Never overwrite an unreadable user's library.
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(absolute)) File.Copy(absolute, absolute + ".bak", overwrite: true);
            File.Move(temporary, absolute, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static List<LightingPreset> Validate(IEnumerable<LightingPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var result = presets.Take(MaximumPresets + 1).Select(p =>
            p?.Validated() ?? throw new InvalidDataException("A preset is missing.")).ToList();
        if (result.Count > MaximumPresets || result.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("Preset names must be unique; at most 100 presets can be saved.");
        return result;
    }

    private static List<LightingPreset> Defaults() =>
    [
        new() { Name = "Red, yellow, blue, green", Mode = DrlColorMode.CustomCycle, UseCustomCycle = true,
            Colors = [new(255, 0, 0), new(255, 255, 0), new(0, 0, 255), new(0, 255, 0)] },
        new() { Name = "Red, blue, purple", Mode = DrlColorMode.CustomCycle, UseCustomCycle = true },
        new() { Name = "Full rainbow", Mode = DrlColorMode.Rainbow }
    ];
}
