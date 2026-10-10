using System.Text.Json;

namespace ForzavistaFreeRoam;

// Static admission for factory amber emitters using native21/20. Activation
// still requires all four fitted materials and their rooted renderer/source
// snapshots, plus the unchanged native blink controller expressions.
internal static class NativeHazardProfileCatalog
{
    internal sealed record CarProfile(string Car, DrlColorMaterialProfile[] Left,
        DrlColorMaterialProfile[] Right);
    private sealed record Document(int FormatVersion, CarProfile[] Cars);
    private static readonly Lazy<IReadOnlyDictionary<string, CarProfile>> Profiles = new(Load);

    internal static CarProfile? ForCar(string? car) => car is not null &&
        Profiles.Value.TryGetValue(car, out var profile) ? profile : null;

    private static IReadOnlyDictionary<string, CarProfile> Load()
    {
        using var stream = typeof(NativeHazardProfileCatalog).Assembly.GetManifestResourceStream(
            "ForzavistaFreeRoam.Signals.native-hazard-profiles.json")
            ?? throw new InvalidOperationException("Native hazard profile resource is missing.");
        var document = JsonSerializer.Deserialize<Document>(stream)
            ?? throw new InvalidOperationException("Native hazard profiles are invalid.");
        if (document.FormatVersion != 1) throw new InvalidOperationException("Unsupported native hazard catalog.");
        var result = new Dictionary<string, CarProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var car in document.Cars)
        {
            if (DrlColorProfileCatalog.NormalizeCar(car.Car) != car.Car ||
                car.Left.Length != 2 || car.Right.Length != 2)
                throw new InvalidOperationException("Native hazard profile needs exact front/rear pairs.");
            foreach (var side in new[] { car.Left, car.Right })
            {
                var validated = side.Select(DrlColorProfileCatalog.ValidateProfile).ToArray();
                if (validated.Any(p => p.Resource != DrlColorProfileCatalog.EmitterResource ||
                        p.TintPolicy != "AuthoredEmission" || p.TintSource != "Instance" ||
                        p.OtherTriggers is not null || p.EmitterBaseTint is not null) ||
                    validated.Count(p => p.Path.StartsWith(@"Scene\Exterior\PrimaryLights\", StringComparison.OrdinalIgnoreCase)) != 1 ||
                    validated.Count(p => p.Path.StartsWith(@"Scene\Exterior\SecondaryLights\", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidOperationException("Native hazard profile family/coverage changed.");
            }
            if (!result.TryAdd(car.Car, car)) throw new InvalidOperationException("Duplicate native hazard car.");
        }
        return result;
    }
}
