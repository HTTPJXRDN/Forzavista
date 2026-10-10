using System.Text;

// Guarded Steam 6.461.691.0 RS6 upgraded-mirror development route.
// Caller must supply an exact-build/exact-car sameCar identity guard. Both _b
// instances must belong to the current player's rooted Scene Pose. Only mapped
// emitter RGB/intensity DWORDs are owned, plus the selected mirror's trigger
// DWORD for selected sides. Combined70 routing retains the selected mirror's
// authored trigger and disconnects the opposite mirror to held-OFF22.
// Alpha, glass, CPU/source parameters, flags and disk assets are never written.
internal sealed class Rs6MirrorRoute
{
    private sealed record Parameter(ulong Address, byte[] Header);
    private sealed record Target(ulong Wrapper, ulong Instance, ulong Resource, ulong PathAddress, string Path,
        ulong Definition, ulong Table, ulong Count, ulong Record, byte[] RecordHeader, uint Id, string Name,
        ulong Material, ulong Begin, ulong End, byte[] Members, Parameter[] Parameters,
        ulong Trigger, byte[] TriggerHeader, ulong Color, byte[] ColorHeader, ulong Intensity, byte[] IntensityHeader,
        ulong Compiled, ulong Cpu, ulong Binding, ulong Buffer, byte[] Descriptor, ulong Page, byte[] PageHeader,
        ulong Mapped, byte[] Original, byte Flags, bool Left);
    private sealed record Edit(Target Target, int Offset, byte[] Original, byte[] Applied);
    private const int BufferSize = 0x100;
    private const string LeftPath = @"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\Doors\wingMirrorL_b.modelbin";
    private const string RightPath = @"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\Doors\wingMirrorR_b.modelbin";
    private static readonly byte[] Tail = Convert.FromHexString("11010000120100000000000000000000");
    // Exact observed launch signatures. Only trailing binding words differ;
    // do not accept arbitrary tails or rewrite any of these bytes.
    private static readonly byte[] RestartTail = Convert.FromHexString("05010000060100000000000000000000");
    // Steam 6.461.691.0 read-only rs6-lamps-readonly.log: both mirrors
    // retain authored source/CPU semantics and exact CPU/mapped equality.
    private static readonly byte[] UpdatedBuildTail = Convert.FromHexString("FB000000FC0000000000000000000000");
    private static readonly byte[] UploadSignature = Convert.FromHexString("4C8BDC49895B205556574883EC70498B18");
    private static readonly byte[] WalkSignature = Convert.FromHexString(
        "48895C241855565741544155415641574883EC204D8BF94C8BD24C8BE14C2B91A00000004D03D04C8B415033D2498BC249F7F0");
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<bool> sameCar;
    private readonly Func<ulong, int, bool> writable;
    private readonly ulong module, service, backend, backendVtable, pages, pagesEnd, pageSize, gpuBase;
    private readonly ChargerRenderOwner renderOwner;
    private readonly uint output;
    private readonly uint nativeTrigger;
    private readonly List<Target> targets = [];
    private readonly List<Edit> edits = [];
    private readonly HashSet<Edit> attempted = [];
    private readonly Rs6MirrorMaskCandidate? maskCandidate;
    internal IEnumerable<(ulong Address, int Size)> ExtraStorage => maskCandidate?.Storage ?? [];
    internal IEnumerable<(ulong Mapped, ulong Cpu)> Buffers => targets.Select(t => (t.Mapped, t.Cpu));
    internal float LeftIntensity { get; }
    internal float RightIntensity { get; }

