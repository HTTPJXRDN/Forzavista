using System.Diagnostics;
using System.Text;

// Shared guarded RSX route for the menu and bounded diagnostic. Discovers every target from exact lamp-owned
// named material tables; never chooses nearest unnamed materials. The parked
// source-refresh experiment is retained for research only. The compiled mode
// requires fourteen distinct owned buffers and exact shader trigger signatures;
// only the selected side's seven trigger dwords are temporarily changed.
internal sealed class RsxSideMaterialRoute
{
    private sealed record Target(ulong Wrapper, ulong Instance, ulong Resource, string Path,
        ulong Definition, ulong Table, ulong Count, ulong Record, uint Id, string Name,
        ulong Material, ulong Begin, ulong End, ulong Parameter, byte[] Header, uint Output);
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<bool> sameCar;
    private readonly ulong module;
    private readonly List<Target> targets = [];
    private readonly HashSet<ulong> applied = [];
    private readonly uint output;
    private sealed record CompiledTarget(Target Source, ulong Compiled, ulong Values, ulong End, ulong Slot, byte[] Original);
    private readonly bool useCompiled;
    private readonly List<CompiledTarget> compiledTargets = [];
    private readonly HashSet<ulong> compiledApplied = [];
    private readonly bool useMapped;
    private readonly Func<ulong, int, bool>? writable;
    private sealed record MappedTarget(CompiledTarget Source, ulong Binding, ulong Buffer, byte[] Descriptor,
        ulong Page, byte[] PageHeader, ulong Values, ulong Slot, byte[] Original, byte Flags);
    private readonly List<MappedTarget> mappedTargets = [];
    private readonly HashSet<ulong> mappedApplied = [];
    private ulong uploadService, uploadBackend, uploadVtable, uploadPages, uploadPagesEnd, uploadPageSize, uploadBase;
    private ulong activeVehicle, activePresentation, activeBindings, sharedLampOwner;

