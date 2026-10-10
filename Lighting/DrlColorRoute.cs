using System.Text;

namespace ForzavistaFreeRoam;

// Qualified Steam emitter, glass and radiosity DRL channels. The caller must provide an
// exact supported-build/current-player identity. Catalog metadata does not
// establish an active renderer; every loaded destination is guarded here.
internal sealed class DrlColorRoute
{
    private sealed record Layout(int BufferSize, int Tint, int Intensity, int Trigger, int TextureBytes,
        int TailOffset, int ExternalBytes, int? Switch = null, int? SurfaceTint = null)
    {
        internal int[] ColorOffsets { get; } = SurfaceTint is { } surface
            ? [Tint, Tint + 4, Tint + 8, surface, surface + 4, surface + 8] : [Tint, Tint + 4, Tint + 8];
        internal int[] OwnedOffsets => [.. ColorOffsets, Trigger];
    }
    private static readonly Layout EmitterLayout = new(0x100, 0x40, 0xB0, 0xC0, 0xC0, 0xF0, 8);
    // Exact car_lens CBMP/DFPR and both rooted 4C buffers. This shader has one
    // emission channel and no surface-color/master-switch field.
    private static readonly Layout ProjectorLensLayout = new(0x80, 0x10, 0x48, 0x50, 0x60, 0x60, 0x18);
    // Current-disk CBMP maps and complete read-only M2 CPU/mapped captures.
    // RGB and the selected mapped trigger are owned. CPU sources, intensity,
    // other channels and master switches retain their captured bytes.
    private static readonly Layout GlassLayout = new(0x220, 0x30, 0x15C, 0x1C0, 0x260, 0x210, 8, 0x1FC);
    // Regular car_glass emission; pinned current shader CBMP and both 488 GTB
    // bulb buffers. Base tint is owned only with its explicit separate profile;
    // radiosity and other lighting channels remain unowned.
    private static readonly Layout RegularGlassLayout = new(0x1E0, 0x20, 0x128, 0x190, 0x200, 0x1D0, 8, 0x1C0);
    private static readonly Layout RegularGlassSurfaceLayout = new(0x1E0, 0x20, 0x128, 0x190, 0x200, 0x1D0, 8, 0x1C0, 0x10);
    private static readonly Layout RadiosityLayout = new(0x220, 0x50, 0x154, 0x1C0, 0x240, 0x210, 8, 0x1F8);
    private static readonly Layout StandardRadiosityLayout = new(0x220, 0x50, 0x154, 0x1C0, 0x240, 0x210, 8);
    // Current traffic opaque CBMP and both complete rooted PG bus captures:
    // illumination RGB20, exposure58, function60, CBEX70, four texture slots.
    // Own RGB and optional strobe trigger only; every other field stays captured.
    private static readonly Layout TrafficEmissionLayout = new(0x80, 0x20, 0x58, 0x60, 0x80, 0x70, 8);
    private static readonly byte[] UploadSignature = Convert.FromHexString("4C8BDC49895B205556574883EC70498B18");
    private static readonly byte[] WalkSignature = Convert.FromHexString(
        "48895C241855565741544155415641574883EC204D8BF94C8BD24C8BE14C2B91A00000004D03D04C8B415033D2498BC249F7F0");
    // CBEX starts at each family's pinned TailOffset. Its IDs are assigned at
    // runtime, rather than fixed shader constants. They must match the complete
    // material external-binding vector; capture and retain that vector and the
    // zero padding unchanged for every subsequent write and restore.
    private sealed record TextureSource(ulong Resource, ulong PathAddress, byte[] Path);
    private sealed record Parameter(ulong Address, byte[] Header, TextureSource? Texture);
    private sealed class Target
    {
        internal required Layout Layout;
        internal required ulong Wrapper, Instance, Resource, PathAddress, Definition, Table, Count, Record, Material;
        internal required ulong Begin, End, External, Compiled, Cpu, Binding, Buffer, Page, Mapped, Textures;
        internal required string Path, Name;
        internal required byte[] TableBytes, RecordHeader, Members, ExternalHeader, ExternalBindings,
            CompiledHeader, Descriptor, PageHeader, TextureBytes, Original, Last;
        internal required Parameter[] Parameters;
        internal required byte Flags;
        internal HashSet<int> Owned { get; } = [];
    }
    private sealed record PendingWrite(Target Target, int Offset, byte[] Previous, byte[] Desired);
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<bool> sameCar;
    private readonly Func<ulong, int, bool> writable;
    private readonly ulong module, service, backend, backendVtable, pages, pagesEnd, pageSize, gpuBase;
    private readonly ChargerRenderOwner renderOwner;
    private readonly DrlBuildMapping build;
    private readonly uint sourceTrigger;
    private readonly List<Target> targets = [];
    private PendingWrite? pending;
    internal int Count => targets.Count;
    internal bool HasWrites => pending is not null || targets.Any(t => t.Owned.Count != 0);
    internal bool IsCurrentScene(Func<ulong, ulong?> pointer) => renderOwner.IsCurrentScene(pointer);
    internal IEnumerable<(ulong Mapped, ulong Cpu)> Buffers => targets.Select(t => (t.Mapped, t.Cpu));