    private static bool Heap(ulong address) => address is >= 0x10000000 and < 0x7FF000000000 && (address & 7) == 0;
    private static string Normalize(string path) => path.Replace('/', '\\');
    private static uint PropertyId(byte[] header) => BitConverter.ToUInt32(header, 9) & 0xFFFFFF;
    private static byte[] AuthoredTint(bool left) => (left ? new[] { .71569353f, .791298f, .9646863f, 1f }
        : new[] { 1f, 1f, 1f, 1f }).SelectMany(BitConverter.GetBytes).ToArray();
    private byte[] Bytes(ulong address, int length)
    {
        if (length <= 0 || address > ulong.MaxValue - (ulong)length)
            throw new InvalidOperationException("RS6 read bounds invalid; no writes.");
        var value = read(address, length);
        if (value.Length != length) throw new InvalidOperationException("Short RS6 ownership read; no writes.");
        return value;
    }
    private ulong Pointer(ulong address) => BitConverter.ToUInt64(Bytes(address, 8));
    private string ResourcePath(ulong address)
    {
        if (!Heap(address)) throw new InvalidOperationException("RS6 model path pointer invalid.");
        var bytes = Bytes(address, 512);
        var end = Array.IndexOf(bytes, (byte)0);
        if (end is <= 0 or >= 512) throw new InvalidOperationException("RS6 model path bounds invalid.");
        return Normalize(Encoding.ASCII.GetString(bytes, 0, end));
    }
    private string MaterialName(byte[] record)
    {
        var length = BitConverter.ToUInt64(record, 0x18);
        var capacity = BitConverter.ToUInt64(record, 0x20);
        if (length is < 1 or > 127 || capacity < length || capacity > 255)
            throw new InvalidOperationException("RS6 material name bounds invalid.");
        if (capacity < 16) return Encoding.ASCII.GetString(record, 8, (int)length);
        var address = BitConverter.ToUInt64(record, 8);
        if (!Heap(address)) throw new InvalidOperationException("RS6 material name pointer invalid.");
        return Encoding.ASCII.GetString(Bytes(address, (int)length));
    }

