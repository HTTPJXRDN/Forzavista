using System.Text;

// Shared guarded GTAm-only route: its stock headlightL model contains BOTH
// front lamps. One shared emitter buffer cannot establish left/right support.
// Only its trigger, green/blue tint and optional emission-intensity DWORDs are owned; no game functions,
// source parameters, CPU shader values, flags or disk assets are changed.
internal sealed class GiuliaMergedDrlRoute
{
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<bool> sameCar;
    private readonly Func<ulong, int, bool> writable;
    private readonly ulong module, vehicle, component, presentation, owner, ownerContext;
    private readonly ulong wrapper, instance, resource, definition, table, record, material, begin, end;
    private readonly ulong trigger, color, intensity, compiled, cpu, binding, buffer, page, mapped;
    private readonly ulong service, backend, backendVtable, pages, pagesEnd, pageSize, gpuBase;
    private readonly byte[] triggerHeader, colorHeader, intensityHeader, members, descriptor, pageHeader, original;
    private readonly bool brighter;
    private readonly byte flags;
    private readonly List<(int Offset, byte[] Original, byte[] Applied)> edits = [];
    private readonly HashSet<int> attempted = [];
    private readonly string name;
    private ulong Pointer(ulong address) => BitConverter.ToUInt64(read(address, 8));
    private static bool Heap(ulong address) => address is >= 0x10000000 and < 0x7FF000000000 && (address & 7) == 0;