    private static bool Heap(ulong address) => address is >= 0x10000000 and < 0x7FF000000000 && (address & 7) == 0;
    private static string Normalize(string path) => path.Replace('/', '\\');
    private static bool Overlaps(ulong first, int firstLength, ulong second, int secondLength) =>
        firstLength <= 0 || secondLength <= 0 || first > ulong.MaxValue - (ulong)firstLength ||
        second > ulong.MaxValue - (ulong)secondLength ||
        first < second + (ulong)secondLength && second < first + (ulong)firstLength;
    private byte[] Bytes(ulong address, int length)
    {
        if (address < 0x10000 || length <= 0 || address > ulong.MaxValue - (ulong)length)
            throw new InvalidOperationException("DRL color read bounds are invalid.");
        var value = read(address, length);
        if (value.Length != length) throw new InvalidOperationException("Short DRL color ownership read.");
        return value;
    }
    private ulong Pointer(ulong address) => BitConverter.ToUInt64(Bytes(address, 8));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private string ResourcePath(ulong address)
    {
        Require(Heap(address), "DRL color model path pointer is invalid.");
        var bytes = Bytes(address, 512);
        var end = Array.IndexOf(bytes, (byte)0);
        Require(end is > 0 and < 512, "DRL color model path bounds are invalid.");
        return Normalize(Encoding.ASCII.GetString(bytes, 0, end));
    }
    private string MaterialName(byte[] record)
    {
        var length = BitConverter.ToUInt64(record, 0x18);
        var capacity = BitConverter.ToUInt64(record, 0x20);
        Require(length is > 0 and <= 127 && capacity >= length && capacity <= 255,
            "DRL color material name bounds are invalid.");
        if (capacity < 16) return Encoding.ASCII.GetString(record, 8, (int)length);
        var address = BitConverter.ToUInt64(record, 8);
        Require(Heap(address), "DRL color material name pointer is invalid.");
        return Encoding.ASCII.GetString(Bytes(address, (int)length));
    }