    internal Rs6MirrorRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, bool brighter = false, uint output = 22, uint nativeTrigger = 22, bool correctRightMask = false)
    {
        if (!ValidOutput(output)) throw new ArgumentOutOfRangeException(nameof(output), "RS6 mirror output must be 20, 21 or 22.");
        if (nativeTrigger is not (22 or 70)) throw new ArgumentOutOfRangeException(nameof(nativeTrigger));
        if (correctRightMask && (output != 20 || nativeTrigger != 70 || !brighter))
            throw new ArgumentException("Experimental mask candidate is bounded RIGHT70 at matching5.657 only.");
        this.nativeTrigger = nativeTrigger;
        this.read = read; this.write = write; this.sameCar = sameCar; this.writable = writable; this.module = module;
        this.output = output;
        LeftIntensity = brighter ? 5.657f : 1.414f;
        // User requires the same5.657 used by the earlier accepted BOTH probe.
        // Increasing right to32 did not fix its visible dimness on native22.
        RightIntensity = LeftIntensity;
        renderOwner = ChargerRenderOwner.Capture(read, sameCar, module, vehicle, component);
        var selected = models.Where(m => Normalize(m.Path).Equals(LeftPath, StringComparison.OrdinalIgnoreCase) ||
            Normalize(m.Path).Equals(RightPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!sameCar() || selected.Length != 2 ||
            selected.Select(m => Normalize(m.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            selected.Select(m => m.Wrapper).Distinct().Count() != 2 || selected.Select(m => m.Instance).Distinct().Count() != 2)
            throw new InvalidOperationException("Unique current RS6 LEFT/RIGHT _b mirror models required; no writes.");

        service = Pointer(module + 0xA82C398);
        if (!Heap(service)) throw new InvalidOperationException("RS6 upload service invalid.");
        backend = Pointer(service + 0x18);
        if (!Heap(backend)) throw new InvalidOperationException("RS6 upload backend invalid.");
        backendVtable = Pointer(backend); pages = Pointer(backend + 0x78); pagesEnd = Pointer(backend + 0x80);
        pageSize = Pointer(backend + 0x50); gpuBase = Pointer(backend + 0xA0);
        if (!Heap(pages) || pagesEnd <= pages || pagesEnd - pages > 0x10000 || (pagesEnd - pages) % 16 != 0 ||
            pageSize != 0x2000000 || backendVtable < module || backendVtable > module + 0xB3C0000 - 0x40)
            throw new InvalidOperationException("RS6 upload page-table/vtable bounds invalid.");
        ValidateBackend();

        uint? intensityId = null;
        // Derive the launch-local intensity property ID from LEFT's unique .177
        // float, then identify RIGHT's float1 by that ID (many floats equal1).
        foreach (var model in selected.OrderByDescending(m => Normalize(m.Path).Equals(LeftPath, StringComparison.OrdinalIgnoreCase)))
        {
            var left = Normalize(model.Path).Equals(LeftPath, StringComparison.OrdinalIgnoreCase);
            var expectedPath = left ? LeftPath : RightPath;
            if (!sameCar() || !Heap(model.Wrapper) || !Heap(model.Instance) || !Heap(model.Resource) ||
                Pointer(model.Wrapper) != module + 0x65C6408 || Pointer(model.Wrapper + 0x60) != model.Instance ||
                Pointer(model.Wrapper + 0x68) != model.Instance - 0x10 || Pointer(model.Instance) != module + 0x6474E38 ||
                Pointer(model.Instance + 0x20) != model.Resource || Pointer(model.Resource) != module + 0x6474CA8 ||
                !renderOwner.MatchesInstance(model.Instance))
                throw new InvalidOperationException("RS6 mirror does not belong to the current player Scene; no writes.");
            var pathAddress = Pointer(model.Resource + 0x80);
            if (!ResourcePath(pathAddress).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RS6 exact mirror resource path mismatch.");
            var definition = Pointer(model.Instance + 0x28);
            if (!Heap(definition)) throw new InvalidOperationException("RS6 mirror definition invalid.");
            var table = Pointer(definition + 0x140); var count = left ? 9UL : 8UL; var id = left ? 8u : 7u;
            if (!Heap(table) || Pointer(definition + 0x148) != count)
                throw new InvalidOperationException("RS6 exact mirror named-material table mismatch.");
            var entries = Bytes(table, checked((int)count * 0x38));
            var matches = Enumerable.Range(0, (int)count).Where(i => BitConverter.ToUInt32(entries, i * 0x38) == id).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("RS6 emitter material ID ambiguous.");
            var offset = matches[0] * 0x38; var record = table + (ulong)offset;
            var recordHeader = entries.AsSpan(offset, 0x38).ToArray(); var name = MaterialName(recordHeader);
            if (name != "lights_smooth_CH1") throw new InvalidOperationException("RS6 exact emitter material name mismatch.");
            var material = BitConverter.ToUInt64(recordHeader, 0x28);
            if (!Heap(material) || Pointer(material) != module + 0x65EB720 || Pointer(material - 0x10) != module + 0x6475280 ||
                BitConverter.ToUInt64(recordHeader, 0x30) != material - 0x10)
                throw new InvalidOperationException("RS6 emitter material/control identity mismatch.");
            var begin = Pointer(material + 8); var end = Pointer(material + 0x10);
            if (!Heap(begin) || end <= begin || end - begin > 1024 || (end - begin) % 8 != 0)
                throw new InvalidOperationException("RS6 source parameter-vector bounds invalid.");
            var members = Bytes(begin, (int)(end - begin)); var parameters = new List<Parameter>();
            for (var p = 0; p < members.Length; p += 8)
            {
                var address = BitConverter.ToUInt64(members, p);
                if (!Heap(address)) throw new InvalidOperationException("RS6 source parameter pointer invalid.");
                var header = Bytes(address, 0x18);
                if (BitConverter.ToUInt64(header) == module + 0x6578438) header = Bytes(address, 0x20);
                parameters.Add(new(address, header));
            }
            if (parameters.Select(p => p.Address).Distinct().Count() != parameters.Count)
                throw new InvalidOperationException("RS6 source parameter membership duplicated.");
            var triggers = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x65786F8 &&
                BitConverter.ToUInt32(p.Header, 0x10) == 70).ToArray();
            var colors = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x6578438).ToArray();
            var intensities = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x6578478 &&
                (left ? BitConverter.ToSingle(p.Header, 0x10) == .177f : intensityId.HasValue && PropertyId(p.Header) == intensityId.Value)).ToArray();
            if (triggers.Length != 1 || colors.Length != 1 || intensities.Length != 1 ||
                !colors[0].Header.AsSpan(0x10, 16).SequenceEqual(AuthoredTint(left)) ||
                BitConverter.ToSingle(intensities[0].Header, 0x10) != (left ? .177f : 1f))
                throw new InvalidOperationException("RS6 authored trigger/tint/intensity source set ambiguous or changed.");
            if (left) intensityId = PropertyId(intensities[0].Header);
            var trigger = triggers[0]; var color = colors[0]; var intensity = intensities[0];
            var compiled = Pointer(material + 0x58);
            if (!Heap(compiled)) throw new InvalidOperationException("RS6 compiled emitter object invalid.");
            var cpu = Pointer(compiled + 0x10);
            if (!Heap(cpu) || Pointer(compiled + 0x18) != cpu + BufferSize)
                throw new InvalidOperationException("RS6 single-channel emitter buffer layout changed.");
            var original = Bytes(cpu, BufferSize);
            if (!ValidEmissionLayout(original, left) || !original.AsSpan(0x40, 16).SequenceEqual(color.Header.AsSpan(0x10, 16)) ||
                !original.AsSpan(0xB0, 4).SequenceEqual(intensity.Header.AsSpan(0x10, 4)))
                throw new InvalidOperationException("RS6 emitter shader/source semantic signature mismatch.");
            var binding = Pointer(compiled + 0x70);
            if (!Heap(binding)) throw new InvalidOperationException("RS6 emitter binding invalid.");
            var buffer = Pointer(binding); var descriptor = Bytes(binding + 0x10, 0x20);
            var gpu = BitConverter.ToUInt64(descriptor, 8);
            if (!Heap(buffer) || Pointer(buffer) != module + 0x65E6788 || Pointer(binding + 8) != buffer - 0x10 ||
                gpu < gpuBase || BitConverter.ToUInt32(descriptor, 0x18) != BufferSize || (descriptor[0x10] & 7) == 0 ||
                (gpu - gpuBase) / pageSize >= (pagesEnd - pages) / 16 || (gpu - gpuBase) % pageSize > pageSize - BufferSize)
                throw new InvalidOperationException("RS6 mapped emitter descriptor bounds mismatch.");
            var relative = gpu - gpuBase; var page = pages + relative / pageSize * 16; var pageHeader = Bytes(page, 16);
            var mappedBase = BitConverter.ToUInt64(pageHeader, 8); var pageOffset = relative % pageSize;
            if (!Heap(mappedBase) || mappedBase > ulong.MaxValue - pageOffset - BufferSize)
                throw new InvalidOperationException("RS6 mapped page address overflow.");
            var mapped = mappedBase + pageOffset;
            if (!Heap(mapped) || !writable(mapped, BufferSize) || !Bytes(mapped, BufferSize).SequenceEqual(original))
                throw new InvalidOperationException("RS6 full writable mapped emitter must equal its CPU source.");
            targets.Add(new(model.Wrapper, model.Instance, model.Resource, pathAddress, expectedPath,
                definition, table, count, record, recordHeader, id, name, material, begin, end, members, parameters.ToArray(),
                trigger.Address, trigger.Header, color.Address, color.Header, intensity.Address, intensity.Header,
                compiled, cpu, binding, buffer, descriptor, page, pageHeader, mapped, original, Bytes(material + 0x80, 1)[0], left));
        }
        if (targets.Count != 2 || targets.Count(t => t.Left) != 1 || targets.Select(t => t.Material).Distinct().Count() != 2 ||
            targets.Select(t => t.Cpu).Distinct().Count() != 2 || targets.Select(t => t.Binding).Distinct().Count() != 2 ||
            targets.Select(t => t.Buffer).Distinct().Count() != 2 ||
            targets.Select(t => PropertyId(t.TriggerHeader)).Distinct().Count() != 1 ||
            targets.Select(t => PropertyId(t.ColorHeader)).Distinct().Count() != 1 ||
            targets.Select(t => PropertyId(t.IntensityHeader)).Distinct().Count() != 1 ||
            targets.Any(t => targets.Any(other => t != other && Overlaps(t.Mapped, other.Mapped))) ||
            targets.Any(t => targets.Any(other => Overlaps(t.Mapped, other.Cpu))))
            throw new InvalidOperationException("RS6 mirrored emitter set shared, overlapping or ambiguous; no writes.");
        if (correctRightMask)
        {
            var leftTarget = targets.Single(t => t.Left); var rightTarget = targets.Single(t => !t.Left);
            ulong TextureParameter(Target t) => t.Parameters.Single(p => BitConverter.ToUInt64(p.Header) == module + 0x6578638).Address;
            maskCandidate = new Rs6MirrorMaskCandidate(read, write, writable, module,
                TextureParameter(leftTarget), leftTarget.Compiled, TextureParameter(rightTarget), rightTarget.Compiled);
        }
        foreach (var t in targets)
        {
            if (!ShouldRoute(t.Left, output))
            {
                if (nativeTrigger == 70) AddEdit(t, 0xC0, BitConverter.GetBytes(22u));
                continue;
            }
            AddEdit(t, 0xC0, BitConverter.GetBytes(nativeTrigger));
            AddEdit(t, 0x40, 1f); AddEdit(t, 0x44, .35f); AddEdit(t, 0x48, .015f);
            AddEdit(t, 0xB0, t.Left ? LeftIntensity : RightIntensity);
        }
        Validate();
        Console.WriteLine($"Verified current-player RS6 _b mirror emitters and exact separate source/renderer buffers; {(output == 22 ? "BOTH hazard" : output == 21 ? "LEFT" : "RIGHT")} trigger{nativeTrigger}/RGB amber prepared without writes; selected intensities: {string.Join(",", targets.Where(t => ShouldRoute(t.Left, output)).Select(t => $"{(t.Left ? "LEFT" : "RIGHT")}={(t.Left ? LeftIntensity : RightIntensity):R}"))}. Opposite tint/intensity and glass unchanged.");
    }

    private void AddEdit(Target target, int offset, float value) => AddEdit(target, offset, BitConverter.GetBytes(value));
    private void AddEdit(Target target, int offset, byte[] applied)
    {
        if (!CanOwn(target, offset) || applied.Length != 4 ||
            (offset & 3) != 0 || offset > target.Original.Length - 4)
            throw new InvalidOperationException("Only selected RS6 RGB/intensity DWORDs and their trigger may be owned.");
        var original = target.Original.AsSpan(offset, 4).ToArray();
        if (!original.SequenceEqual(applied)) edits.Add(new(target, offset, original, applied));
    }
    private bool CanOwn(Target target, int offset) => OwnedOffset(offset, output) &&
        (ShouldRoute(target.Left, output) || nativeTrigger == 70 && offset == 0xC0);
    private void ValidateBackend()
    {
        if (Pointer(module + 0xA82C398) != service || Pointer(service + 0x18) != backend || Pointer(backend) != backendVtable ||
            Pointer(backend + 0x78) != pages || Pointer(backend + 0x80) != pagesEnd || Pointer(backend + 0x50) != pageSize ||
            Pointer(backend + 0xA0) != gpuBase || Pointer(backendVtable + 0x38) != module + 0x14FEC00 ||
            !Bytes(module + 0x14FEC00, UploadSignature.Length).SequenceEqual(UploadSignature) ||
            !Bytes(module + 0x14FF170, WalkSignature.Length).SequenceEqual(WalkSignature))
            throw new InvalidOperationException("RS6 upload backend identity/signature changed; no stale writes.");
    }
    internal void Validate(bool restoring = false)
    {
        renderOwner.Validate();
        if (!sameCar()) throw new InvalidOperationException("Exact RS6 player/build identity changed; no writes.");
        if (attempted.Any(e => !CanOwn(e.Target, e.Offset)))
            throw new InvalidOperationException("RS6 attempted fields exceed selected-side ownership; no writes.");
        ValidateBackend();
        foreach (var t in targets)
        {
            if (Pointer(t.Wrapper) != module + 0x65C6408 || Pointer(t.Wrapper + 0x60) != t.Instance ||
                Pointer(t.Wrapper + 0x68) != t.Instance - 0x10 || Pointer(t.Instance) != module + 0x6474E38 ||
                Pointer(t.Instance + 0x20) != t.Resource || Pointer(t.Resource) != module + 0x6474CA8 ||
                !renderOwner.MatchesInstance(t.Instance) || Pointer(t.Resource + 0x80) != t.PathAddress ||
                !ResourcePath(t.PathAddress).Equals(t.Path, StringComparison.OrdinalIgnoreCase) ||
                Pointer(t.Instance + 0x28) != t.Definition || Pointer(t.Definition + 0x140) != t.Table ||
                Pointer(t.Definition + 0x148) != t.Count || !Bytes(t.Record, 0x38).SequenceEqual(t.RecordHeader) ||
                MaterialName(t.RecordHeader) != t.Name || Pointer(t.Material) != module + 0x65EB720 ||
                Pointer(t.Material - 0x10) != module + 0x6475280 || Pointer(t.Material + 8) != t.Begin ||
                Pointer(t.Material + 0x10) != t.End || !Bytes(t.Begin, t.Members.Length).SequenceEqual(t.Members) ||
                t.Parameters.Any(p => !Bytes(p.Address, p.Header.Length).SequenceEqual(p.Header)) ||
                Bytes(t.Material + 0x80, 1)[0] != t.Flags || Pointer(t.Material + 0x58) != t.Compiled ||
                Pointer(t.Compiled + 0x10) != t.Cpu || Pointer(t.Compiled + 0x18) != t.Cpu + BufferSize ||
                !Bytes(t.Cpu, BufferSize).SequenceEqual(t.Original) || Pointer(t.Compiled + 0x70) != t.Binding ||
                Pointer(t.Binding) != t.Buffer || Pointer(t.Binding + 8) != t.Buffer - 0x10 ||
                Pointer(t.Buffer) != module + 0x65E6788 || !Bytes(t.Binding + 0x10, 0x20).SequenceEqual(t.Descriptor) ||
                !Bytes(t.Page, 16).SequenceEqual(t.PageHeader) || !writable(t.Mapped, BufferSize))
                throw new InvalidOperationException("RS6 rooted mirror/source/renderer ownership changed; no stale restoration.");
            if (!ChargerDrlRoute.IsOwnedBuffer(Bytes(t.Mapped, BufferSize), t.Original,
                attempted.Where(e => e.Target == t).Select(e => (e.Offset, e.Applied)).ToArray(), restoring))
                throw new InvalidOperationException("RS6 mapped bytes changed outside attempted owned DWORDs; restoration stopped.");
        }
        maskCandidate?.Validate(restoring);
        foreach (var storage in ExtraStorage)
            foreach (var buffer in Buffers)
                if (Rs6MirrorMaskCandidate.Overlaps(storage.Address, storage.Size, buffer.Mapped, BufferSize) ||
                    Rs6MirrorMaskCandidate.Overlaps(storage.Address, storage.Size, buffer.Cpu, BufferSize))
                    throw new InvalidOperationException("RS6 mask cache aliases a mirror shader buffer.");
        renderOwner.Validate();
    }
    internal void Apply()
    {
        Validate(); maskCandidate?.Apply(); Validate();
        foreach (var edit in edits)
        {
            Validate(); attempted.Add(edit); // Own the attempt even if the write throws before/after mutation.
            write(edit.Target.Mapped + (ulong)edit.Offset, edit.Applied); Validate();
        }
        Console.WriteLine("Selected RS6 _b mirror mapped RGB/intensity and guarded triggers applied; unselected tint/intensity, alpha, glass, CPU/source and flags untouched.");
    }
    internal void Restore()
    {
        Validate(restoring: true);
        foreach (var edit in edits.Where(attempted.Contains).Reverse().ToArray())
        {
            Validate(restoring: true); write(edit.Target.Mapped + (ulong)edit.Offset, edit.Original);
            attempted.Remove(edit); Validate(restoring: true);
        }
        maskCandidate?.Restore();
        Validate();
        Console.WriteLine("RESTORED AND VERIFIED: both complete original RS6 _b mirror renderer buffers exactly match captured bytes.");
    }
    private static bool ValidOutput(uint output) => output is 20 or 21 or 22;
    internal static bool ShouldRoute(bool left, uint output) => output switch
    {
        21 => left, 20 => !left, 22 => true,
        _ => throw new ArgumentOutOfRangeException(nameof(output), "RS6 mirror output must be 20, 21 or 22.")
    };
    private static bool OwnedOffset(int offset, uint output) => ValidOutput(output) &&
        offset is 0x40 or 0x44 or 0x48 or 0xB0 or 0xC0;
    // Reject overflow too; renderer destinations must never alias either CPU
    // source, even when their captured bytes happen to be identical.
    private static bool Overlaps(ulong first, ulong second) =>
        first > ulong.MaxValue - BufferSize || second > ulong.MaxValue - BufferSize ||
        first < second + BufferSize && second < first + BufferSize;
    private static bool ValidEmissionLayout(byte[] values, bool left) => values.Length == BufferSize &&
        values.AsSpan(0x40, 16).SequenceEqual(AuthoredTint(left)) &&
        BitConverter.ToSingle(values, 0xB0) == (left ? .177f : 1f) && BitConverter.ToUInt32(values, 0xC0) == 70 &&
        BitConverter.ToUInt32(values, 0xC4) == 0 && BitConverter.ToUInt32(values, 0xC8) == 0 &&
        (values.AsSpan(0xF0, 16).SequenceEqual(Tail) || values.AsSpan(0xF0, 16).SequenceEqual(RestartTail) ||
            values.AsSpan(0xF0, 16).SequenceEqual(UpdatedBuildTail));
    internal static void SelfTest()
    {
        var count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException($"RS6 mirror guard check {count + 1} failed."); count++; }
        byte[] Original(bool left)
        {
            var bytes = new byte[BufferSize]; AuthoredTint(left).CopyTo(bytes, 0x40);
            BitConverter.GetBytes(left ? .177f : 1f).CopyTo(bytes, 0xB0); BitConverter.GetBytes(70u).CopyTo(bytes, 0xC0);
            Tail.CopyTo(bytes, 0xF0); return bytes;
        }
        var fields = new[] { (Offset: 0x40, Applied: BitConverter.GetBytes(1f)), (Offset: 0x44, Applied: BitConverter.GetBytes(.35f)),
            (Offset: 0x48, Applied: BitConverter.GetBytes(.015f)), (Offset: 0xB0, Applied: BitConverter.GetBytes(1.414f)) };
        Check(WalkSignature.Length == 0x33 && UploadSignature.Length == 0x11);
        Check(Overlaps(0x1000, 0x1000));
        Check(Overlaps(0x1000, 0x10FF));
        Check(Overlaps(0x10FF, 0x1000));
        Check(!Overlaps(0x1000, 0x1100));
        Check(!Overlaps(0x1100, 0x1000));
        Check(Overlaps(ulong.MaxValue, 0x1000));
        Check(Overlaps(0x1000, ulong.MaxValue));
        foreach (var left in new[] { true, false })
        {
            var original = Original(left); Check(ValidEmissionLayout(original, left)); Check(!ValidEmissionLayout(original, !left));
            var restartOriginal = original.ToArray(); RestartTail.CopyTo(restartOriginal, 0xF0);
            Check(ValidEmissionLayout(restartOriginal, left));
            var unknownTail = restartOriginal.ToArray(); unknownTail[0xF0] ^= 1;
            Check(!ValidEmissionLayout(unknownTail, left));
            Check(!ValidEmissionLayout(original[..255], left));
            foreach (var offset in new[] { 0x40, 0x44, 0x48, 0x4C, 0xB0, 0xC0, 0xC4, 0xC8, 0xF0, 0xFF })
            { var foreign = original.ToArray(); foreign[offset] ^= 1; Check(!ValidEmissionLayout(foreign, left)); }
            var applied = original.ToArray(); foreach (var f in fields) f.Applied.CopyTo(applied, f.Offset);
            Check(ChargerDrlRoute.IsOwnedBuffer(applied, original, fields, false));
            Check(ChargerDrlRoute.IsOwnedBuffer(original, original, fields, true));
            // Every mix of fully original/applied DWORDs is a safe partial route
            // restoration. Torn/foreign DWORD values are deliberately rejected.
            for (var mask = 0; mask < 16; mask++)
            {
                var partial = original.ToArray();
                for (var f = 0; f < fields.Length; f++) if ((mask & (1 << f)) != 0) fields[f].Applied.CopyTo(partial, fields[f].Offset);
                Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, fields, true));
            }
            for (var offset = 0; offset < BufferSize; offset++)
            {
                var foreign = applied.ToArray(); foreign[offset] ^= 1;
                Check(!ChargerDrlRoute.IsOwnedBuffer(foreign, original, fields, true));
            }
            for (var done = fields.Length; done >= 0; done--)
            {
                var remaining = fields.Take(done).ToArray(); var partial = original.ToArray();
                foreach (var f in remaining) f.Applied.CopyTo(partial, f.Offset);
                Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, remaining, true));
                Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, remaining, false));
            }
            Check(!ChargerDrlRoute.IsOwnedBuffer(applied, original, fields[..3], true)); // Unattempted intensity is not owned.
            Check(applied.AsSpan(0x4C, 4).SequenceEqual(original.AsSpan(0x4C, 4)) &&
                applied.AsSpan(0xC0, 12).SequenceEqual(original.AsSpan(0xC0, 12)));
            var brightFields = fields.Select(f => f.Offset == 0xB0 ? (f.Offset, BitConverter.GetBytes(5.657f)) : f).ToArray();
            var bright = original.ToArray(); foreach (var f in brightFields) f.Item2.CopyTo(bright, f.Offset);
            Check(ChargerDrlRoute.IsOwnedBuffer(bright, original, brightFields, false));
            Check(!ValidEmissionLayout(bright, left)); // Applied intensity never becomes the source baseline.
            for (var mask = 0; mask < 16; mask++)
            {
                var partial = original.ToArray();
                for (var f = 0; f < brightFields.Length; f++) if ((mask & (1 << f)) != 0) brightFields[f].Item2.CopyTo(partial, brightFields[f].Offset);
                Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, brightFields, true));
            }
        }
        foreach (var modeOutput in new uint[] { 20, 21, 22 })
        {
            Check(ValidOutput(modeOutput));
            Check(ShouldRoute(true, modeOutput) == (modeOutput is 21 or 22));
            Check(ShouldRoute(false, modeOutput) == (modeOutput is 20 or 22));
            foreach (var offset in new[] { 0x40, 0x44, 0x48, 0xB0 }) Check(OwnedOffset(offset, modeOutput));
            Check(OwnedOffset(0xC0, modeOutput));
            foreach (var offset in new[] { -4, 0x41, 0x4C, 0xAC, 0xB4, 0xC4, 0xC8, 0x100 })
                Check(!OwnedOffset(offset, modeOutput));
            foreach (var left in new[] { true, false })
            {
                var original = Original(left); var selectedSide = ShouldRoute(left, modeOutput);
                var modeFields = selectedSide
                    ? fields.Append((Offset: 0xC0, Applied: BitConverter.GetBytes(22u))).ToArray()
                    : Array.Empty<(int Offset, byte[] Applied)>();
                Check(modeFields.Length == (selectedSide ? 5 : 0));
                Check(modeFields.All(f => OwnedOffset(f.Offset, modeOutput)));
                var modeApplied = original.ToArray(); foreach (var f in modeFields) f.Applied.CopyTo(modeApplied, f.Offset);
                Check(ChargerDrlRoute.IsOwnedBuffer(modeApplied, original, modeFields, false));
                Check(ChargerDrlRoute.IsOwnedBuffer(original, original, modeFields, true));
                Check(BitConverter.ToUInt32(modeApplied, 0xC0) == (selectedSide ? 22u : 70u));
                Check(modeApplied.AsSpan(0x4C, 4).SequenceEqual(original.AsSpan(0x4C, 4)) &&
                    modeApplied.AsSpan(0xC4, 8).SequenceEqual(original.AsSpan(0xC4, 8)));
                Check(selectedSide || modeApplied.SequenceEqual(original)); // Opposite side is entirely unowned.
                for (var mask = 0; mask < (1 << modeFields.Length); mask++)
                {
                    var partial = original.ToArray();
                    for (var f = 0; f < modeFields.Length; f++)
                        if ((mask & (1 << f)) != 0) modeFields[f].Applied.CopyTo(partial, modeFields[f].Offset);
                    Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, modeFields, true));
                }
                for (var offset = 0; offset < BufferSize; offset++)
                {
                    var foreign = modeApplied.ToArray(); foreign[offset] ^= 1;
                    Check(!ChargerDrlRoute.IsOwnedBuffer(foreign, original, modeFields, true));
                }
                for (var done = modeFields.Length; done >= 0; done--)
                {
                    var remaining = modeFields.Take(done).ToArray(); var partial = original.ToArray();
                    foreach (var f in remaining) f.Applied.CopyTo(partial, f.Offset);
                    Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, remaining, true));
                    Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, remaining, false));
                }
                foreach (var field in modeFields.Where(f => !original.AsSpan(f.Offset, 4).SequenceEqual(f.Applied)))
                    Check(!ChargerDrlRoute.IsOwnedBuffer(modeApplied, original,
                        modeFields.Where(f => f.Offset != field.Offset).ToArray(), true));
                if (!selectedSide)
                {
                    var foreignTrigger = modeApplied.ToArray(); BitConverter.GetBytes(modeOutput).CopyTo(foreignTrigger, 0xC0);
                    Check(!ChargerDrlRoute.IsOwnedBuffer(foreignTrigger, original, modeFields, true));
                }
            }
        }
        foreach (var invalid in new uint[] { 0, 19, 23, 70, uint.MaxValue })
        {
            Check(!ValidOutput(invalid) && !OwnedOffset(0xC0, invalid) && !OwnedOffset(0x40, invalid));
            var rejected = false;
            try { _ = ShouldRoute(true, invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected);
            var reads = 0; var writes = 0; rejected = false;
            try
            {
                _ = new Rs6MirrorRoute((address, length) => { reads++; return new byte[length]; },
                    (address, value) => { writes++; }, () => true, (address, length) => true, 0, 0, 0,
                    Array.Empty<(ulong Wrapper, ulong Instance, ulong Resource, string Path)>(), output: invalid);
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && reads == 0 && writes == 0); // Reject an invalid mode before any process read/write.
        }
        Console.WriteLine($"PASS: {count} pure RS6 mirror layout/owned-DWORD/partial-restoration checks; no game process opened or written.");
    }
}