    internal GiuliaMergedDrlRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, bool brighter = false)
    {
        this.read = read; this.write = write; this.sameCar = sameCar; this.writable = writable;
        this.module = module; this.vehicle = vehicle; this.component = component; this.brighter = brighter;
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "headlightl_a.modelbin", "headlightlbulbs_a.modelbin", "headlightrbulbs_a.modelbin", "taillightl_a.modelbin", "taillightr_a.modelbin" };
        string FileName(string path) => path[(path.LastIndexOf('\\') + 1)..];
        // Non-target bulbs/rears can unload with LOD/scene changes. Require a
        // UNIQUE exact merged headlamp plus its strong current-component owner,
        // not a fixed total count of unrelated lamps.
        if (!sameCar() || models.Count is < 1 or > 5 ||
            models.Any(m => !m.Path.Contains("\\alf_giuliagtam_21\\", StringComparison.OrdinalIgnoreCase) ||
                !expectedPaths.Contains(FileName(m.Path))) ||
            models.Select(m => FileName(m.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != models.Count ||
            models.Count(m => FileName(m.Path).Equals("headlightl_a.modelbin", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("Unique exact Giulia merged headlamp required; no writes.");
        var model = models.Single(m => FileName(m.Path).Equals("headlightl_a.modelbin", StringComparison.OrdinalIgnoreCase));
        wrapper = model.Wrapper; instance = model.Instance; resource = model.Resource;
        presentation = Pointer(vehicle + 0x7918); owner = Pointer(instance + 0x38); ownerContext = Pointer(owner + 0x18);
        // Unlike the Charger's bindings+90 bridge, Giulia's verified strong
        // back-reference comes from the shared model context to the CURRENT
        // component's control block. Proximity is never write authorization.
        if (!Heap(presentation) || !Heap(owner) || !Heap(ownerContext) ||
            Pointer(component + 0xB8) != presentation || Pointer(owner) != module + 0x6474B18 ||
            Pointer(ownerContext) != module + 0x6474B28 || Pointer(ownerContext + 0x50) != component - 0x10 ||
            models.Any(m => Pointer(m.Instance + 0x38) != owner))
            throw new InvalidOperationException("Giulia shared lamp context does not own the current player component; no writes.");
        definition = Pointer(instance + 0x28); table = Pointer(definition + 0x140);
        if (!Heap(definition) || !Heap(table) || Pointer(definition + 0x148) != 21)
            throw new InvalidOperationException("Giulia merged headlamp table changed; no writes.");
        var entries = read(table, 21 * 0x38);
        var matches = new List<int>();
        for (var i = 0; i < 21; i++)
            if (BitConverter.ToUInt32(entries, i * 0x38) == 4) matches.Add(i * 0x38);
        if (matches.Count != 1) throw new InvalidOperationException("Giulia authored DRL material ID ambiguous.");
        var offset = matches[0]; record = table + (ulong)offset;
        var length = BitConverter.ToUInt64(entries, offset + 0x18); var capacity = BitConverter.ToUInt64(entries, offset + 0x20);
        if (length is < 1 or > 127 || capacity < length || capacity > 255)
            throw new InvalidOperationException("Giulia material name bounds invalid.");
        name = Encoding.ASCII.GetString(capacity < 16 ? entries.AsSpan(offset + 8, (int)length).ToArray()
            : read(BitConverter.ToUInt64(entries, offset + 8), (int)length));
        if (name != "lights_CH1Bump_CH1LMask_b") throw new InvalidOperationException("Giulia exact DRL material name mismatch.");
        material = BitConverter.ToUInt64(entries, offset + 0x28);
        if (!Heap(material) || Pointer(material) != module + 0x65EB720 || Pointer(material - 0x10) != module + 0x6475280 ||
            BitConverter.ToUInt64(entries, offset + 0x30) != material - 0x10)
            throw new InvalidOperationException("Giulia material strong ownership changed.");
        begin = Pointer(material + 8); end = Pointer(material + 0x10);
        if (!Heap(begin) || end < begin || end - begin > 1024 || (end - begin) % 8 != 0)
            throw new InvalidOperationException("Giulia source parameter bounds invalid.");
        members = read(begin, (int)(end - begin));
        var triggers = new List<ulong>(); var colors = new List<ulong>(); var intensities = new List<ulong>();
        for (var p = 0; p < members.Length; p += 8)
        {
            var address = BitConverter.ToUInt64(members, p);
            if (!Heap(address)) throw new InvalidOperationException("Giulia source parameter pointer invalid.");
            var header = read(address, 0x18);
            if (BitConverter.ToUInt64(header) == module + 0x65786F8 && BitConverter.ToUInt32(header, 0x10) == 15) triggers.Add(address);
            if (BitConverter.ToUInt64(header) == module + 0x6578438) colors.Add(address);
            // Authored explicit Emissive_Intensity_2 is 1.414 on this exact
            // material. Require one typed source AND its matching shader
            // scalar; compact property IDs are not stable across launches.
            if (BitConverter.ToUInt64(header) == module + 0x6578478 && BitConverter.ToSingle(header, 0x10) == 1.414f)
                intensities.Add(address);
        }
        if (triggers.Count != 1 || colors.Count != 1 || intensities.Count != 1)
            throw new InvalidOperationException("Giulia explicit trigger/tint/intensity set ambiguous.");
        trigger = triggers[0]; color = colors[0]; triggerHeader = read(trigger, 0x18); colorHeader = read(color, 0x20);
        intensity = intensities[0]; intensityHeader = read(intensity, 0x18);
        var white = Enumerable.Repeat(1f, 4).SelectMany(BitConverter.GetBytes).ToArray();
        if (!colorHeader.AsSpan(0x10, 16).SequenceEqual(white)) throw new InvalidOperationException("Giulia authored white emission tint changed.");
        compiled = Pointer(material + 0x58); cpu = Pointer(compiled + 0x10);
        if (!Heap(compiled) || !Heap(cpu) || Pointer(compiled + 0x18) != cpu + 0x100)
            throw new InvalidOperationException("Giulia single-channel emitter layout mismatch.");
        original = read(cpu, 0x100);
        if (!ValidEmissionLayout(original, BitConverter.ToSingle(intensityHeader, 0x10)))
            throw new InvalidOperationException("Giulia emitter trigger/color shader signature mismatch.");
        service = Pointer(module + 0xA82C398); backend = Pointer(service + 0x18); backendVtable = Pointer(backend);
        pages = Pointer(backend + 0x78); pagesEnd = Pointer(backend + 0x80); pageSize = Pointer(backend + 0x50); gpuBase = Pointer(backend + 0xA0);
        if (!Heap(service) || !Heap(backend) || !Heap(pages) || pagesEnd <= pages || pagesEnd - pages > 0x10000 ||
            (pagesEnd - pages) % 16 != 0 || pageSize != 0x2000000 || backendVtable < module || backendVtable + 0x40 >= module + 0xB3C0000 ||
            Pointer(backendVtable + 0x38) != module + 0x14FEC00 ||
            !read(module + 0x14FEC00, 0x11).SequenceEqual(Convert.FromHexString("4C8BDC49895B205556574883EC70498B18")) ||
            !read(module + 0x14FF170, 0x33).SequenceEqual(Convert.FromHexString(
                "48895C241855565741544155415641574883EC204D8BF94C8BD24C8BE14C2B91A00000004D03D04C8B415033D2498BC249F7F0")))
            throw new InvalidOperationException("Giulia upload backend identity changed.");
        binding = Pointer(compiled + 0x70); buffer = Pointer(binding); descriptor = read(binding + 0x10, 0x20);
        var gpu = BitConverter.ToUInt64(descriptor, 8);
        if (!Heap(binding) || !Heap(buffer) || Pointer(buffer) != module + 0x65E6788 || Pointer(binding + 8) != buffer - 0x10 ||
            gpu < gpuBase || BitConverter.ToUInt32(descriptor, 0x18) != 0x100 || (descriptor[0x10] & 7) == 0 ||
            (gpu - gpuBase) / pageSize >= (pagesEnd - pages) / 16 || (gpu - gpuBase) % pageSize + 0x100 > pageSize)
            throw new InvalidOperationException("Giulia renderer descriptor range mismatch.");
        var relative = gpu - gpuBase; page = pages + relative / pageSize * 16; pageHeader = read(page, 16);
        mapped = BitConverter.ToUInt64(pageHeader, 8) + relative % pageSize;
        if (!Heap(mapped) || !writable(mapped, 0x100) || !read(mapped, 0x100).SequenceEqual(original))
            throw new InvalidOperationException("Giulia full mapped buffer must equal its CPU source and be writable.");
        flags = read(material + 0x80, 1)[0];
        foreach (var field in new[] { (Offset: 0xC0, Value: BitConverter.GetBytes(22u)),
            (Offset: 0x44, Value: BitConverter.GetBytes(.35f)), (Offset: 0x48, Value: BitConverter.GetBytes(.015f)) })
            edits.Add((field.Offset, original.AsSpan(field.Offset, 4).ToArray(), field.Value));
        if (brighter) edits.Add((0xB0, original.AsSpan(0xB0, 4).ToArray(), BitConverter.GetBytes(8f)));
        Validate();
        Console.WriteLine($"Verified Giulia merged current-player DRL emitter; BOTH-front amber hazard route {(brighter ? "with temporary emission intensity 1.414 -> 8" : "at original intensity")} prepared without writes. No independent-side claim.");
    }

    internal void Validate(bool restoring = false)
    {
        if (!sameCar() || Pointer(vehicle + 0x7918) != presentation || Pointer(component + 0xB8) != presentation ||
            Pointer(owner) != module + 0x6474B18 || Pointer(owner + 0x18) != ownerContext || Pointer(ownerContext) != module + 0x6474B28 ||
            Pointer(ownerContext + 0x50) != component - 0x10 || Pointer(wrapper) != module + 0x65C6408 ||
            Pointer(wrapper + 0x60) != instance || Pointer(wrapper + 0x68) != instance - 0x10 || Pointer(instance) != module + 0x6474E38 ||
            Pointer(instance + 0x20) != resource || Pointer(resource) != module + 0x6474CA8 || Pointer(instance + 0x38) != owner ||
            Pointer(instance + 0x28) != definition || Pointer(definition + 0x140) != table || Pointer(definition + 0x148) != 21 ||
            BitConverter.ToUInt32(read(record, 4)) != 4 || Pointer(record + 0x28) != material || Pointer(record + 0x30) != material - 0x10 ||
            Pointer(material) != module + 0x65EB720 || Pointer(material - 0x10) != module + 0x6475280 ||
            Pointer(material + 8) != begin || Pointer(material + 0x10) != end || !read(begin, members.Length).SequenceEqual(members) ||
            !read(trigger, 0x18).SequenceEqual(triggerHeader) || !read(color, 0x20).SequenceEqual(colorHeader) ||
            !read(intensity, 0x18).SequenceEqual(intensityHeader) ||
            read(material + 0x80, 1)[0] != flags || Pointer(material + 0x58) != compiled || Pointer(compiled + 0x10) != cpu ||
            Pointer(compiled + 0x18) != cpu + 0x100 || !read(cpu, 0x100).SequenceEqual(original))
            throw new InvalidOperationException("Giulia current-player/source ownership changed; no stale writes.");
        var nameLength = Pointer(record + 0x18); var capacity = Pointer(record + 0x20);
        if (nameLength != (ulong)name.Length || capacity < nameLength || capacity > 255 ||
            Encoding.ASCII.GetString(capacity < 16 ? read(record + 8, (int)nameLength) : read(Pointer(record + 8), (int)nameLength)) != name)
            throw new InvalidOperationException("Giulia named material identity changed.");
        if (Pointer(module + 0xA82C398) != service || Pointer(service + 0x18) != backend || Pointer(backend) != backendVtable ||
            Pointer(backend + 0x78) != pages || Pointer(backend + 0x80) != pagesEnd || Pointer(backend + 0x50) != pageSize ||
            Pointer(backend + 0xA0) != gpuBase || Pointer(compiled + 0x70) != binding || Pointer(binding) != buffer ||
            Pointer(binding + 8) != buffer - 0x10 || Pointer(buffer) != module + 0x65E6788 ||
            !read(binding + 0x10, 0x20).SequenceEqual(descriptor) || !read(page, 16).SequenceEqual(pageHeader) || !writable(mapped, 0x100))
            throw new InvalidOperationException("Giulia mapped renderer ownership changed.");
        if (!ChargerDrlRoute.IsOwnedBuffer(read(mapped, 0x100), original,
            edits.Where(e => attempted.Contains(e.Offset)).Select(e => (e.Offset, e.Applied)).ToArray(), restoring))
            throw new InvalidOperationException("Giulia renderer changed outside owned DWORDs; restoration stopped.");
    }
    internal void Apply()
    {
        foreach (var e in edits)
        {
            Validate(); attempted.Add(e.Offset); write(mapped + (ulong)e.Offset, e.Applied); Validate();
        }
        Console.WriteLine($"Only merged front DRL trigger, green/blue tint{(brighter ? " and emission intensity" : "")} changed; all rear, multi-channel headlights, CPU sources and flags untouched.");
    }
    internal void Restore()
    {
        foreach (var e in edits.Where(e => attempted.Contains(e.Offset)).Reverse().ToArray())
        {
            Validate(restoring: true); write(mapped + (ulong)e.Offset, e.Original); attempted.Remove(e.Offset); Validate(restoring: true);
        }
        Validate();
        Console.WriteLine("RESTORED AND VERIFIED: Giulia complete merged front DRL renderer buffer exactly matches its original bytes.");
    }
    private static bool ValidEmissionLayout(byte[] values, float sourceIntensity) => values.Length == 0x100 &&
        sourceIntensity == 1.414f && BitConverter.ToSingle(values, 0xB0) == sourceIntensity &&
        BitConverter.ToUInt32(values, 0xC0) == 15 && BitConverter.ToUInt32(values, 0xC4) == 0 &&
        BitConverter.ToUInt32(values, 0xC8) == 0 &&
        values.AsSpan(0x40, 16).SequenceEqual(Enumerable.Repeat(1f, 4).SelectMany(BitConverter.GetBytes).ToArray()) &&
        values.AsSpan(0xF0, 16).SequenceEqual(Convert.FromHexString("11010000120100000000000000000000"));
    internal static void SelfTest()
    {
        var count = 0;
        void Check(bool ok) { if (!ok) throw new InvalidOperationException($"Giulia guard check {count + 1} failed."); count++; }
        var original = new byte[0x100];
        Enumerable.Repeat(1f, 4).SelectMany(BitConverter.GetBytes).ToArray().CopyTo(original, 0x40);
        BitConverter.GetBytes(1.414f).CopyTo(original, 0xB0); BitConverter.GetBytes(15u).CopyTo(original, 0xC0);
        Convert.FromHexString("11010000120100000000000000000000").CopyTo(original, 0xF0);
        Check(ValidEmissionLayout(original, 1.414f));
        Check(!ValidEmissionLayout(original[..255], 1.414f));
        Check(!ValidEmissionLayout(original, float.NaN));
        Check(!ValidEmissionLayout(original, 8f));
        foreach (var offset in new[] { 0x40, 0x44, 0x48, 0x4C, 0xB0, 0xC0, 0xC4, 0xC8, 0xF0 })
        { var foreign = original.ToArray(); foreign[offset] ^= 1; Check(!ValidEmissionLayout(foreign, 1.414f)); }
        var field = (Offset: 0xB0, Applied: BitConverter.GetBytes(8f)); var applied = original.ToArray(); field.Applied.CopyTo(applied, field.Offset);
        Check(ChargerDrlRoute.IsOwnedBuffer(applied, original, [field], false));
        Check(ChargerDrlRoute.IsOwnedBuffer(original, original, [field], true));
        Check(!ChargerDrlRoute.IsOwnedBuffer(original, original, [field], false));
        var badAlpha = applied.ToArray(); badAlpha[0x4C] ^= 1;
        Check(!ChargerDrlRoute.IsOwnedBuffer(badAlpha, original, [field], true));
        Check(!ValidEmissionLayout(applied, 1.414f));
        Console.WriteLine($"PASS: {count} pure Giulia emitter/intensity ownership checks; no game process opened.");
    }
}
