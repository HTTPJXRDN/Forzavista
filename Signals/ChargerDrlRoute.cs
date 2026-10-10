using System.IO;
using System.Text;

// Shared guarded 2015 Charger front-DRL / rear brake-turn route, not a universal-car mapper.
// Only renderer-facing DWORDs are changed; native sequence ownership stays in
// Program.cs. CPU source, material flags, center/reverse/plate lights and
// high-beam reference materials are never written. Rear-only mode changes
// only the selected side's brake-emitter trigger and restores its brake link.
internal sealed class ChargerDrlRoute
{
    private sealed record Target(ulong Wrapper, ulong Instance, ulong Resource, ulong Definition,
        ulong Table, ulong Count, ulong Record, uint Id, string Name, ulong Material, ulong Begin, ulong End,
        ulong Trigger, byte[] TriggerHeader, ulong Color, byte[] ColorHeader, ulong Compiled,
        ulong Cpu, ulong Binding, ulong Buffer, byte[] Descriptor, ulong Page, byte[] PageHeader,
        ulong Mapped, byte[] Original, byte Flags, bool Left);
    private sealed record Edit(Target Target, int Offset, byte[] Original, byte[] Applied);
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<bool> sameCar;
    private readonly Func<ulong, int, bool> writable;
    private readonly ulong module;
    private readonly ChargerRenderOwner renderOwner;
    private readonly (ulong Wrapper, ulong Instance, ulong Resource, string Path)[] modelOwners;
    private readonly ulong service, backend, backendVtable, pages, pagesEnd, pageSize, gpuBase;
    private readonly List<Target> targets = [];
    private readonly List<Edit> edits = [];
    private readonly HashSet<Edit> attempted = [];
    private readonly bool rear;
    private readonly bool hazards;
    private readonly bool combined;
    private ulong Pointer(ulong address) => BitConverter.ToUInt64(read(address, 8));
    private static bool Heap(ulong address) => address is >= 0x10000000 and < 0x7FF000000000 && (address & 7) == 0;