    internal DrlColorRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        string car, IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models,
        IReadOnlyList<DrlColorMaterialProfile> profiles, uint sourceTrigger = 15, DrlBuildMapping? build = null)
    {
        if (sourceTrigger is not (15 or 20 or 21)) throw new ArgumentOutOfRangeException(nameof(sourceTrigger));
        this.sourceTrigger = sourceTrigger;
        this.build = build ?? DrlBuildMapping.Steam;
        this.read = read; this.write = write; this.sameCar = sameCar; this.writable = writable; this.module = module;
        Require(module >= 0x10000 && module <= ulong.MaxValue - this.build.SizeOfImage && sameCar(),
            "DRL color exact player/build identity is not ready.");
        var carKey = DrlColorProfileCatalog.NormalizeCar(car);
        Require(profiles.Count is > 0 and <= 512 && models.Count is > 0 and <= 512,
            "No bounded DRL color material candidates are available.");
        var qualified = profiles.Select(DrlColorProfileCatalog.ValidateProfile).ToArray();
        var profileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in qualified)
            Require(profileKeys.Add(profile.Path + "\n" + profile.Id), "Duplicate DRL color material candidates.");
        var expected = qualified.GroupBy(p => @"Game:\Media\cars\" + carKey + "\\" + p.Path,
            StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var loaded = models.Where(m => expected.ContainsKey(Normalize(m.Path))).ToArray();
        Require(loaded.Length > 0 && loaded.Select(m => Normalize(m.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == loaded.Length &&
            loaded.Select(m => m.Wrapper).Distinct().Count() == loaded.Length &&
            loaded.Select(m => m.Instance).Distinct().Count() == loaded.Length &&
            loaded.Select(m => m.Resource).Distinct().Count() == loaded.Length,
            "Loaded DRL color models are missing, duplicated or shared.");
        renderOwner = ChargerRenderOwner.Capture(read, sameCar, module, vehicle, component, this.build);
        service = Pointer(module + this.build.AllocatorGlobalRva);
        Require(Heap(service), "DRL color renderer upload service is invalid.");
        backend = Pointer(service + 0x18);
        Require(Heap(backend), "DRL color renderer upload backend is invalid.");
        backendVtable = Pointer(backend); pages = Pointer(backend + 0x78); pagesEnd = Pointer(backend + 0x80);
        pageSize = Pointer(backend + 0x50); gpuBase = Pointer(backend + 0xA0);
        Require(Heap(pages) && pagesEnd > pages && pagesEnd - pages <= 0x10000 && (pagesEnd - pages) % 16 == 0 &&
            pageSize == 0x2000000 && backendVtable >= module && backendVtable <= module + this.build.SizeOfImage - 0x40,
            "DRL color renderer page table bounds are invalid.");
        ValidateBackend();
        foreach (var model in loaded)
        {
            Require(sameCar() && Heap(model.Wrapper) && Heap(model.Instance) && Heap(model.Resource) &&
                Pointer(model.Wrapper) == module + this.build.CarRenderModelWrapperVtableRva && Pointer(model.Wrapper + 0x60) == model.Instance &&
                Pointer(model.Wrapper + 0x68) == model.Instance - 0x10 && Pointer(model.Instance) == module + this.build.CarModelInstanceVtableRva &&
                Pointer(model.Instance + 0x20) == model.Resource && Pointer(model.Resource) == module + this.build.CarModelResourceVtableRva &&
                renderOwner.MatchesInstance(model.Instance), "DRL color model does not belong to the current player scene.");
            var path = Normalize(model.Path); var pathAddress = Pointer(model.Resource + 0x80);
            Require(ResourcePath(pathAddress).Equals(path, StringComparison.OrdinalIgnoreCase), "DRL color exact model resource changed.");
            var definition = Pointer(model.Instance + 0x28);
            Require(Heap(definition), "DRL color model definition is invalid.");
            var table = Pointer(definition + 0x140); var count = Pointer(definition + 0x148);
            Require(Heap(table) && count is > 0 and <= 256, "DRL color named-material table bounds are invalid.");
            var tableBytes = Bytes(table, checked((int)count * 0x38));
            foreach (var profile in expected[path])
            {
                var layout = profile.Resource == DrlColorProfileCatalog.EmitterResource ||
                    profile.Resource == DrlColorProfileCatalog.RegularEmitterResource ? EmitterLayout :
                    profile.Resource == DrlColorProfileCatalog.ProjectorLensResource ? ProjectorLensLayout :
                    profile.Resource == DrlColorProfileCatalog.TrafficOpaqueResource ? TrafficEmissionLayout :
                    profile.Resource == DrlColorProfileCatalog.GlassResource ? GlassLayout :
                    profile.Resource == DrlColorProfileCatalog.RegularGlassResource
                        ? profile.SurfaceTint is null ? RegularGlassLayout : RegularGlassSurfaceLayout :
                    DrlColorProfileCatalog.IsStandardRadiosityResource(profile.Resource) ? StandardRadiosityLayout : RadiosityLayout;
                var bufferSize = layout.BufferSize;
                var matches = Enumerable.Range(0, (int)count).Where(i => BitConverter.ToUInt32(tableBytes, i * 0x38) == profile.Id).ToArray();
                Require(matches.Length == 1, "DRL color archive material ID is missing or ambiguous.");
                var offset = matches[0] * 0x38; var record = table + (ulong)offset;
                var recordHeader = tableBytes.AsSpan(offset, 0x38).ToArray();
                Require(MaterialName(recordHeader) == profile.Name, "DRL color exact material name changed.");
                var material = BitConverter.ToUInt64(recordHeader, 0x28);
                Require(Heap(material) && Pointer(material) == module + this.build.MaterialVtableRva &&
                    Pointer(material - 0x10) == module + this.build.MaterialControlVtableRva && BitConverter.ToUInt64(recordHeader, 0x30) == material - 0x10,
                    "DRL color material strong ownership changed.");
                var begin = Pointer(material + 8); var end = Pointer(material + 0x10);
                Require(Heap(begin) && end > begin && end - begin <= 1024 && (end - begin) % 8 == 0,
                    "DRL color source parameter vector bounds are invalid.");
                var members = Bytes(begin, (int)(end - begin));
                var parameters = new List<Parameter>();
                for (var p = 0; p < members.Length; p += 8)
                    parameters.Add(CaptureParameter(BitConverter.ToUInt64(members, p)));
                Require(parameters.Select(p => p.Address).Distinct().Count() == parameters.Count,
                    "DRL color source parameter membership is duplicated.");
                var triggers = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + this.build.TriggerParameterVtableRva).ToArray();
                var colors = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + this.build.ColorParameterVtableRva).ToArray();
                var intensities = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + this.build.FloatParameterVtableRva &&
                    BitConverter.ToSingle(p.Header, 0x10) == profile.Intensity).ToArray();
                var tint = profile.Tint.SelectMany(BitConverter.GetBytes).ToArray();
                // Unrelated CH1 scalars commonly equal the authored intensity
                // (especially 1). The family's mapped field below identifies
                // intensity/exposure; this route never owns/writes it. Require a matching
                // typed source value and retain EVERY captured parameter header.
                var sourceTriggers = triggers.Select(p => BitConverter.ToUInt32(p.Header, 0x10)).Order().ToArray();
                var expectedTriggers = new uint[] { sourceTrigger }.Concat(profile.OtherTriggers ?? []).Order().ToArray();
                var tintMatches = colors.Count(p => p.Header.AsSpan(0x10, 16).SequenceEqual(tint));
                var emitterBase = profile.EmitterBaseTint?.SelectMany(BitConverter.GetBytes).ToArray();
                Require(emitterBase is null || colors.Length == 2 && tintMatches >= 1 &&
                    colors.Any(p => p.Header.AsSpan(0x10, 16).SequenceEqual(emitterBase)) &&
                    (!emitterBase.SequenceEqual(tint) || tintMatches == 2),
                    "DRL emitter needs exactly its separately authored emission and base Color sources.");
                var lensTint = profile.LensTint?.SelectMany(BitConverter.GetBytes).ToArray();
                Require(lensTint is null ||
                    (profile.LensTintSource == "CompiledWhite"
                        ? colors.Length == 1 && tintMatches == 1
                        : colors.Any(p => p.Header.AsSpan(0x10, 16).SequenceEqual(lensTint)) &&
                            (!lensTint.SequenceEqual(tint) || tintMatches >= 2)),
                    "Nonwhite DRL emission needs its complete qualified clear glass source proof.");
                var surfaceTint = profile.SurfaceTint?.SelectMany(BitConverter.GetBytes).ToArray();
                Require(surfaceTint is null ||
                    colors.Any(p => p.Header.AsSpan(0x10, 16).SequenceEqual(surfaceTint)) &&
                    (!surfaceTint.SequenceEqual(tint) || tintMatches >= 2),
                    "DRL glass surface and emission need their separately authored Color sources.");
                var switches = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + this.build.BoolParameterVtableRva && p.Header[0x10] == 1);
                // Inherited tint is allowed only by the exact family defaults key
                // validated above, with NO typed Color in the complete captured
                // vector. An explicit color cannot silently become a default.
                var tintSourceMatches = layout == EmitterLayout || layout == ProjectorLensLayout || layout == TrafficEmissionLayout
                    ? profile.TintSource == "ShaderDefault" ? colors.Length == 0 :
                        emitterBase is null ? colors.Length == 1 && tintMatches == 1 : colors.Length == 2 && tintMatches >= 1
                    : colors.Length is > 0 and <= 8 && tintMatches >= 1 &&
                        (layout == StandardRadiosityLayout || switches.Any());
                Require(sourceTriggers.SequenceEqual(expectedTriggers) &&
                    tintSourceMatches && intensities.Length >= 1,
                    "DRL color authored trigger/tint/intensity source is ambiguous or changed.");
                var compiled = Pointer(material + 0x58);
                Require(Heap(compiled), "DRL color compiled material is invalid.");
                var compiledHeader = Bytes(compiled, 0x80); var cpu = Pointer(compiled + 0x10);
                Require(Heap(cpu) && Pointer(compiled + 0x18) == cpu + (ulong)bufferSize,
                    "DRL color compiled shader size does not match its qualified family.");
                var original = Bytes(cpu, bufferSize);
                var externalHeader = Bytes(material + 0x40, 0x18);
                var external = BitConverter.ToUInt64(externalHeader);
                var externalEnd = BitConverter.ToUInt64(externalHeader, 8);
                Require(Heap(external) && externalEnd == external + (ulong)layout.ExternalBytes &&
                    BitConverter.ToUInt64(externalHeader, 0x10) == externalEnd,
                    "DRL color material external-binding vector does not match its qualified family.");
                var externalBindings = Bytes(external, layout.ExternalBytes);
                var externalIds = Enumerable.Range(0, layout.ExternalBytes / 4)
                    .Select(i => BitConverter.ToUInt32(externalBindings, i * 4)).ToArray();
                Require(externalIds.All(id => id != uint.MaxValue) && externalIds.Distinct().Count() == externalIds.Length &&
                    original.AsSpan(layout.TailOffset, layout.ExternalBytes).SequenceEqual(externalBindings) &&
                    original.AsSpan(layout.TailOffset + layout.ExternalBytes)
                        .SequenceEqual(new byte[bufferSize - layout.TailOffset - layout.ExternalBytes]),
                    "DRL color compiled external shader bindings do not match their material source.");
                Require(original.AsSpan(layout.Tint, 16).SequenceEqual(tint) &&
                    (emitterBase is null || original.AsSpan(0x10, 16).SequenceEqual(emitterBase)) &&
                    (lensTint is null || original.AsSpan(0x10, 16).SequenceEqual(lensTint)) &&
                    (layout.SurfaceTint is not { } surfaceOffset || surfaceTint is not null &&
                        original.AsSpan(surfaceOffset, 16).SequenceEqual(surfaceTint)) &&
                    BitConverter.ToSingle(original, layout.Intensity) == profile.Intensity &&
                    BitConverter.ToUInt32(original, layout.Trigger) == sourceTrigger &&
                    (layout.Switch is not { } switchOffset || BitConverter.ToUInt32(original, switchOffset) == 1) &&
                    (layout != EmitterLayout || BitConverter.ToUInt32(original, 0xC4) == 0 && BitConverter.ToUInt32(original, 0xC8) == 0) &&
                    // Regular emitter keeps the shader's channel-2 UV selector.
                    // Its D0 DWORD is captured and never owned, even during strobe.
                    (profile.Resource != DrlColorProfileCatalog.RegularEmitterResource || BitConverter.ToUInt32(original, 0xD0) == 0) &&
                    (profile.OtherTriggers is null || profile.OtherTriggers.Select((value, index) =>
                        BitConverter.ToUInt32(original, layout.Trigger + 4 + index * 4) == value).All(equal => equal)),
                    "DRL color shader semantics do not match the archive channel and family.");
                var textures = Pointer(compiled + 0x28);
                Require(Heap(textures) && Pointer(compiled + 0x30) == textures + (ulong)layout.TextureBytes &&
                    Pointer(compiled + 0x38) == textures + (ulong)layout.TextureBytes,
                    "DRL color family texture binding layout changed.");
                var textureBytes = Bytes(textures, layout.TextureBytes);
                var binding = Pointer(compiled + 0x70);
                Require(Heap(binding), "DRL color renderer binding is invalid.");
                var buffer = Pointer(binding); var descriptor = Bytes(binding + 0x10, 0x20);
                var gpu = BitConverter.ToUInt64(descriptor, 8);
                Require(Heap(buffer) && Pointer(buffer) == module + this.build.ConstantBufferVtableRva && Pointer(binding + 8) == buffer - 0x10 &&
                    gpu >= gpuBase && BitConverter.ToUInt32(descriptor, 0x18) == bufferSize && (descriptor[0x10] & 7) != 0 &&
                    (gpu - gpuBase) / pageSize < (pagesEnd - pages) / 16 && (gpu - gpuBase) % pageSize <= pageSize - (ulong)bufferSize,
                    "DRL color mapped emitter descriptor bounds are invalid.");
                var relative = gpu - gpuBase; var page = pages + relative / pageSize * 16;
                var pageHeader = Bytes(page, 16); var mappedBase = BitConverter.ToUInt64(pageHeader, 8); var pageOffset = relative % pageSize;
                Require(Heap(mappedBase) && mappedBase <= ulong.MaxValue - pageOffset - (ulong)bufferSize, "DRL color mapped address overflow.");
                var mapped = mappedBase + pageOffset;
                Require(Heap(mapped) && writable(mapped, bufferSize) && Bytes(mapped, bufferSize).SequenceEqual(original),
                    "DRL color complete writable renderer buffer must equal its CPU source.");
                targets.Add(new Target
                {
                    Layout = layout,
                    Wrapper = model.Wrapper, Instance = model.Instance, Resource = model.Resource, PathAddress = pathAddress, Path = path,
                    Definition = definition, Table = table, Count = count, TableBytes = tableBytes, Record = record, RecordHeader = recordHeader,
                    Material = material, Name = profile.Name, Begin = begin, End = end, Members = members, Parameters = parameters.ToArray(),
                    External = external, ExternalHeader = externalHeader, ExternalBindings = externalBindings,
                    Compiled = compiled, CompiledHeader = compiledHeader, Cpu = cpu, Binding = binding, Buffer = buffer, Descriptor = descriptor,
                    Page = page, PageHeader = pageHeader, Mapped = mapped, Textures = textures, TextureBytes = textureBytes,
                    Original = original, Last = original.ToArray(), Flags = Bytes(material + 0x80, 1)[0]
                });
            }
        }
        Require(targets.Count is > 0 and <= 512 && targets.Select(t => t.Material).Distinct().Count() == targets.Count &&
            targets.Select(t => t.Compiled).Distinct().Count() == targets.Count && targets.Select(t => t.Cpu).Distinct().Count() == targets.Count &&
            targets.Select(t => t.Binding).Distinct().Count() == targets.Count && targets.Select(t => t.Buffer).Distinct().Count() == targets.Count,
            "DRL color destinations are shared or ambiguous.");
        var protectedRanges = targets.SelectMany(ProtectedRanges).ToArray();
        foreach (var target in targets)
            Require(!targets.Any(other => target != other && Overlaps(target.Mapped, target.Layout.BufferSize, other.Mapped, other.Layout.BufferSize)) &&
                !protectedRanges.Any(range => Overlaps(target.Mapped, target.Layout.BufferSize, range.Address, range.Length)),
                "DRL color renderer storage aliases another destination or source object.");
        Validate();
    }

    private Parameter CaptureParameter(ulong address)
    {
        Require(Heap(address), "DRL color source parameter pointer is invalid.");
        var vt = Pointer(address);
        var length = vt == module + this.build.ColorParameterVtableRva || vt == module + this.build.VectorParameterVtableRva ? 0x20 :
            vt == module + this.build.TextureParameterVtableRva ? 0x40 :
            vt == module + this.build.TriggerParameterVtableRva || vt == module + this.build.FloatParameterVtableRva || vt == module + this.build.BoolParameterVtableRva ? 0x18 : 0;
        Require(length != 0, "DRL color source parameter type is unsupported.");
        var header = Bytes(address, length); TextureSource? texture = null;
        if (vt == module + this.build.TextureParameterVtableRva)
        {
            var resource = BitConverter.ToUInt64(header, 0x10); var pathAddress = BitConverter.ToUInt64(header, 0x18);
            var pathLength = BitConverter.ToUInt64(header, 0x28); var capacity = BitConverter.ToUInt64(header, 0x30);
            Require(header[8] == 5 && Heap(resource) && Pointer(resource) == module + this.build.TextureResourceVtableRva && Heap(pathAddress) &&
                pathLength is >= 16 and <= 512 && capacity >= pathLength && capacity <= 1024,
                "DRL color source texture bounds are unsupported.");
            texture = new(resource, pathAddress, Bytes(pathAddress, (int)pathLength));
        }
        return new(address, header, texture);
    }
    private static IEnumerable<(ulong Address, int Length)> ProtectedRanges(Target t)
    {
        yield return (t.Wrapper, 0x70); yield return (t.Instance, 0x88); yield return (t.Resource, 0x88);
        yield return (t.PathAddress, 512); yield return (t.Definition, 0x150); yield return (t.Table, t.TableBytes.Length);
        yield return (t.Material - 0x10, 0x98); yield return (t.Begin, t.Members.Length);
        yield return (t.External, t.ExternalBindings.Length); yield return (t.Compiled, 0x80);
        yield return (t.Cpu, t.Layout.BufferSize); yield return (t.Binding, 0x30); yield return (t.Buffer - 0x10, 0x20);
        yield return (t.Page, 16); yield return (t.Textures, t.TextureBytes.Length);
        var nameLength = BitConverter.ToUInt64(t.RecordHeader, 0x18);
        if (BitConverter.ToUInt64(t.RecordHeader, 0x20) >= 16)
            yield return (BitConverter.ToUInt64(t.RecordHeader, 8), (int)nameLength);
        foreach (var parameter in t.Parameters)
        {
            yield return (parameter.Address, parameter.Header.Length);
            if (parameter.Texture is { } texture)
            { yield return (texture.Resource, 8); yield return (texture.PathAddress, texture.Path.Length); }
        }
    }
    private void ValidateBackend()
    {
        Require(Pointer(module + this.build.AllocatorGlobalRva) == service && Pointer(service + 0x18) == backend && Pointer(backend) == backendVtable &&
            Pointer(backend + 0x78) == pages && Pointer(backend + 0x80) == pagesEnd && Pointer(backend + 0x50) == pageSize &&
            Pointer(backend + 0xA0) == gpuBase && Pointer(backendVtable + 0x38) == module + this.build.UploadRva &&
            Bytes(module + this.build.UploadRva, UploadSignature.Length).SequenceEqual(UploadSignature) &&
            Bytes(module + this.build.WalkRva, WalkSignature.Length).SequenceEqual(WalkSignature),
            "DRL color renderer upload identity/signatures changed.");
    }
    internal void Validate(bool restoring = false)
    {
        renderOwner.Validate(); Require(sameCar(), "DRL color player/build identity changed."); ValidateBackend();
        Require(pending is null || targets.Contains(pending.Target) && pending.Target.Owned.Contains(pending.Offset) &&
            pending.Target.Layout.OwnedOffsets.Contains(pending.Offset), "DRL color pending write has no owned destination.");
        foreach (var t in targets)
        {
            Require(t.Owned.All(t.Layout.OwnedOffsets.Contains) &&
                (pending is null || pending.Target.Layout.OwnedOffsets.Contains(pending.Offset)),
                "DRL color attempted fields exceed color/native-strobe ownership.");
            // Fresh contiguous headers cover exactly the same pointer/field
            // proofs with fewer process reads. No snapshot survives Validate().
            var wrapper = Bytes(t.Wrapper, 0x70);
            var instance = Bytes(t.Instance, 0x30);
            var resource = Bytes(t.Resource, 0x88);
            var definition = Bytes(t.Definition + 0x140, 16);
            var material = Bytes(t.Material - 0x10, 0x98);
            var compiled = Bytes(t.Compiled, 0x80);
            var binding = Bytes(t.Binding, 0x30);
            Require(BitConverter.ToUInt64(wrapper, 0) == module + this.build.CarRenderModelWrapperVtableRva && BitConverter.ToUInt64(wrapper, 0x60) == t.Instance &&
                BitConverter.ToUInt64(wrapper, 0x68) == t.Instance - 0x10 && BitConverter.ToUInt64(instance, 0) == module + this.build.CarModelInstanceVtableRva &&
                BitConverter.ToUInt64(instance, 0x20) == t.Resource && BitConverter.ToUInt64(resource, 0) == module + this.build.CarModelResourceVtableRva &&
                renderOwner.MatchesInstance(t.Instance) && BitConverter.ToUInt64(resource, 0x80) == t.PathAddress &&
                ResourcePath(t.PathAddress).Equals(t.Path, StringComparison.OrdinalIgnoreCase) &&
                BitConverter.ToUInt64(instance, 0x28) == t.Definition && BitConverter.ToUInt64(definition, 0) == t.Table &&
                BitConverter.ToUInt64(definition, 8) == t.Count && Bytes(t.Table, t.TableBytes.Length).SequenceEqual(t.TableBytes) &&
                MaterialName(t.RecordHeader) == t.Name && BitConverter.ToUInt64(material, 0x10) == module + this.build.MaterialVtableRva &&
                BitConverter.ToUInt64(material, 0) == module + this.build.MaterialControlVtableRva && BitConverter.ToUInt64(material, 0x18) == t.Begin &&
                BitConverter.ToUInt64(material, 0x20) == t.End && Bytes(t.Begin, t.Members.Length).SequenceEqual(t.Members) &&
                material.AsSpan(0x50, 0x18).SequenceEqual(t.ExternalHeader) &&
                Bytes(t.External, t.ExternalBindings.Length).SequenceEqual(t.ExternalBindings) &&
                t.Parameters.All(p => Bytes(p.Address, p.Header.Length).SequenceEqual(p.Header) &&
                    (p.Texture is not { } texture || Pointer(texture.Resource) == module + this.build.TextureResourceVtableRva &&
                        Bytes(texture.PathAddress, texture.Path.Length).SequenceEqual(texture.Path))) &&
                material[0x90] == t.Flags && BitConverter.ToUInt64(material, 0x68) == t.Compiled &&
                compiled.SequenceEqual(t.CompiledHeader) && BitConverter.ToUInt64(compiled, 0x10) == t.Cpu &&
                BitConverter.ToUInt64(compiled, 0x18) == t.Cpu + (ulong)t.Layout.BufferSize && Bytes(t.Cpu, t.Layout.BufferSize).SequenceEqual(t.Original) &&
                Bytes(t.Textures, t.TextureBytes.Length).SequenceEqual(t.TextureBytes) && BitConverter.ToUInt64(compiled, 0x70) == t.Binding &&
                BitConverter.ToUInt64(binding, 0) == t.Buffer && BitConverter.ToUInt64(binding, 8) == t.Buffer - 0x10 && Pointer(t.Buffer) == module + this.build.ConstantBufferVtableRva &&
                binding.AsSpan(0x10, 0x20).SequenceEqual(t.Descriptor) && Bytes(t.Page, 16).SequenceEqual(t.PageHeader) &&
                writable(t.Mapped, t.Layout.BufferSize), "DRL color rooted material/source/renderer ownership changed; no stale writes.");
            var current = Bytes(t.Mapped, t.Layout.BufferSize);
            var fields = t.Owned.Select(offset => (Offset: offset, Applied:
                pending is { } operation && operation.Target == t && operation.Offset == offset
                    ? operation.Desired : t.Last.AsSpan(offset, 4).ToArray())).ToArray();
            // A writer can fail before or after a whole DWORD takes effect.
            // During recovery, its previous owned value is also recognized.
            if (restoring && pending is { } interrupted && interrupted.Target == t &&
                current.AsSpan(interrupted.Offset, 4).SequenceEqual(interrupted.Previous))
                interrupted.Desired.CopyTo(current, interrupted.Offset);
            Require(ChargerDrlRoute.IsOwnedBuffer(current, t.Original, fields, restoring),
                "DRL color renderer changed outside owned color/trigger DWORDs; restoration stopped.");
        }
        renderOwner.Validate(); Require(sameCar(), "DRL color identity changed during validation.");
    }
    internal void SetColor(float r, float g, float b)
        => SetColor(r, g, b, null);

    internal bool SetColor(float r, float g, float b, Func<bool>? shouldYield)
    {
        Require(sourceTrigger == 15, "Native indicator proofs are read-only and cannot recolor signal bulbs.");
        Require(new[] { r, g, b }.All(value => float.IsFinite(value) && value is >= 0 and <= DrlColorSelection.MaximumEmissionChannel),
            "DRL emission channels must be finite values between zero and four.");
        Require(pending is null, "An interrupted DRL color write must restore before another color update.");
        if (shouldYield?.Invoke() == true) return false;
        Validate(); var values = new[] { r, g, b };
        foreach (var target in targets)
            for (var index = 0; index < target.Layout.ColorOffsets.Length; index++)
            {
                // Yield only between committed DWORDs. Every ownership guard
                // still runs; a handoff never leaves an interrupted write pending.
                if (shouldYield?.Invoke() == true) return false;
                var offset = target.Layout.ColorOffsets[index];
                // Boost emission RGB only. A separately owned diffuse surface
                // keeps its hue and remains at or below its existing 1x ceiling.
                var value = values[index % 3];
                if (index >= 3) value /= Math.Max(1f, values.Max());
                var desired = BitConverter.GetBytes(value);
                if (target.Last.AsSpan(offset, 4).SequenceEqual(desired)) continue;
                Validate();
                if (shouldYield?.Invoke() == true) return false;
                var previous = target.Last.AsSpan(offset, 4).ToArray();
                target.Owned.Add(offset); pending = new(target, offset, previous, desired);
                write(target.Mapped + (ulong)offset, desired); Validate();
                desired.CopyTo(target.Last, offset); pending = null;
                // The post-write Validate already read back the exact desired
                // DWORD and all ownership fields; committing bookkeeping is local.
            }
        return true;
    }

    internal bool SetNativeStrobe(bool enabled, Func<bool>? shouldYield = null)
    {
        Require(sourceTrigger == 15, "Native indicator proofs are read-only and cannot strobe signal bulbs.");
        Require(pending is null, "An interrupted DRL write must restore before another update.");
        if (shouldYield?.Invoke() == true) return false;
        if (!enabled && targets.All(target => !target.Owned.Contains(target.Layout.Trigger))) return true;
        Validate();
        foreach (var target in targets)
        {
            if (shouldYield?.Invoke() == true) return false;
            var offset = target.Layout.Trigger;
            var desired = enabled ? BitConverter.GetBytes(NativeDrlStrobeSource.Trigger)
                : target.Original.AsSpan(offset, 4).ToArray();
            if (target.Last.AsSpan(offset, 4).SequenceEqual(desired)) continue;
            Validate();
            if (shouldYield?.Invoke() == true) return false;
            var previous = target.Last.AsSpan(offset, 4).ToArray();
            target.Owned.Add(offset); pending = new(target, offset, previous, desired);
            write(target.Mapped + (ulong)offset, desired); Validate();
            desired.CopyTo(target.Last, offset); pending = null;
                // The post-write Validate already read back the exact desired
                // DWORD and all ownership fields; committing bookkeeping is local.
            if (!enabled) target.Owned.Remove(offset);
        }
        return true;
    }

    internal void Restore()
    {
        Validate(restoring: true);
        // Recover an interrupted color update first. Its attempted value can
        // differ from both the prior frame and the captured authored value.
        if (pending is { } interrupted) RestoreField(interrupted.Target, interrupted.Offset);
        foreach (var target in targets.AsEnumerable().Reverse())
            foreach (var offset in target.Layout.OwnedOffsets.Reverse().Where(target.Owned.Contains).ToArray())
                RestoreField(target, offset);
        Validate(); Require(!HasWrites, "DRL color restoration still owns an incomplete write.");
    }
    private void RestoreField(Target target, int offset)
    {
        Validate(restoring: true);
        var desired = target.Original.AsSpan(offset, 4).ToArray();
        var previous = Bytes(target.Mapped + (ulong)offset, 4);
        if (!previous.SequenceEqual(desired))
        {
            pending = new(target, offset, previous, desired);
            write(target.Mapped + (ulong)offset, desired); Validate(restoring: true);
            Require(Bytes(target.Mapped + (ulong)offset, 4).SequenceEqual(desired), "DRL color restoration did not persist.");
        }
        desired.CopyTo(target.Last, offset); target.Owned.Remove(offset);
        if (pending is { } operation && operation.Target == target && operation.Offset == offset) pending = null;
        Validate(restoring: true);
    }
}
