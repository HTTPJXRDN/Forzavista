using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForzavistaFreeRoam;

// Archive-derived candidates, not a declaration of runtime/visual support.
// White DRL channels and reviewed explicit nonwhite emission. Shader-inherited
// white is pinned separately. Intensity and the DRL trigger remain explicit.
internal sealed record DrlColorMaterialProfile(string Path, uint Id, string Name,
    string Resource, float[] Tint, float Intensity, string Channel = "Emission",
    bool? EmissiveSwitch = null, uint[]? OtherTriggers = null,
    string TintSource = "Instance", string? DefaultsKey = null, float[]? SurfaceTint = null,
    string TintPolicy = "White", float[]? LensTint = null, string LensTintSource = "Instance",
    float[]? EmitterBaseTint = null);

internal static class DrlColorProfileCatalog
{
    internal const string ResourceName = "ForzavistaFreeRoam.Lighting.drl-color-profiles.json";
    internal const string EmitterResource = @"Game:\Media\cars\_library\materials\_fmnext\lamp\emitter_ch1.materialbin";
    internal const string RegularEmitterResource = @"Game:\Media\cars\_library\materials\_fmnext\lamp\emitter.materialbin";
    internal const string GlassResource = @"Game:\Media\cars\_library\materials\_fmnext\glass\glass_detailed_ch1.materialbin";
    internal const string RegularGlassResource = @"Game:\Media\cars\_library\materials\_fmnext\glass\glass.materialbin";
    internal const string ProjectorLensResource = @"Game:\Media\cars\_library\materials\_fmnext\glass\glass_projectorlens_ch1.materialbin";
    internal const string AluminumResource = @"Game:\Media\cars\_library\materials\_fmnext\metal\aluminum.materialbin";
    internal const string DetailResource = @"Game:\Media\cars\_library\materials\_fmnext\detail\detail_ch1.materialbin";
    internal const string PaintedMetalResource = @"Game:\Media\cars\_library\materials\_fmnext\paint\paintedmetal.materialbin";
    internal const string PlasticResource = @"Game:\Media\cars\_library\materials\_fmnext\plastic\plastic.materialbin";
    internal const string TrafficOpaqueResource = @"Game:\Media\cars\_library\materials\_fmnext\TrafficVehicle\detail_opaque.materialbin";
    internal static bool IsStandardRadiosityResource(string resource) =>
        new[] { DetailResource, PaintedMetalResource, PlasticResource }.Contains(resource, StringComparer.OrdinalIgnoreCase);
    // Exact current-disk material -> shader chain; generator verifies both full
    // entry hashes and the complete white DFPR default before emitting profiles.
    internal const string EmitterWhiteDefaultsKey = "emitter_ch1.white:" +
        "c3ab547b8ce034550eb31c07dc0e5404fcc3ba3835990be47d1796b9f707279a:" +
        "8d4ef07a59378e6862a1e9318b8b247100e7fc5e05954a8fdbe6ae6ea2a57178";
    internal const string RegularEmitterWhiteDefaultsKey = "emitter.white:" +
        "ccd5c38189ae631c30f5fa9c13b4e7dae7ac2a3d3536177b37177c506c3e97e0:" +
        "8d4ef07a59378e6862a1e9318b8b247100e7fc5e05954a8fdbe6ae6ea2a57178";
    // Complete car_lens DFPR and direct material chain: its inherited tint is
    // cool white, not emitter white. Explicit source15/intensity remain required.
    internal const string ProjectorLensDefaultsKey = "projectorlens_ch1.coolwhite:" +
        "86935698a9a4a530fd7ca79f8588b632beb5a30e385a29d985ad668a7c90fcd9:" +
        "2152427527b2d8ba8e19434ee21cd45c9ec70719345fa789e904ef67b8c66c1c";
    private static readonly Lazy<IReadOnlyDictionary<string, DrlColorMaterialProfile[]>> Profiles = new(Load);
    private sealed record CatalogDocument(int FormatVersion, CatalogCar[] Cars);
    private sealed record CatalogCar(string Car, DrlColorMaterialProfile[] Materials);

    internal static IReadOnlyList<DrlColorMaterialProfile> ForCar(string car)
    {
        var key = NormalizeCar(car);
        return Profiles.Value.TryGetValue(key, out var values)
            ? values.Select(p => p with { Tint = p.Tint.ToArray(), OtherTriggers = p.OtherTriggers?.ToArray(),
                SurfaceTint = p.SurfaceTint?.ToArray(), LensTint = p.LensTint?.ToArray(),
                EmitterBaseTint = p.EmitterBaseTint?.ToArray() }).ToArray()
            : Array.Empty<DrlColorMaterialProfile>();
    }