    internal ChargerDrlRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, uint output, bool amber, bool rear = false,
        bool includeFrontAndRear = false)
    {
        rear |= includeFrontAndRear;
        this.read = read; this.write = write; this.sameCar = sameCar; this.writable = writable;
        this.module = module;
        hazards = output == 22;
        combined = includeFrontAndRear && !hazards;
        this.rear = rear && !hazards;
        rear |= hazards; // Full hazards validates and includes both rear emitters.
        if (rear && amber && !hazards && !includeFrontAndRear) throw new InvalidOperationException("Rear red brake/turn tint must remain unchanged.");
        if (output is not (20 or 21 or 22) || models.Count != 6 ||
            models.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 6)
            throw new InvalidOperationException("Expected six separate Charger lamp model owners; no writes.");
        renderOwner = ChargerRenderOwner.Capture(read, sameCar, module, vehicle, component);
        modelOwners = models.ToArray();
        ValidateModelOwners();
        service = Pointer(module + 0xA82C398); backend = Pointer(service + 0x18);
        backendVtable = Pointer(backend); pages = Pointer(backend + 0x78); pagesEnd = Pointer(backend + 0x80);
        pageSize = Pointer(backend + 0x50); gpuBase = Pointer(backend + 0xA0);
        if (!Heap(service) || !Heap(backend) || !Heap(pages) || pagesEnd <= pages ||
            pagesEnd - pages > 0x10000 || (pagesEnd - pages) % 16 != 0 || pageSize != 0x2000000 ||
            backendVtable < module || backendVtable + 0x40 >= module + 0xB3C0000 ||
            Pointer(backendVtable + 0x38) != module + 0x14FEC00 ||
            !read(module + 0x14FEC00, 0x11).SequenceEqual(Convert.FromHexString("4C8BDC49895B205556574883EC70498B18")) ||
            !read(module + 0x14FF170, 0x33).SequenceEqual(Convert.FromHexString(
                "48895C241855565741544155415641574883EC204D8BF94C8BD24C8BE14C2B91A00000004D03D04C8B415033D2498BC249F7F0")))
            throw new InvalidOperationException("Charger mapped upload identity/signature mismatch; no writes.");
        foreach (var model in models.Where(m => Path.GetFileName(m.Path).Equals("headlightL_a.modelbin", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(m.Path).Equals("headlightR_a.modelbin", StringComparison.OrdinalIgnoreCase) ||
            rear && (Path.GetFileName(m.Path).Equals("taillightL_a.modelbin", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(m.Path).Equals("taillightR_a.modelbin", StringComparison.OrdinalIgnoreCase))))
        {
            if (!sameCar()) throw new InvalidOperationException("Player changed during Charger material discovery.");
            var definition = Pointer(model.Instance + 0x28);
            var table = Pointer(definition + 0x140);
            var tableCount = Path.GetFileName(model.Path).StartsWith("taillight", StringComparison.OrdinalIgnoreCase) ? 9UL : 12UL;
            if (!Heap(definition) || !Heap(table) || Pointer(definition + 0x148) != tableCount)
                throw new InvalidOperationException("Charger headlamp named-material table mismatch.");
            var entries = read(table, checked((int)(tableCount * 0x38)));
            for (var index = 0; index < (int)tableCount; index++)
            {
                var offset = index * 0x38;
                var length = BitConverter.ToUInt64(entries, offset + 0x18);
                var capacity = BitConverter.ToUInt64(entries, offset + 0x20);
                if (length is < 1 or > 127 || capacity < length || capacity > 255)
                    throw new InvalidOperationException("Invalid Charger material name layout.");
                var name = Encoding.ASCII.GetString(capacity < 16 ? entries.AsSpan(offset + 8, (int)length).ToArray()
                    : read(BitConverter.ToUInt64(entries, offset + 8), (int)length));
                if (name is not ("lights_smooth_CH1_custom_DRL" or "lights_smooth_CH1_custom_headhigh" or "lights_smooth_CH1_custom_br")) continue;
                var drl = name.EndsWith("_DRL", StringComparison.Ordinal);
                var brake = name.EndsWith("_br", StringComparison.Ordinal);
                if (brake != (tableCount == 9)) throw new InvalidOperationException("Charger lamp material belongs to an unexpected model.");
                var expectedTrigger = brake ? 0u : drl ? 15u : 70u;
                var id = BitConverter.ToUInt32(entries, offset);
                var material = BitConverter.ToUInt64(entries, offset + 0x28);
                if (id != (brake ? 6u : drl ? 8u : 2u) || !Heap(material) || Pointer(material) != module + 0x65EB720 ||
                    Pointer(material - 0x10) != module + 0x6475280 ||
                    BitConverter.ToUInt64(entries, offset + 0x30) != material - 0x10)
                    throw new InvalidOperationException("Charger material identity mismatch.");
                var begin = Pointer(material + 8); var end = Pointer(material + 0x10);
                if (!Heap(begin) || end < begin || end - begin > 1024 || (end - begin) % 8 != 0)
                    throw new InvalidOperationException("Invalid Charger parameter-vector bounds.");
                var members = read(begin, (int)(end - begin));
                var trigger = 0UL; byte[] triggerHeader = []; var color = 0UL; byte[] colorHeader = [];
                for (var p = 0; p < members.Length; p += 8)
                {
                    var parameter = BitConverter.ToUInt64(members, p);
                    if (!Heap(parameter)) throw new InvalidOperationException("Invalid Charger parameter pointer.");
                    var header = read(parameter, 0x18);
                    if (BitConverter.ToUInt64(header) == module + 0x65786F8 &&
                        BitConverter.ToUInt32(header, 0x10) == expectedTrigger)
                    {
                        if (trigger != 0) throw new InvalidOperationException("Ambiguous Charger trigger parameter.");
                        trigger = parameter; triggerHeader = header;
                    }
                    if (!drl && BitConverter.ToUInt64(header) == module + 0x6578438)
                    {
                        if (color != 0) throw new InvalidOperationException("Ambiguous Charger reference color.");
                        color = parameter; colorHeader = read(parameter, 0x20);
                    }
                }
                if (trigger == 0 || !drl && color == 0)
                    throw new InvalidOperationException("Authored Charger trigger/color source missing.");
                var compiled = Pointer(material + 0x58);
                var cpu = Pointer(compiled + 0x10);
                if (!Heap(compiled) || !Heap(cpu) || Pointer(compiled + 0x18) != cpu + 0x100)
                    throw new InvalidOperationException("Charger emitter shader block mismatch.");
                var original = read(cpu, 0x100);
                if (BitConverter.ToUInt32(original, 0xC0) != expectedTrigger ||
                    BitConverter.ToUInt32(original, 0xC4) != 0 || BitConverter.ToUInt32(original, 0xC8) != 0 ||
                    !original.AsSpan(0xF0, 16).SequenceEqual(Convert.FromHexString("11010000120100000000000000000000")) ||
                    drl && !original.AsSpan(0x40, 16).SequenceEqual(Enumerable.Repeat(1f, 4).SelectMany(BitConverter.GetBytes).ToArray()) ||
                    !drl && !original.AsSpan(0x40, 16).SequenceEqual(colorHeader.AsSpan(0x10, 16)))
                    throw new InvalidOperationException("Charger shader trigger/tint signature mismatch.");
                var authoredColor = brake ? new[] { 1f, .008568126f, .001214108f, 1f } : new[] { .71569353f, .791298f, .9646863f, 1f };
                if (!drl && !colorHeader.AsSpan(0x10, 16).SequenceEqual(authoredColor.SelectMany(BitConverter.GetBytes).ToArray()))
                    throw new InvalidOperationException("Disk-authored Charger emission tint reference changed.");
                var binding = Pointer(compiled + 0x70); var buffer = Pointer(binding);
                var descriptor = read(binding + 0x10, 0x20);
                var gpu = BitConverter.ToUInt64(descriptor, 8);
                if (!Heap(binding) || !Heap(buffer) || Pointer(buffer) != module + 0x65E6788 ||
                    Pointer(binding + 8) != buffer - 0x10 || gpu < gpuBase ||
                    BitConverter.ToUInt32(descriptor, 0x18) != 0x100 || (descriptor[0x10] & 7) == 0 ||
                    (gpu - gpuBase) / pageSize >= (pagesEnd - pages) / 16 || (gpu - gpuBase) % pageSize + 0x100 > pageSize)
                    throw new InvalidOperationException("Charger mapped buffer descriptor mismatch.");
                var relative = gpu - gpuBase; var page = pages + relative / pageSize * 16;
                var pageHeader = read(page, 16); var mapped = BitConverter.ToUInt64(pageHeader, 8) + relative % pageSize;
                if (!Heap(mapped) || !writable(mapped, 0x100) || !read(mapped, 0x100).SequenceEqual(original))
                    throw new InvalidOperationException("Entire Charger mapped buffer must match its CPU copy.");
                targets.Add(new(model.Wrapper, model.Instance, model.Resource, definition, table, tableCount, table + (ulong)offset,
                    id, name, material, begin, end, trigger, triggerHeader, color, colorHeader, compiled,
                    cpu, binding, buffer, descriptor, page, pageHeader, mapped, original, read(material + 0x80, 1)[0],
                    Path.GetFileName(model.Path).Equals("headlightL_a.modelbin", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(model.Path).Equals("taillightL_a.modelbin", StringComparison.OrdinalIgnoreCase)));
            }
        }
        var expectedCount = rear ? 6 : 4;
        if (targets.Count != expectedCount || targets.Count(t => t.Left) != expectedCount / 2 ||
            targets.Select(t => t.Material).Distinct().Count() != expectedCount ||
            targets.Select(t => t.Cpu).Distinct().Count() != expectedCount ||
            targets.Select(t => BitConverter.ToUInt32(t.TriggerHeader, 9) & 0xFFFFFF).Distinct().Count() != 1 ||
            targets.Any(t => targets.Any(other => t != other && t.Mapped < other.Mapped + 0x100 && other.Mapped < t.Mapped + 0x100)))
            throw new InvalidOperationException("Charger named material set is incomplete, ambiguous or overlapping.");
        var selectedTargets = targets.Where(t => ShouldRoute(t.Left, t.Id, output, rear, includeFrontAndRear)).ToArray();
        if (selectedTargets.Length != (hazards ? 4 : includeFrontAndRear ? 2 : 1)) throw new InvalidOperationException("Unexpected selected Charger signal set.");
        foreach (var selected in selectedTargets)
        {
            AddEdit(selected, 0xC0, BitConverter.GetBytes(selected.Left ? 21u : 20u));
            if (amber && selected.Id == 8)
            {
                AddEdit(selected, 0x40, BitConverter.GetBytes(1f));
                AddEdit(selected, 0x44, BitConverter.GetBytes(.35f));
                AddEdit(selected, 0x48, BitConverter.GetBytes(.015f));
            }
        }
        Validate();
        Console.WriteLine(hazards ? "Verified Charger current-player ownership and six separate renderer buffers; both amber fronts and red rear brake/turn routes prepared without writes."
            : $"Verified Charger current-player ownership, separate lamp buffers and emission tint layout; {(output == 21 ? "LEFT" : "RIGHT")} {(combined ? "front amber and rear red brake/turn" : rear ? "rear red brake/turn" : amber ? "front amber" : "front original-color")} route prepared without writes.");
    }
    private void AddEdit(Target target, int offset, byte[] bytes)
    {
        if (bytes.Length != 4 || (offset & 3) != 0 || offset < 0 || offset + 4 > target.Original.Length)
            throw new InvalidOperationException("Only bounded aligned renderer DWORDs may be changed.");
        var original = target.Original.AsSpan(offset, 4).ToArray();
        if (!original.SequenceEqual(bytes)) edits.Add(new(target, offset, original, bytes));
    }
    internal static bool ShouldRoute(bool left, uint id, uint output, bool rear, bool includeFrontAndRear)
        => (output == 22 || includeFrontAndRear ? id is 6 or 8 : id == (rear ? 6u : 8u)) &&
            (output == 22 || left == (output == 21));
    internal void Validate(bool restoring = false)
    {
        renderOwner.Validate();
        ValidateModelOwners();
        if (Pointer(module + 0xA82C398) != service || Pointer(service + 0x18) != backend ||
            Pointer(backend) != backendVtable || Pointer(backend + 0x78) != pages || Pointer(backend + 0x80) != pagesEnd ||
            Pointer(backend + 0x50) != pageSize || Pointer(backend + 0xA0) != gpuBase)
            throw new InvalidOperationException("Charger renderer upload ownership changed.");
        foreach (var t in targets)
        {
            if (Pointer(t.Wrapper) != module + 0x65C6408 || Pointer(t.Wrapper + 0x60) != t.Instance ||
                Pointer(t.Wrapper + 0x68) != t.Instance - 0x10 || Pointer(t.Instance) != module + 0x6474E38 ||
                Pointer(t.Instance + 0x20) != t.Resource || Pointer(t.Resource) != module + 0x6474CA8 ||
                !renderOwner.MatchesInstance(t.Instance) || Pointer(t.Instance + 0x28) != t.Definition ||
                Pointer(t.Definition + 0x140) != t.Table || Pointer(t.Definition + 0x148) != t.Count ||
                BitConverter.ToUInt32(read(t.Record, 4)) != t.Id || Pointer(t.Record + 0x28) != t.Material ||
                Pointer(t.Record + 0x30) != t.Material - 0x10 || Pointer(t.Material) != module + 0x65EB720 ||
                Pointer(t.Material - 0x10) != module + 0x6475280 || Pointer(t.Material + 8) != t.Begin ||
                Pointer(t.Material + 0x10) != t.End || !read(t.Trigger, 0x18).SequenceEqual(t.TriggerHeader) ||
                t.Color != 0 && !read(t.Color, 0x20).SequenceEqual(t.ColorHeader) ||
                read(t.Material + 0x80, 1)[0] != t.Flags || Pointer(t.Material + 0x58) != t.Compiled ||
                Pointer(t.Compiled + 0x10) != t.Cpu || Pointer(t.Compiled + 0x18) != t.Cpu + 0x100 ||
                Pointer(t.Compiled + 0x70) != t.Binding || Pointer(t.Binding) != t.Buffer ||
                Pointer(t.Binding + 8) != t.Buffer - 0x10 || Pointer(t.Buffer) != module + 0x65E6788 ||
                !read(t.Binding + 0x10, 0x20).SequenceEqual(t.Descriptor) || !read(t.Page, 16).SequenceEqual(t.PageHeader) ||
                !writable(t.Mapped, 0x100) || !read(t.Cpu, 0x100).SequenceEqual(t.Original))
                throw new InvalidOperationException("Charger material ownership/source/descriptor changed.");
            var members = read(t.Begin, checked((int)(t.End - t.Begin)));
            bool Member(ulong address) => Enumerable.Range(0, members.Length / 8).Any(i => BitConverter.ToUInt64(members, i * 8) == address);
            if (!Member(t.Trigger) || t.Color != 0 && !Member(t.Color))
                throw new InvalidOperationException("Charger source parameter no longer belongs to its named material.");
            var current = read(t.Mapped, 0x100);
            if (!IsOwnedBuffer(current, t.Original, attempted.Where(e => e.Target == t).Select(e => (e.Offset, e.Applied)).ToArray(), restoring))
                throw new InvalidOperationException("Charger renderer bytes changed outside the owned fields; no stale restoration.");
        }
    }
    private void ValidateModelOwners()
    {
        foreach (var m in modelOwners)
            if (!Heap(m.Wrapper) || !Heap(m.Instance) || !Heap(m.Resource) ||
                Pointer(m.Wrapper) != module + 0x65C6408 || Pointer(m.Wrapper + 0x60) != m.Instance ||
                Pointer(m.Wrapper + 0x68) != m.Instance - 0x10 || Pointer(m.Instance) != module + 0x6474E38 ||
                Pointer(m.Instance + 0x20) != m.Resource || Pointer(m.Resource) != module + 0x6474CA8 ||
                !renderOwner.MatchesInstance(m.Instance))
                throw new InvalidOperationException("Charger lamp model no longer belongs to the captured player scene; no writes.");
    }
    internal void Apply()
    {
        foreach (var edit in edits)
        {
            Validate(); attempted.Add(edit); // Track before even an attempted write.
            write(edit.Target.Mapped + (ulong)edit.Offset, edit.Applied); Validate();
        }
        Console.WriteLine(hazards ? "Only both front DRL trigger/tint and rear brake/turn trigger fields changed; center brake, running strip, reverse/plate, source and flags untouched."
            : combined ? "Only selected-side front amber DRL and rear red brake/turn renderer fields changed; opposite side, center brake, running strip, source and flags untouched."
            : rear ? "Only selected rear brake/turn renderer trigger changed; front, opposite brake, center brake, running-light strip, source and flags untouched."
            : "Only selected front DRL renderer DWORDs changed; opposite DRL, source, flags, headlights and all rear materials untouched.");
    }
    internal void Restore()
    {
        Validate(restoring: true);
        foreach (var edit in edits.Where(e => attempted.Contains(e)).Reverse().ToArray())
        {
            Validate(restoring: true); write(edit.Target.Mapped + (ulong)edit.Offset, edit.Original);
            attempted.Remove(edit); Validate(restoring: true);
        }
        Validate();
        Console.WriteLine($"RESTORED AND VERIFIED: all {targets.Count} Charger tracked renderer buffers exactly match their original complete bytes.");
    }
    internal static bool IsOwnedBuffer(byte[] current, byte[] original, (int Offset, byte[] Applied)[] fields, bool restoring)
    {
        if (current.Length != original.Length || fields.Select(f => f.Offset).Distinct().Count() != fields.Length ||
            fields.Any(f => f.Applied.Length != 4 || f.Offset < 0 || (f.Offset & 3) != 0 || f.Offset > original.Length - 4)) return false;
        var copy = current.ToArray();
        foreach (var field in fields)
        {
            var now = copy.AsSpan(field.Offset, 4);
            var before = original.AsSpan(field.Offset, 4);
            if (!now.SequenceEqual(field.Applied) && !(restoring && now.SequenceEqual(before))) return false;
            before.CopyTo(now);
        }
        return copy.SequenceEqual(original);
    }
    internal static void SelfTest()
    {
        var count = 0;
        void Check(bool passed) { if (!passed) throw new InvalidOperationException($"Charger pure guard check {count + 1} failed."); count++; }
        var original = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var trigger = (Offset: 0xC0, Applied: BitConverter.GetBytes(21u));
        var green = (Offset: 0x44, Applied: BitConverter.GetBytes(.35f));
        var blue = (Offset: 0x48, Applied: BitConverter.GetBytes(.015f));
        var applied = original.ToArray(); trigger.Applied.CopyTo(applied, trigger.Offset);
        Check(IsOwnedBuffer(original, original, [], false));
        Check(IsOwnedBuffer(applied, original, [trigger], false));
        Check(!IsOwnedBuffer(original, original, [trigger], false));
        Check(IsOwnedBuffer(original, original, [trigger], true));
        Check(IsOwnedBuffer(applied, original, [trigger], true));
        Check(!IsOwnedBuffer(original[..255], original, [], true));
        Check(!IsOwnedBuffer(original, original, [(1, new byte[4])], true));
        Check(!IsOwnedBuffer(original, original, [(-4, new byte[4])], true));
        Check(!IsOwnedBuffer(original, original, [(256, new byte[4])], true));
        Check(!IsOwnedBuffer(original, original, [(252, new byte[8])], true));
        Check(!IsOwnedBuffer(applied, original, [trigger, trigger], true));
        var foreign = applied.ToArray(); foreign[0] ^= 1;
        Check(!IsOwnedBuffer(foreign, original, [trigger], true));
        foreign = applied.ToArray(); foreign[0xC0] ^= 1;
        Check(!IsOwnedBuffer(foreign, original, [trigger], true));
        green.Applied.CopyTo(applied, green.Offset);
        Check(IsOwnedBuffer(applied, original, [trigger, green, blue], true));
        Check(!IsOwnedBuffer(applied, original, [trigger, green, blue], false));
        blue.Applied.CopyTo(applied, blue.Offset);
        Check(IsOwnedBuffer(applied, original, [trigger, green, blue], false));
        var captured = applied.ToArray();
        Check(IsOwnedBuffer(applied, original, [trigger, green, blue], true) && applied.SequenceEqual(captured));
        foreign = applied.ToArray(); foreign[0x4C] ^= 1; // Alpha is not owned.
        Check(!IsOwnedBuffer(foreign, original, [trigger, green, blue], true));
        Check(applied.AsSpan(0x50, 0x70).SequenceEqual(original.AsSpan(0x50, 0x70)));
        var brakeOriginal = new byte[0x100]; var brakeApplied = brakeOriginal.ToArray();
        trigger.Applied.CopyTo(brakeApplied, trigger.Offset);
        Check(IsOwnedBuffer(brakeApplied, brakeOriginal, [trigger], false));
        Check(IsOwnedBuffer(brakeOriginal, brakeOriginal, [trigger], true));
        Console.WriteLine($"PASS: {count} pure Charger renderer-DWORD guard checks; no game process opened or written.");
    }
}
