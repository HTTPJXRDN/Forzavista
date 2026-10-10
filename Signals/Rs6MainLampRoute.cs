using System.Text;

// Isolated Steam 6.461.691.0 RS6 main-lamp candidates, not menu admission or
// visual verification. Front ID12: selected DRL trigger + amber RGB only.
// Rear ID5: selected trigger only, retaining authored red/intensity16 until
// its physical region is identified. Both sides retain full ownership guards.
// Caller supplies exact-build/car identity; all instances require rooted Pose.
internal enum Rs6LampCandidate { FrontDrl, RearRegion5, IsolateStock70 }

internal sealed class Rs6MainLampRoute
{
    private sealed record Parameter(ulong Address, byte[] Header);
    private sealed record Target(ulong Wrapper, ulong Instance, ulong Resource, ulong PathAddress, string Path,
        ulong Definition, ulong Table, ulong Count, byte[] TableBytes, ulong Record, byte[] RecordHeader, uint Id, string Name,
        ulong Material, ulong Begin, ulong End, byte[] Members, Parameter[] Parameters,
        ulong Trigger, byte[] TriggerHeader, ulong Color, byte[] ColorHeader, ulong Intensity, byte[] IntensityHeader,
        ulong Compiled, ulong Cpu, ulong Binding, ulong Buffer, byte[] Descriptor, ulong Page, byte[] PageHeader,
        ulong Mapped, byte[] Original, byte Flags, bool Left);
    private sealed record Edit(Target Target, int Offset, byte[] Original, byte[] Applied);
    private const int BufferSize = 0x100;
    private readonly string LeftPath, RightPath;
    private readonly bool rear, isolation;
    private readonly uint nativeTrigger;
    private static readonly byte[] Bumper70Baseline = Convert.FromHexString("000000000000000000000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000000000000000C6B6543CC6B6543CC6B6543C0000803F0000803F0000803F0000803F0000803F000020409A99993E00000040000000000000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F000000000000803F0000803F0000803F00000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000803E000000000000000046000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000005010000060100000000000000000000");
    private static readonly byte[] Trunk70Baseline = Convert.FromHexString("000000000000000000000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000000000000000C6B6543CC6B6543CC6B6543C0000803F0000000000000000000000000000803F0000A0416666663F00004041000000000000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F000000000000803F0000803F0000803F00000000000000000000803F0000803F0000803F0000803F0000803F0000003D0000803E000000000000000046000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000005010000060100000000000000000000");
    // Complete ID12/ID5 buffers from RS6-main-lamp-buffers-restart.jsonl.
    // Only exact observed main-lamp signatures are accepted, including tails.
    // A new launch signature requires fresh read-only evidence.
    private static readonly byte[] FrontBaseline = Convert.FromHexString("000000000000000000000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000000000000000C6B6543CC6B6543CC6B6543C0000803FB137373F81924A3FAEF5763F0000803F000020409A99993E00000040000000000000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F000000000000803F0000803F0000803F00000000000000000000803F0000803F0000803F23DB793F0000803F2506B5400000803E00000000000000000F000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000005010000060100000000000000000000");
    private static readonly byte[] RearLeftBaseline = Convert.FromHexString("000000000000000000000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000000000000000C6B6543CC6B6543CC6B6543C0000803F50DC133F61EBC63A61EBC63A0000803F000020409A99993E00000040000000000000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F000000000000803F0000803F0000803F00000000000000000000803F0000803F0000803FD9CEF73D0000803F000080410000803E000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000005010000060100000000000000000000");
    private static readonly byte[] RearRightBaseline = Convert.FromHexString("000000000000000000000000000000000000803F0000803F0000803F0000803F0000803F0000803F0000000000000000C6B6543CC6B6543CC6B6543C0000803F51DC133F61EBC63A61EBC63A0000803F000020409A99993E00000040000000000000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F0000803F000000000000803F0000803F0000803F00000000000000000000803F0000803F0000803FD9CEF73D0000803F000080410000803E000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000005010000060100000000000000000000");
    // Steam 6.461.691.0 read-only rs6-lamps-readonly.log: all other
    // template bytes are identical. Captured CPU/mapped buffers stay exact.
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
    private readonly List<Target> targets = [];
    private readonly List<Edit> edits = [];
    private readonly HashSet<Edit> attempted = [];
    internal IEnumerable<(ulong Mapped, ulong Cpu)> Buffers => targets.Select(t => (t.Mapped, t.Cpu));
    internal string Description => isolation ? "bumper/trunk shared70 isolation" : rear ? "rear ID5 red region-identification candidate" : "front ID12 DRL amber candidate";