    internal static string NormalizeCar(string car)
    {
        if (string.IsNullOrWhiteSpace(car) || car.Length > 96 ||
            !Regex.IsMatch(car, @"\A[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("DRL color car identity is invalid.");
        return car.ToUpperInvariant();
    }

    internal static DrlColorMaterialProfile ValidateProfile(DrlColorMaterialProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Path) || profile.Path.Length > 384 ||
            string.IsNullOrEmpty(profile.Name) || profile.Name.Length > 127 ||
            profile.Name.Any(c => c is < ' ' or > '~') || profile.Id > 4095 || profile.Tint is not { Length: 4 } ||
            profile.Tint.Any(c => !float.IsFinite(c) || c < 0 || c > 1) || profile.Tint[3] <= 0 ||
            !float.IsFinite(profile.Intensity) || profile.Intensity <= 0 || profile.Intensity > 1048576)
            throw new InvalidOperationException("DRL color material profile has invalid bounds.");
        var path = profile.Path.Replace('/', '\\');
        var segments = path.Split('\\');
        // Some cars embed inherited scene parts below their own Scene/_library
        // directory. Retain that exact archive path; runtime discovery still
        // requires the current car prefix and its rooted player Scene Pose.
        var exteriorPath = path.StartsWith(@"Scene\Exterior\", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(path, @"\AScene\\(?:_library\\Scene\\[A-Za-z0-9_]+\\Scene\\)+Exterior\\",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var resource = profile.Resource?.Replace('/', '\\');
        var ch1Emitter = string.Equals(resource, EmitterResource, StringComparison.OrdinalIgnoreCase);
        var regularEmitter = string.Equals(resource, RegularEmitterResource, StringComparison.OrdinalIgnoreCase);
        var emitter = (ch1Emitter || regularEmitter) &&
            profile.Channel == "Emission" && profile.EmissiveSwitch is null && profile.OtherTriggers is null;
        var projectorLens = string.Equals(resource, ProjectorLensResource, StringComparison.OrdinalIgnoreCase) &&
            profile.Channel == "Emission" && profile.EmissiveSwitch is null && profile.OtherTriggers is null;
        // PG bus: explicit illumination RGB and light-function15 in the traffic
        // opaque shader. Its 128-byte compiled layout is separate from emitters.
        var trafficEmission = string.Equals(resource, TrafficOpaqueResource, StringComparison.OrdinalIgnoreCase) &&
            profile.Channel == "Emission" && profile.EmissiveSwitch is null && profile.OtherTriggers is null &&
            profile.TintSource == "Instance" && profile.TintPolicy == "White" && profile.SurfaceTint is null &&
            profile.LensTint is null && profile.EmitterBaseTint is null;
        var detailedGlass = string.Equals(resource, GlassResource, StringComparison.OrdinalIgnoreCase);
        var regularGlass = string.Equals(resource, RegularGlassResource, StringComparison.OrdinalIgnoreCase);
        var glass = (detailedGlass || regularGlass) &&
            profile.Channel == "Emission" && profile.EmissiveSwitch == true && profile.OtherTriggers is null;
        var radiosity = string.Equals(resource, AluminumResource, StringComparison.OrdinalIgnoreCase) &&
            profile.Channel == "Radiosity" && profile.EmissiveSwitch == true &&
            profile.OtherTriggers is { Length: 2 } && profile.OtherTriggers.All(v => v <= 127 && v != 15);
        // Current car_standard CBMP and complete M2FE captures prove a DRL
        // radiosity channel at50/1C0 and a separate headlamp channel at60/1C4.
        // These materials have no authored emission master switch.
        var standardRadiosity = resource is not null && IsStandardRadiosityResource(resource) &&
            profile.Channel == "Radiosity" && profile.EmissiveSwitch is null &&
            profile.OtherTriggers is { Length: 1 } && profile.OtherTriggers[0] == 13;
        var tintSourceValid = profile.TintSource == "Instance" && profile.DefaultsKey is null ||
            profile.TintSource == "ShaderDefault" && emitter &&
            profile.DefaultsKey == (ch1Emitter ? EmitterWhiteDefaultsKey : RegularEmitterWhiteDefaultsKey) &&
            profile.Tint.SequenceEqual(new float[] { 1, 1, 1, 1 }) ||
            profile.TintSource == "ShaderDefault" && projectorLens &&
            profile.DefaultsKey == ProjectorLensDefaultsKey &&
            profile.Tint.SequenceEqual(new float[] { .71569353f, .791298f, .9646863f, 1 });
        // Surface tint is a separately authored regular-glass field. Never
        // infer it from emission or allow a surface write in another family.
        var surfaceTintValid = profile.SurfaceTint is null || regularGlass && glass &&
            profile.TintSource == "Instance" && profile.SurfaceTint is { Length: 4 } &&
            profile.SurfaceTint.All(c => float.IsFinite(c) && c is >= 0 and <= 1) &&
            profile.SurfaceTint[3] > 0 && profile.SurfaceTint.Take(3).Min() > .25f &&
            profile.SurfaceTint.Take(3).Max() - profile.SurfaceTint.Take(3).Min() <= .4f;
        // Nonwhite is opt-in only for explicit source15 emission in an already
        // qualified family. Glass additionally proves a clear surface tint,
        // captured read-only at runtime; it is never owned. CompiledWhite is
        // exact white in regular glass, with one explicit emission Color only;
        // it does not infer a value from an incompletely decoded shader default.
        var authoredEmission = profile.TintPolicy == "AuthoredEmission" &&
            profile.TintSource == "Instance" && (emitter || projectorLens || glass || standardRadiosity) && profile.SurfaceTint is null &&
            profile.Tint.Take(3).Max() > 0;
        var lensTintValid = profile.LensTint is null ? !authoredEmission || emitter || projectorLens || standardRadiosity :
            authoredEmission && glass && profile.LensTint is { Length: 4 } &&
            profile.LensTint.All(c => float.IsFinite(c) && c is >= 0 and <= 1) &&
            profile.LensTint[3] > 0 && profile.LensTint.Take(3).Min() > .25f &&
            profile.LensTint.Take(3).Max() - profile.LensTint.Take(3).Min() <= .4f;
        var lensSourceValid = profile.LensTintSource == "Instance" ||
            profile.LensTintSource == "CompiledWhite" && authoredEmission && (regularGlass || detailedGlass) &&
            profile.LensTint is { Length: 4 } && profile.LensTint.SequenceEqual(new float[] { 1, 1, 1, 1 });
        // An explicitly authored emitter base tint is a read-only source proof,
        // not another color destination. It must remain exact at shader 0x10.
        var emitterBaseValid = profile.EmitterBaseTint is null || authoredEmission && emitter &&
            profile.EmitterBaseTint is { Length: 4 } &&
            profile.EmitterBaseTint.All(c => float.IsFinite(c) && c is >= 0 and <= 1) &&
            profile.EmitterBaseTint[3] > 0;
        if (!exteriorPath ||
            !path.EndsWith(".modelbin", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.IndexOfAny([':', '\0']) >= 0) ||
            path.Any(c => c is < ' ' or > '~') ||
            !(emitter || projectorLens || glass || radiosity || standardRadiosity || trafficEmission) || !tintSourceValid || !surfaceTintValid ||
            !(profile.TintPolicy == "White" || authoredEmission) || !lensTintValid || !lensSourceValid || !emitterBaseValid)
            throw new InvalidOperationException("DRL color profile is not a qualified exterior shader channel.");
        var minimum = profile.Tint.Take(3).Min();
        var maximum = profile.Tint.Take(3).Max();
        if (!authoredEmission && (minimum <= .25f || maximum - minimum > .4f))
            throw new InvalidOperationException("DRL color profile does not have a white authored tint.");
        return profile with { Path = path, Resource = ch1Emitter ? EmitterResource :
                regularEmitter ? RegularEmitterResource : detailedGlass ? GlassResource :
                regularGlass ? RegularGlassResource : projectorLens ? ProjectorLensResource :
                trafficEmission ? TrafficOpaqueResource :
                standardRadiosity ? new[] { DetailResource, PaintedMetalResource, PlasticResource }
                    .Single(r => r.Equals(resource, StringComparison.OrdinalIgnoreCase)) : AluminumResource,
            Tint = profile.Tint.ToArray(), OtherTriggers = profile.OtherTriggers?.ToArray(),
            SurfaceTint = profile.SurfaceTint?.ToArray(), LensTint = profile.LensTint?.ToArray(),
            EmitterBaseTint = profile.EmitterBaseTint?.ToArray() };
    }

    private static IReadOnlyDictionary<string, DrlColorMaterialProfile[]> Load()
    {
        using var stream = typeof(DrlColorProfileCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The DRL color material catalog is missing.");
        if (!stream.CanSeek || stream.Length is < 2 or > 16777216)
            throw new InvalidOperationException("The DRL color catalog size is invalid.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = false, MaxDepth = 12 });
        if (document is null || document.FormatVersion != 2 || document.Cars is not { Length: > 0 and <= 4096 })
            throw new InvalidOperationException("The DRL color catalog format is unsupported.");
        var result = new Dictionary<string, DrlColorMaterialProfile[]>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var entry in document.Cars)
        {
            if (entry is null || entry.Materials is not { Length: > 0 and <= 512 })
                throw new InvalidOperationException("The DRL color catalog car entry is invalid.");
            var car = NormalizeCar(entry.Car);
            var materialKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materials = entry.Materials.Select(ValidateProfile).ToArray();
            foreach (var material in materials)
                if (!materialKeys.Add(material.Path + "\n" + material.Id))
                    throw new InvalidOperationException("The DRL color catalog contains a duplicate material.");
            if (!result.TryAdd(car, materials) || (total += materials.Length) > 65536)
                throw new InvalidOperationException("The DRL color catalog contains duplicate cars or too many materials.");
        }
        return result;
    }
}