    private ulong Pointer(ulong address) => BitConverter.ToUInt64(read(address, 8));
    private static bool Heap(ulong value) => value is >= 0x10000000 and < 0x7FF000000000;
    private static bool Emissive(Target target) => System.IO.Path.GetFileName(target.Path).Contains("bulbs", StringComparison.OrdinalIgnoreCase)
        || target.Name == "gls_red";
    internal RsxSideMaterialRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, ulong module,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, uint output,
        bool useCompiled = false, bool useMapped = false, Func<ulong, int, bool>? writable = null, ulong vehicle = 0)
    {
        this.read = read; this.write = write; this.sameCar = sameCar; this.module = module; this.output = output;
        this.useCompiled = useCompiled || useMapped; this.useMapped = useMapped;
        this.writable = writable;
        if (useMapped && writable is null) throw new InvalidOperationException("Mapped route requires memory protection validation.");
        if (output is not (20 or 21) || models.Count != 8 ||
            models.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 8)
            throw new InvalidOperationException("Expected the exact eight RSX lamp owners; no material write attempted.");
        if (useMapped)
        {
            activeVehicle = vehicle;
            if (!Heap(vehicle)) throw new InvalidOperationException("Mapped route needs the fresh current-player vehicle.");
            activePresentation = Pointer(vehicle + 0x7918);
            activeBindings = Pointer(activePresentation + 0x18);
            sharedLampOwner = Pointer(models[0].Instance + 0x38);
            if (!Heap(activePresentation) || !Heap(activeBindings) || !Heap(sharedLampOwner) ||
                Pointer(sharedLampOwner) != module + 0x6474B18 ||
                Pointer(activeBindings + 0x60) != sharedLampOwner + 0x20 ||
                Pointer(sharedLampOwner + 0x20) != activeBindings + 0x60 ||
                models.Any(m => Pointer(m.Instance + 0x38) != sharedLampOwner))
                throw new InvalidOperationException("RSX player-presentation / lamp-owner reciprocal binding did not match; no writes.");
            Console.WriteLine("Verified direct current-player presentation / shared lamp-owner reciprocal binding.");
        }
        foreach (var model in models)
        {
            if (!sameCar()) throw new InvalidOperationException("Car changed during material discovery.");
            var definition = Pointer(model.Instance + 0x28);
            var table = Pointer(definition + 0x140);
            var count = Pointer(definition + 0x148);
            var filename = System.IO.Path.GetFileName(model.Path).ToLowerInvariant();
            if (!Heap(table) || count != (filename.Contains("bulbs") ? filename.StartsWith("headlight") ? 6UL : 3UL
                    : filename.StartsWith("headlight") ? 12UL : 8UL))
                throw new InvalidOperationException("RSX owned material-table count did not match.");
            var entries = read(table, checked((int)(count * 0x38)));
            for (var index = 0; index < (int)count; index++)
            {
                var offset = index * 0x38;
                var length = BitConverter.ToUInt64(entries, offset + 0x18);
                var capacity = BitConverter.ToUInt64(entries, offset + 0x20);
                if (length is < 1 or > 127 || capacity < length || capacity > 255)
                    throw new InvalidOperationException("Invalid material name layout.");
                var name = Encoding.ASCII.GetString(capacity < 16 ? entries.AsSpan(offset + 8, (int)length).ToArray()
                    : read(BitConverter.ToUInt64(entries, offset + 8), (int)length));
                var id = BitConverter.ToUInt32(entries, offset);
                var material = BitConverter.ToUInt64(entries, offset + 0x28);
                if (!Heap(material) || Pointer(material) != module + 0x65EB720 ||
                    Pointer(material - 0x10) != module + 0x6475280 ||
                    BitConverter.ToUInt64(entries, offset + 0x30) != material - 0x10)
                    throw new InvalidOperationException("Owned MaterialInstance identity mismatch.");
                var begin = Pointer(material + 8); var end = Pointer(material + 0x10);
                if (begin == 0 && end == 0) continue; // Authored materials with no inline parameters.
                if (!Heap(begin) || end < begin || end - begin > 1024 || (end - begin) % 8 != 0)
                    throw new InvalidOperationException("Invalid owned parameter-vector layout.");
                var members = read(begin, checked((int)(end - begin)));
                for (var p = 0; p < members.Length; p += 8)
                {
                    var parameter = BitConverter.ToUInt64(members, p);
                    var header = read(parameter, 0x18);
                    if (BitConverter.ToUInt64(header) != module + 0x65786F8 ||
                        BitConverter.ToUInt32(header, 0x10) != 70) continue;
                    var isLeft = filename.StartsWith("headlightl") || filename.StartsWith("taillightl");
                    targets.Add(new(model.Wrapper, model.Instance, model.Resource, model.Path,
                        definition, table, count, table + (ulong)offset, id, name, material, begin, end,
                        parameter, header, isLeft ? 21u : 20u));
                }
            }
        }
        // The game's compact property indices are assigned per launch. Infer
        // only from the complete, disk-authored named set, then cross-check
        // each shader's exact compiled trigger block below. Never reuse an ID
        // from a historical process as a semantic property name.
        var emissiveIds = targets.Where(Emissive).Select(t => BitConverter.ToUInt32(t.Header, 9) & 0xFFFFFF).Distinct().ToArray();
        var radiosityIds = targets.Where(t => !Emissive(t)).Select(t => BitConverter.ToUInt32(t.Header, 9) & 0xFFFFFF).Distinct().ToArray();
        if (emissiveIds.Length != 1 || radiosityIds.Length != 1 || emissiveIds[0] == radiosityIds[0] ||
            emissiveIds[0] == 0 || radiosityIds[0] == 0)
            throw new InvalidOperationException("Authored signal property classes are ambiguous; no writes.");
        Console.WriteLine($"Current-session signal property indices: emissive=0x{emissiveIds[0]:X}; radiosity=0x{radiosityIds[0]:X}.");
        // Expected names/ids independently checked against the stock modelbins.
        bool Authored(Target t)
        {
            var file = System.IO.Path.GetFileName(t.Path).ToLowerInvariant();
            if (file.Contains("bulbs")) return file.StartsWith("headlight")
                ? t.Id == 3 && t.Name == (t.Output == 21 ? "gls_lightBulb_002_left_turn_signle" : "gls_lightBulb_002_right_turn_signle")
                : t.Id == 1 && t.Name == "turn_signal";
            if (file.StartsWith("taillight")) return t.Id == 2 && t.Name == "turn" ||
                t.Id == 7 && t.Name == "tail_light_on_lbk_custom_CH1IlluminationMap_red";
            return t.Id == 7 && t.Name == "gls_red" ||
                t.Id == 8 && t.Name == "lbk_custom_CH1IlluminationMap_turn_light" ||
                t.Id == 9 && t.Name == (t.Output == 21 ? "LBK_TintMapCH1_001" : "LBK_aluminum_001");
        }
        if (targets.Count != 14 || targets.Count(t => t.Output == 21) != 7 ||
            targets.Count(t => t.Output == 20) != 7 || targets.Any(t => !Authored(t)) ||
            targets.Select(t => t.Material).Distinct().Count() != 14 ||
            targets.Select(t => t.Parameter).Distinct().Count() != 14)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "rsx-material-set-mismatch",
                Count = targets.Count, Left = targets.Count(t => t.Output == 21), Right = targets.Count(t => t.Output == 20),
                DistinctMaterials = targets.Select(t => t.Material).Distinct().Count(),
                DistinctParameters = targets.Select(t => t.Parameter).Distinct().Count(),
                Targets = targets.Select(t => new { t.Path, t.Id, t.Name, t.Material, t.Parameter,
                    t.Output, Authored = Authored(t), Property = BitConverter.ToUInt32(t.Header, 9) & 0xFFFFFF }) }));
            throw new InvalidOperationException("Exact separate RSX left/right material set did not match; no writes.");
        }
        Validate();
        if (this.useCompiled)
        {
            foreach (var t in targets)
            {
                var compiled = Pointer(t.Material + 0x58);
                var values = Pointer(compiled + 0x10); var end = Pointer(compiled + 0x18);
                var emissive = Emissive(t);
                var size = emissive ? 0x1E0UL : 0x100UL;
                if (!Heap(compiled) || !Heap(values) || end != values + size)
                    throw new InvalidOperationException("Authored shader compiled-block layout changed; no writes.");
                var slot = values + (emissive ? 0x190UL : 0xC0UL);
                var original = read(slot, 12);
                var expected = emissive ? new uint[] { 70, 32, 69 } : new uint[] { 70, 0, 0 };
                if (!original.SequenceEqual(expected.SelectMany(BitConverter.GetBytes).ToArray()))
                    throw new InvalidOperationException("Compiled signal-trigger signature changed; no writes.");
                compiledTargets.Add(new(t, compiled, values, end, slot, original));
            }
            if (compiledTargets.Select(t => t.Compiled).Distinct().Count() != 14 ||
                compiledTargets.Select(t => t.Values).Distinct().Count() != 14)
                throw new InvalidOperationException("Left/right compiled signal bindings share storage; no writes.");
            Validate();
        }
        if (useMapped)
        {
            uploadService = Pointer(module + 0xA82C398);
            uploadBackend = Pointer(uploadService + 0x18);
            uploadVtable = Pointer(uploadBackend);
            if (uploadVtable < module || uploadVtable + 0x40 >= module + 0xB3C0000 ||
                Pointer(uploadVtable + 0x38) != module + 0x14FEC00 ||
                !read(module + 0x14FEC00, 0x11).SequenceEqual(Convert.FromHexString("4C8BDC49895B205556574883EC70498B18")) ||
                !read(module + 0x14FF170, 0x33).SequenceEqual(Convert.FromHexString(
                    "48895C241855565741544155415641574883EC204D8BF94C8BD24C8BE14C2B91A00000004D03D04C8B415033D2498BC249F7F0")))
                throw new InvalidOperationException("Mapped upload page-walk signature changed; no writes.");
            uploadPages = Pointer(uploadBackend + 0x78);
            uploadPagesEnd = Pointer(uploadBackend + 0x80);
            uploadPageSize = Pointer(uploadBackend + 0x50);
            uploadBase = Pointer(uploadBackend + 0xA0);
            if (uploadPageSize != 0x2000000 || !Heap(uploadPages) || uploadPagesEnd <= uploadPages ||
                uploadPagesEnd - uploadPages > 0x10000 || (uploadPagesEnd - uploadPages) % 16 != 0)
                throw new InvalidOperationException("Mapped upload page table layout changed; no writes.");
            foreach (var t in compiledTargets)
            {
                var binding = Pointer(t.Compiled + 0x70);
                if (!Heap(binding)) throw new InvalidOperationException("No owned mapped lamp binding.");
                var buffer = Pointer(binding);
                if (!Heap(buffer) || Pointer(binding + 8) != buffer - 0x10 || Pointer(buffer) != module + 0x65E6788)
                    throw new InvalidOperationException("Mapped lamp buffer resource identity changed; no writes.");
                var descriptor = read(binding + 0x10, 0x20);
                var address = BitConverter.ToUInt64(descriptor, 8);
                var count = BitConverter.ToUInt32(descriptor, 0x18);
                if (address < uploadBase || count != t.End - t.Values ||
                    (descriptor[0x10] & 7) == 0 || (address - uploadBase) / uploadPageSize >= (uploadPagesEnd - uploadPages) / 16 ||
                    (address - uploadBase) % uploadPageSize + count > uploadPageSize)
                    throw new InvalidOperationException("Mapped lamp buffer descriptor changed; no writes.");
                var relative = address - uploadBase;
                var page = uploadPages + relative / uploadPageSize * 16;
                var pageHeader = read(page, 16);
                var values = BitConverter.ToUInt64(pageHeader, 8) + relative % uploadPageSize;
                var original = read(t.Values, (int)count);
                if (!Heap(values) || (values & 3) != 0 || ((values + t.Slot - t.Values) & 3) != 0 ||
                    !writable!(values, (int)count) || !read(values, (int)count).SequenceEqual(original))
                    throw new InvalidOperationException("Entire mapped lamp buffer must match its CPU source; no writes.");
                mappedTargets.Add(new(t, binding, buffer, descriptor, page, pageHeader, values,
                    values + t.Slot - t.Values, original, read(t.Source.Material + 0x80, 1)[0]));
            }
            if (mappedTargets.Select(t => t.Binding).Distinct().Count() != 14 ||
                mappedTargets.Any(t => mappedTargets.Any(other => t != other &&
                    t.Values < other.Values + (ulong)other.Original.Length && other.Values < t.Values + (ulong)t.Original.Length)))
                throw new InvalidOperationException("Mapped left/right lamp storage overlaps; no writes.");
            Validate();
            Console.WriteLine("Verified fourteen non-overlapping renderer-facing buffers; complete bytes match CPU copies.");
        }
        Console.WriteLine($"Verified 14 separate RSX signal material bindings; selected {output} has seven targets.");
    }
    internal void Validate(bool restoring = false)
    {
        if (!sameCar()) throw new InvalidOperationException("Car/scene changed; material access stopped.");
        if (useMapped && (Pointer(activeVehicle + 0x7918) != activePresentation ||
            Pointer(activePresentation + 0x18) != activeBindings || Pointer(sharedLampOwner) != module + 0x6474B18 ||
            Pointer(activeBindings + 0x60) != sharedLampOwner + 0x20 || Pointer(sharedLampOwner + 0x20) != activeBindings + 0x60))
            throw new InvalidOperationException("Current-player lamp-owner binding changed; access stopped.");
        foreach (var t in targets)
        {
            if (Pointer(t.Wrapper) != module + 0x65C6408 || Pointer(t.Wrapper + 0x60) != t.Instance ||
                Pointer(t.Instance) != module + 0x6474E38 || Pointer(t.Instance + 0x20) != t.Resource ||
                Pointer(t.Resource) != module + 0x6474CA8 || Pointer(t.Instance + 0x28) != t.Definition ||
                useMapped && Pointer(t.Instance + 0x38) != sharedLampOwner ||
                Pointer(t.Definition + 0x140) != t.Table || Pointer(t.Definition + 0x148) != t.Count ||
                Pointer(t.Record + 0x28) != t.Material || BitConverter.ToUInt32(read(t.Record, 4)) != t.Id ||
                Pointer(t.Material) != module + 0x65EB720 || Pointer(t.Material + 8) != t.Begin ||
                Pointer(t.Material + 0x10) != t.End)
                throw new InvalidOperationException("Tracked lamp material ownership changed.");
            var current = read(t.Parameter, 0x18);
            var expected = t.Header.ToArray();
            if (applied.Contains(t.Parameter)) BitConverter.GetBytes(output).CopyTo(expected, 0x10);
            if (!current.SequenceEqual(expected)) throw new InvalidOperationException("Tracked signal parameter changed externally.");
            var members = read(t.Begin, checked((int)(t.End - t.Begin)));
            if (!Enumerable.Range(0, members.Length / 8).Any(p => BitConverter.ToUInt64(members, p * 8) == t.Parameter))
                throw new InvalidOperationException("Signal parameter no longer belongs to its lamp material.");
        }
        foreach (var t in compiledTargets)
        {
            if (Pointer(t.Source.Material + 0x58) != t.Compiled || Pointer(t.Compiled + 0x10) != t.Values ||
                Pointer(t.Compiled + 0x18) != t.End)
                throw new InvalidOperationException("Compiled lamp binding ownership changed.");
            var expected = t.Original.ToArray();
            if (compiledApplied.Contains(t.Slot)) BitConverter.GetBytes(output).CopyTo(expected, 0);
            if (!read(t.Slot, 12).SequenceEqual(expected))
                throw new InvalidOperationException("Compiled signal trigger changed externally.");
        }
        if (mappedTargets.Count != 0)
        {
            if (Pointer(module + 0xA82C398) != uploadService || Pointer(uploadService + 0x18) != uploadBackend ||
                Pointer(uploadBackend) != uploadVtable || Pointer(uploadBackend + 0x78) != uploadPages ||
                Pointer(uploadBackend + 0x80) != uploadPagesEnd || Pointer(uploadBackend + 0x50) != uploadPageSize ||
                Pointer(uploadBackend + 0xA0) != uploadBase)
                throw new InvalidOperationException("Render upload ownership changed; access stopped.");
            foreach (var t in mappedTargets)
            {
                if (Pointer(t.Source.Compiled + 0x70) != t.Binding ||
                    Pointer(t.Binding) != t.Buffer || Pointer(t.Binding + 8) != t.Buffer - 0x10 ||
                    Pointer(t.Buffer) != module + 0x65E6788 || !writable!(t.Values, t.Original.Length) ||
                    !read(t.Binding + 0x10, 0x20).SequenceEqual(t.Descriptor) ||
                    !read(t.Page, 16).SequenceEqual(t.PageHeader) ||
                    read(t.Source.Source.Material + 0x80, 1)[0] != t.Flags ||
                    !read(t.Source.Values, t.Original.Length).SequenceEqual(t.Original))
                    throw new InvalidOperationException("Owned mapped lamp descriptor/source changed.");
                var expected = t.Original.ToArray();
                if (mappedApplied.Contains(t.Slot)) BitConverter.GetBytes(output).CopyTo(expected, (int)(t.Slot - t.Values));
                var current = read(t.Values, expected.Length);
                if (!current.SequenceEqual(expected) && !(restoring && current.SequenceEqual(t.Original)))
                    throw new InvalidOperationException("Mapped signal buffer changed externally.");
            }
        }
    }
    internal void Inspect()
    {
        Validate();
        var materialUpdate = Pointer(module + 0x65EB720 + 11 * 8);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "material-update-code", Function = materialUpdate,
            Bytes = Convert.ToHexString(read(materialUpdate, 0x300)) }));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "numeric-material-sync-code", Function = module + 0x1400F80,
            Bytes = Convert.ToHexString(read(module + 0x1400F80, 0x300)) }));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "material-buffer-upload-code", Function = module + 0x13F8810,
            Bytes = Convert.ToHexString(read(module + 0x13F8810, 0x300)) }));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "buffer-upload-queue-code", Function = module + 0x1105270,
            Bytes = Convert.ToHexString(read(module + 0x1105270, 0x600)) }));
        var uploadService = Pointer(module + 0xA82C398);
        var backend = Pointer(uploadService + 0x18);
        var enqueue = Pointer(Pointer(backend) + 0x10);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "buffer-upload-backend-code", Service = uploadService,
            Backend = backend, Function = enqueue, Bytes = Convert.ToHexString(read(enqueue, 0x600)) }));
        var upload = Pointer(Pointer(backend) + 0x38);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "buffer-upload-implementation-code",
            Backend = backend, Function = upload, Bytes = Convert.ToHexString(read(upload, 0x800)) }));
        var backendState = read(backend, 0xB0);
        var pageSize = BitConverter.ToUInt64(backendState, 0x50);
        var pages = BitConverter.ToUInt64(backendState, 0x78);
        var pagesEnd = BitConverter.ToUInt64(backendState, 0x80);
        var addressBase = BitConverter.ToUInt64(backendState, 0xA0);
        foreach (var owner in targets.Select(t => Pointer(t.Instance + 0x38)).Distinct())
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "instance-shared-material-owner", Owner = owner,
                Header = Convert.ToHexString(read(owner, 0x200)) }));
        foreach (var t in targets)
        {
            var compiled = Pointer(t.Material + 0x58);
            var header = read(compiled, 0x100);
            var gpuBinding = BitConverter.ToUInt64(header, 0x70);
            if (Heap(gpuBinding))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "material-buffer-binding", t.Path, t.Name,
                    Binding = gpuBinding, Header = Convert.ToHexString(read(gpuBinding, 0x80)) }));
                var address = Pointer(gpuBinding + 0x18);
                if (pageSize == 0x2000000 && Heap(pages) && pagesEnd >= pages && (pagesEnd - pages) % 16 == 0 &&
                    address >= addressBase && (address - addressBase) / pageSize < (pagesEnd - pages) / 16)
                {
                    var relative = address - addressBase;
                    var page = pages + relative / pageSize * 16;
                    var pageBytes = read(page, 16);
                    var mapped = BitConverter.ToUInt64(pageBytes, 8) + relative % pageSize;
                    var valueBegin = BitConverter.ToUInt64(header, 0x10);
                    var valueEnd = BitConverter.ToUInt64(header, 0x18);
                    if (Heap(mapped) && valueEnd > valueBegin && valueEnd - valueBegin <= 0x1000 &&
                        relative % pageSize + valueEnd - valueBegin <= pageSize)
                    {
                        var cpu = read(valueBegin, (int)(valueEnd - valueBegin));
                        var mappedBytes = read(mapped, cpu.Length);
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "mapped-material-read",
                            t.Path, t.Name, Backend = backend, Pages = pages, AddressBase = addressBase, PageSize = pageSize,
                            Address = address, Page = page, PageHeader = Convert.ToHexString(pageBytes), Mapped = mapped,
                            Length = cpu.Length, Equal = cpu.SequenceEqual(mappedBytes), Bytes = Convert.ToHexString(mappedBytes) }));
                    }
                }
                var buffer = Pointer(gpuBinding);
                if (Heap(buffer))
                {
                    var raw = read(buffer, 0x120);
                    var vtable = BitConverter.ToUInt64(raw);
                    if (vtable > module && vtable < module + 0xB3C0000)
                    {
                        var flush = Pointer(vtable + 8);
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "material-buffer-resource", t.Path, t.Name,
                            Buffer = buffer, Header = Convert.ToHexString(raw), Flush = flush,
                            FlushBytes = Convert.ToHexString(read(flush, 0x80)) }));
                    }
                }
            }
            var begin = BitConverter.ToUInt64(header, 0x10);
            var end = BitConverter.ToUInt64(header, 0x18);
            var bytes = Heap(begin) && end > begin && end - begin <= 0x1000
                ? read(begin, (int)(end - begin)) : Array.Empty<byte>();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { t.Path, t.Name, t.Material,
                t.Parameter, Trigger = BitConverter.ToUInt32(read(t.Parameter + 0x10, 4)),
                Flags = read(t.Material + 0x80, 1)[0], Compiled = compiled,
                Header = Convert.ToHexString(header), Values = begin, Length = bytes.Length,
                Bytes = Convert.ToHexString(bytes) }));
        }
        Validate();
    }
    internal void ClearPendingRefresh()
    {
        // Recovery for this session's cancelled source-parameter test only.
        // Original read-only capture recorded 0x48 on all fourteen materials.
        Validate();
        if (targets.Any(t => read(t.Material + 0x80, 1)[0] != (t.Output == 21 ? 0x49 : 0x48)))
            throw new InvalidOperationException("Pending-refresh recovery signature changed; no cleanup writes.");
        foreach (var t in targets.Where(t => t.Output == 21))
        {
            Validate();
            if (read(t.Material + 0x80, 1)[0] != 0x49)
                throw new InvalidOperationException("Refresh flag changed during cleanup.");
            write(t.Material + 0x80, [0x48]);
        }
        Validate();
        if (targets.Any(t => read(t.Material + 0x80, 1)[0] != 0x48))
            throw new InvalidOperationException("Original refresh flags not restored.");
        Console.WriteLine("CLEANUP VERIFIED: all fourteen triggers remain 70; all material flags restored to their captured 0x48 state.");
    }
    private void Rebuild(IEnumerable<Target> changed)
    {
        var list = changed.ToArray();
        if (list.Length == 0) return;
        Validate();
        foreach (var t in list)
        {
            Validate();
            var flags = read(t.Material + 0x80, 1)[0];
            write(t.Material + 0x80, [(byte)(flags | 1)]);
        }
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            Validate();
            if (list.All(t => (read(t.Material + 0x80, 1)[0] & 1) == 0))
            {
                Console.WriteLine($"Lamp material rebuild acknowledged after {clock.ElapsedMilliseconds}ms.");
                return;
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Material rebuild not acknowledged; native blink must not start.");
    }
    internal void Apply()
    {
        if (useMapped)
        {
            foreach (var t in mappedTargets.Where(t => t.Source.Source.Output == output))
            {
                Validate();
                mappedApplied.Add(t.Slot);
                write(t.Slot, BitConverter.GetBytes(output));
                Validate();
            }
            Console.WriteLine("Seven renderer-facing trigger dwords routed; CPU source, flags and other side untouched.");
            return;
        }
        if (useCompiled)
        {
            foreach (var t in compiledTargets.Where(t => t.Source.Output == output))
            {
                Validate();
                compiledApplied.Add(t.Slot);
                write(t.Slot, BitConverter.GetBytes(output));
                Validate();
            }
            Console.WriteLine("Seven already-compiled lamp bindings routed; source parameters/refresh flags unchanged.");
            return;
        }
        foreach (var t in targets.Where(t => t.Output == output))
        {
            Validate();
            applied.Add(t.Parameter);
            write(t.Parameter + 0x10, BitConverter.GetBytes(output));
            Validate();
        }
        Rebuild(targets.Where(t => applied.Contains(t.Parameter)));
    }
    internal void Restore()
    {
        Validate(restoring: useMapped);
        if (useMapped)
        {
            foreach (var t in mappedTargets.Where(t => mappedApplied.Contains(t.Slot)).ToArray())
            {
                Validate(restoring: true);
                write(t.Slot, t.Original.AsSpan((int)(t.Slot - t.Values), 4).ToArray());
                mappedApplied.Remove(t.Slot);
                Validate(restoring: true);
            }
            Console.WriteLine("RESTORED AND VERIFIED: all fourteen renderer-facing buffers exactly match their original complete bytes.");
            return;
        }
        if (useCompiled)
        {
            foreach (var t in compiledTargets.Where(t => compiledApplied.Contains(t.Slot)).ToArray())
            {
                Validate();
                write(t.Slot, t.Original.AsSpan(0, 4).ToArray());
                compiledApplied.Remove(t.Slot);
                Validate();
            }
            Console.WriteLine("RESTORED AND VERIFIED: all original compiled RSX left/right lamp trigger bindings.");
            return;
        }
        var changed = targets.Where(t => applied.Contains(t.Parameter)).ToArray();
        foreach (var t in changed)
        {
            Validate();
            write(t.Parameter + 0x10, t.Header.AsSpan(0x10, 4).ToArray());
            applied.Remove(t.Parameter);
        }
        Rebuild(changed);
        Validate();
        Console.WriteLine("RESTORED AND VERIFIED: all original RSX material trigger bindings.");
    }
}