    private static bool Heap(ulong address) => address is >= 0x10000000 and < 0x7FF000000000 && (address & 7) == 0;
    private static string Normalize(string path) => path.Replace('/', '\\');
    private static uint PropertyId(byte[] header) => BitConverter.ToUInt32(header, 9) & 0xFFFFFF;
    internal static byte[] Baseline(bool rear, bool left) =>
        (rear ? left ? RearLeftBaseline : RearRightBaseline : FrontBaseline).ToArray();
    internal static byte[] IsolationBaseline(bool bumper) => (bumper ? Bumper70Baseline : Trunk70Baseline).ToArray();
    private byte[] ExpectedBaseline(bool left) => isolation ? IsolationBaseline(left) : Baseline(rear, left);
    private static bool ValidObservedBaseline(byte[] values, byte[] expected) =>
        values.Length == BufferSize && expected.Length == BufferSize &&
        (values.SequenceEqual(expected) || values.AsSpan(0, 0xF0).SequenceEqual(expected.AsSpan(0, 0xF0)) &&
            values.AsSpan(0xF0, 16).SequenceEqual(UpdatedBuildTail));
    private byte[] AuthoredTint(bool left) => ExpectedBaseline(left).AsSpan(0x40, 16).ToArray();
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

    internal Rs6MainLampRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, Rs6LampCandidate candidate, uint output, uint nativeTrigger = 22)
    {
        if (!ValidOutput(output)) throw new ArgumentOutOfRangeException(nameof(output), "RS6 main-lamp output must be 20, 21 or 22.");
        if (candidate is not (Rs6LampCandidate.FrontDrl or Rs6LampCandidate.RearRegion5 or Rs6LampCandidate.IsolateStock70))
            throw new ArgumentOutOfRangeException(nameof(candidate));
        if (nativeTrigger is not (22 or 70) || candidate == Rs6LampCandidate.IsolateStock70 && (output != 22 || nativeTrigger != 22))
            throw new ArgumentOutOfRangeException(nameof(nativeTrigger));
        this.nativeTrigger = nativeTrigger;
        isolation = candidate == Rs6LampCandidate.IsolateStock70;
        rear = candidate != Rs6LampCandidate.FrontDrl;
        var folder = rear ? "secondarylights" : "primarylights";
        var part = rear ? "taillight" : "headlight";
        LeftPath = $@"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\{folder}\{part}L_a.modelbin";
        RightPath = $@"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\{folder}\{part}R_a.modelbin";
        if (isolation)
        {
            LeftPath = @"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\BumperR\bumperrsidemarkers_b.modelbin";
            RightPath = @"Game:\Media\cars\AUD_RS6AVANT_21\Scene\Exterior\Trunk\trunkLiner_a__SLOD.modelbin";
        }
        this.read = read; this.write = write; this.sameCar = sameCar; this.writable = writable; this.module = module;
        this.output = output;
        renderOwner = ChargerRenderOwner.Capture(read, sameCar, module, vehicle, component);
        var selected = models.Where(m => Normalize(m.Path).Equals(LeftPath, StringComparison.OrdinalIgnoreCase) ||
            Normalize(m.Path).Equals(RightPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!sameCar() || selected.Length != 2 ||
            selected.Select(m => Normalize(m.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            selected.Select(m => m.Wrapper).Distinct().Count() != 2 || selected.Select(m => m.Instance).Distinct().Count() != 2)
            throw new InvalidOperationException("Unique current RS6 LEFT/RIGHT main-lamp models required; no writes.");

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

        // Derive launch-local IDs from typed exact-value source records;
        // front intensity5.657 and rear16 each have one unique float candidate.
        uint? isolationIntensityId = null;
        foreach (var model in selected.OrderByDescending(m => Normalize(m.Path).Equals(isolation ? RightPath : LeftPath, StringComparison.OrdinalIgnoreCase)))
        {
            var left = Normalize(model.Path).Equals(LeftPath, StringComparison.OrdinalIgnoreCase);
            var expectedPath = left ? LeftPath : RightPath;
            if (!sameCar() || !Heap(model.Wrapper) || !Heap(model.Instance) || !Heap(model.Resource) ||
                Pointer(model.Wrapper) != module + 0x65C6408 || Pointer(model.Wrapper + 0x60) != model.Instance ||
                Pointer(model.Wrapper + 0x68) != model.Instance - 0x10 || Pointer(model.Instance) != module + 0x6474E38 ||
                Pointer(model.Instance + 0x20) != model.Resource || Pointer(model.Resource) != module + 0x6474CA8 ||
                !renderOwner.MatchesInstance(model.Instance))
                throw new InvalidOperationException("RS6 main-lamp does not belong to the current player Scene; no writes.");
            var pathAddress = Pointer(model.Resource + 0x80);
            if (!ResourcePath(pathAddress).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RS6 exact main-lamp resource path mismatch.");
            var definition = Pointer(model.Instance + 0x28);
            if (!Heap(definition)) throw new InvalidOperationException("RS6 main-lamp definition invalid.");
            var table = Pointer(definition + 0x140); var count = isolation ? left ? 1UL : 6UL : rear ? 7UL : 19UL;
            var id = isolation ? left ? 0u : 1u : rear ? 5u : 12u;
            if (!Heap(table) || Pointer(definition + 0x148) != count)
                throw new InvalidOperationException("RS6 exact main-lamp named-material table mismatch.");
            var entries = Bytes(table, checked((int)count * 0x38));
            var matches = Enumerable.Range(0, (int)count).Where(i => BitConverter.ToUInt32(entries, i * 0x38) == id).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("RS6 emitter material ID ambiguous.");
            var offset = matches[0] * 0x38; var record = table + (ulong)offset;
            var recordHeader = entries.AsSpan(offset, 0x38).ToArray(); var name = MaterialName(recordHeader);
            if (name != (isolation ? left ? "chrome" : "plastic_smooth_001" : rear ? "chrome" : "lights_smooth_CH1")) throw new InvalidOperationException("RS6 exact emitter material name mismatch.");
            var material = BitConverter.ToUInt64(recordHeader, 0x28);
            if (!Heap(material) || Pointer(material) != module + 0x65EB720 || Pointer(material - 0x10) != module + 0x6475280 ||
                BitConverter.ToUInt64(recordHeader, 0x30) != material - 0x10)
                throw new InvalidOperationException("RS6 emitter material/control identity mismatch.");
            var begin = Pointer(material + 8); var end = Pointer(material + 0x10);
            // The old report omitted three non-scalar source members. Full
            // read-only capture verifies9 front/11 rear: scalar/color/trigger
            // records plus one vector and two texture records, all guarded.
            if (!Heap(begin) || end <= begin || end - begin != (isolation ? left ? 9UL : 8UL : rear ? 11UL : 9UL) * 8)
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
            var types = parameters.GroupBy(p => BitConverter.ToUInt64(p.Header)).ToDictionary(g => g.Key, g => g.Count());
            bool HasType(ulong rva, int count) => types.TryGetValue(module + rva, out var found) && found == count;
            if (types.Count != (isolation && !left ? 4 : 5) || !HasType(0x65786F8, 1) || !HasType(0x6578438, 1) ||
                !HasType(0x6578478, isolation ? 5 : rear ? 6 : 4) ||
                (isolation && !left ? types.ContainsKey(module + 0x65785B8) : !HasType(0x65785B8, 1)) ||
                !HasType(0x6578638, isolation ? 1 : 2))
                throw new InvalidOperationException("RS6 complete typed source membership differs from captured evidence.");
            var triggers = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x65786F8 &&
                BitConverter.ToUInt32(p.Header, 0x10) == (isolation ? 70u : rear ? 0u : 15u)).ToArray();
            var colors = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x6578438).ToArray();
            var intensities = parameters.Where(p => BitConverter.ToUInt64(p.Header) == module + 0x6578478 &&
                (isolation && left ? isolationIntensityId.HasValue && PropertyId(p.Header) == isolationIntensityId.Value
                    : BitConverter.ToSingle(p.Header, 0x10) == (isolation ? .03125f : rear ? 16f : 5.657f))).ToArray();
            if (triggers.Length != 1 || colors.Length != 1 || intensities.Length != 1 ||
                !colors[0].Header.AsSpan(0x10, 16).SequenceEqual(AuthoredTint(left)) ||
                BitConverter.ToSingle(intensities[0].Header, 0x10) != (isolation ? left ? 1f : .03125f : rear ? 16f : 5.657f))
                throw new InvalidOperationException("RS6 authored trigger/tint/intensity source set ambiguous or changed.");
            if (isolation && !left) isolationIntensityId = PropertyId(intensities[0].Header);
            var trigger = triggers[0]; var color = colors[0]; var intensity = intensities[0];
            var compiled = Pointer(material + 0x58);
            if (!Heap(compiled)) throw new InvalidOperationException("RS6 compiled emitter object invalid.");
            var cpu = Pointer(compiled + 0x10);
            if (!Heap(cpu) || Pointer(compiled + 0x18) != cpu + BufferSize)
                throw new InvalidOperationException("RS6 single-channel emitter buffer layout changed.");
            var original = Bytes(cpu, BufferSize);
            if (!ValidObservedBaseline(original, ExpectedBaseline(left)) || !original.AsSpan(0x40, 16).SequenceEqual(color.Header.AsSpan(0x10, 16)) ||
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
                definition, table, count, entries, record, recordHeader, id, name, material, begin, end, members, parameters.ToArray(),
                trigger.Address, trigger.Header, color.Address, color.Header, intensity.Address, intensity.Header,
                compiled, cpu, binding, buffer, descriptor, page, pageHeader, mapped, original, Bytes(material + 0x80, 1)[0], left));
        }
        if (targets.Count != 2 || targets.Count(t => t.Left) != 1 || targets.Select(t => t.Material).Distinct().Count() != 2 ||
            targets.Select(t => t.Cpu).Distinct().Count() != 2 || targets.Select(t => t.Binding).Distinct().Count() != 2 ||
            targets.Select(t => t.Buffer).Distinct().Count() != 2 || targets.Select(t => t.Compiled).Distinct().Count() != 2 ||
            targets.Select(t => PropertyId(t.TriggerHeader)).Distinct().Count() != 1 ||
            targets.Select(t => PropertyId(t.ColorHeader)).Distinct().Count() != 1 ||
            targets.Select(t => PropertyId(t.IntensityHeader)).Distinct().Count() != 1 ||
            targets.Any(t => targets.Any(other => t != other && Overlaps(t.Mapped, other.Mapped))) ||
            targets.Any(t => targets.Any(other => Overlaps(t.Mapped, other.Cpu))))
            throw new InvalidOperationException("RS6 main-lamp emitter set shared, overlapping or ambiguous; no writes.");
        foreach (var t in targets.Where(t => ShouldRoute(t.Left, output)))
        {
            AddEdit(t, 0xC0, BitConverter.GetBytes(nativeTrigger));
            if (!rear) { AddEdit(t, 0x40, 1f); AddEdit(t, 0x44, .35f); AddEdit(t, 0x48, .015f); }
        }
        Validate();
        Console.WriteLine($"Verified current-player RS6 {Description}; {(output == 22 ? "BOTH targets" : output == 21 ? "LEFT" : "RIGHT")} on trigger{nativeTrigger} prepared without writes. Intensity, alpha and glass unchanged.");
    }

    private void AddEdit(Target target, int offset, float value) => AddEdit(target, offset, BitConverter.GetBytes(value));
    private void AddEdit(Target target, int offset, byte[] applied)
    {
        if (!ShouldRoute(target.Left, output) || !OwnedOffset(offset, output, rear) || applied.Length != 4 ||
            (offset & 3) != 0 || offset > target.Original.Length - 4)
            throw new InvalidOperationException("Only selected RS6 trigger and front-candidate RGB DWORDs may be owned.");
        var original = target.Original.AsSpan(offset, 4).ToArray();
        if (!original.SequenceEqual(applied)) edits.Add(new(target, offset, original, applied));
    }
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
        if (attempted.Any(e => !ShouldRoute(e.Target.Left, output) || !OwnedOffset(e.Offset, output, rear)))
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
                Pointer(t.Definition + 0x148) != t.Count || !Bytes(t.Table, t.TableBytes.Length).SequenceEqual(t.TableBytes) ||
                !Bytes(t.Record, 0x38).SequenceEqual(t.RecordHeader) ||
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
                throw new InvalidOperationException("RS6 rooted main-lamp/source/renderer ownership changed; no stale restoration.");
            if (!ChargerDrlRoute.IsOwnedBuffer(Bytes(t.Mapped, BufferSize), t.Original,
                attempted.Where(e => e.Target == t).Select(e => (e.Offset, e.Applied)).ToArray(), restoring))
                throw new InvalidOperationException("RS6 mapped bytes changed outside attempted owned DWORDs; restoration stopped.");
        }
        renderOwner.Validate();
    }
    internal void Apply()
    {
        foreach (var edit in edits)
        {
            Validate(); attempted.Add(edit); // Own the attempt even if the write throws before/after mutation.
            write(edit.Target.Mapped + (ulong)edit.Offset, edit.Applied); Validate();
        }
        Console.WriteLine($"Applied {(output == 22 ? "BOTH" : output == 21 ? "LEFT" : "RIGHT")} RS6 {Description}; authored intensity retained.");
    }
    internal void Restore()
    {
        Validate(restoring: true);
        foreach (var edit in edits.Where(attempted.Contains).Reverse().ToArray())
        {
            Validate(restoring: true); write(edit.Target.Mapped + (ulong)edit.Offset, edit.Original);
            attempted.Remove(edit); Validate(restoring: true);
        }
        Validate();
        Console.WriteLine("RESTORED AND VERIFIED: both complete original RS6 main-lamp renderer buffers exactly match captured bytes.");
    }
    private static bool ValidOutput(uint output) => output is 20 or 21 or 22;
    internal static bool ShouldRoute(bool left, uint output) => output switch
    {
        21 => left, 20 => !left, 22 => true,
        _ => throw new ArgumentOutOfRangeException(nameof(output), "RS6 main-lamp output must be 20, 21 or 22.")
    };
    private static bool OwnedOffset(int offset, uint output, bool rear) => ValidOutput(output) &&
        (offset == 0xC0 || !rear && offset is 0x40 or 0x44 or 0x48);
    // Reject overflow too; renderer destinations must never alias either CPU
    // source, even when their captured bytes happen to be identical.
    private static bool Overlaps(ulong first, ulong second) =>
        first > ulong.MaxValue - BufferSize || second > ulong.MaxValue - BufferSize ||
        first < second + BufferSize && second < first + BufferSize;
    private static bool ValidEmissionLayout(byte[] values, bool left, bool rear) =>
        values.Length == BufferSize && values.SequenceEqual(Baseline(rear, left));

    internal static void SelfTest()
    {
        var count = 0;
        void Check(bool value)
        {
            if (!value) throw new InvalidOperationException($"RS6 main-lamp check {count + 1} failed.");
            count++;
        }
        foreach (var rear in new[] { false, true })
        foreach (var left in new[] { false, true })
        {
            var original = Baseline(rear, left);
            Check(ValidEmissionLayout(original, left, rear));
            Check(!ValidEmissionLayout(original[..255], left, rear));
            Check(BitConverter.ToSingle(original, 0xB0) == (rear ? 16f : 5.657f));
            Check(BitConverter.ToUInt32(original, 0xC0) == (rear ? 0u : 15u));
            for (var offset = 0; offset < BufferSize; offset++)
            {
                var foreign = original.ToArray(); foreign[offset] ^= 1;
                Check(!ValidEmissionLayout(foreign, left, rear));
            }
            foreach (var output in new uint[] { 20, 21, 22 })
            {
                Check(ShouldRoute(left, output) == (output == 22 || (left ? output == 21 : output == 20)));
                var trigger = 22u;
                (int Offset, byte[] Applied)[] fields = !ShouldRoute(left, output) ? Array.Empty<(int, byte[])>()
                    : rear ? new[] { (0xC0, BitConverter.GetBytes(trigger)) }
                    : new[] { (0xC0, BitConverter.GetBytes(trigger)), (0x40, BitConverter.GetBytes(1f)),
                        (0x44, BitConverter.GetBytes(.35f)), (0x48, BitConverter.GetBytes(.015f)) };
                Check(fields.All(f => OwnedOffset(f.Offset, output, rear)));
                for (var mask = 0; mask < (1 << fields.Length); mask++)
                {
                    var partial = original.ToArray();
                    for (var f = 0; f < fields.Length; f++)
                        if ((mask & (1 << f)) != 0) fields[f].Applied.CopyTo(partial, fields[f].Offset);
                    Check(ChargerDrlRoute.IsOwnedBuffer(partial, original, fields, true));
                }
                var applied = original.ToArray();
                foreach (var field in fields) field.Applied.CopyTo(applied, field.Offset);
                Check(ChargerDrlRoute.IsOwnedBuffer(applied, original, fields, false));
                for (var offset = 0; offset < BufferSize; offset++)
                {
                    var foreign = applied.ToArray(); foreign[offset] ^= 1;
                    Check(!ChargerDrlRoute.IsOwnedBuffer(foreign, original, fields, true));
                }
                Check(applied.AsSpan(0x4C, 4).SequenceEqual(original.AsSpan(0x4C, 4)));
                Check(applied.AsSpan(0xB0, 4).SequenceEqual(original.AsSpan(0xB0, 4)));
                Check(applied.AsSpan(0xC4).SequenceEqual(original.AsSpan(0xC4)));
                Check(!rear || applied.AsSpan(0x40, 16).SequenceEqual(original.AsSpan(0x40, 16)));
                Check(!OwnedOffset(0xB0, output, rear) && !OwnedOffset(0x4C, output, rear));
            }
        }
        foreach (var output in new uint[] { 0, 19, 23, 70, uint.MaxValue })
        {
            var reads = 0; var writes = 0; var rejected = false;
            try
            {
                _ = new Rs6MainLampRoute((_, n) => { reads++; return new byte[n]; },
                    (_, _) => { writes++; }, () => true, (_, _) => true, 0, 0, 0,
                    Array.Empty<(ulong, ulong, ulong, string)>(), Rs6LampCandidate.FrontDrl, output);
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && reads == 0 && writes == 0);
        }
        Check(Overlaps(0x1000, 0x10FF) && !Overlaps(0x1000, 0x1100));
        Check(Overlaps(ulong.MaxValue, 0x1000) && Overlaps(0x1000, ulong.MaxValue));
        Console.WriteLine($"PASS: {count} pure RS6 main-lamp exact-profile/selected-DWORD checks; no game process opened or written.");
    }
}
