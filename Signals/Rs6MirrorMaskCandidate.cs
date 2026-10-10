using System.Text;

// CLI diagnostic only; disabled in the menu. TXMP maps EmissiveCH1 to slot0x12,
// the third texture. Borrow only LEFT's cached slot+0xC index DWORD for RIGHT.
// No resource/view/shared_ptr addresses or reference counts change. Whether
// this cached index controls the operative draw is still a visual question.
internal sealed class Rs6MirrorMaskCandidate
{
    private sealed record Binding(ulong Parameter, byte[] Header, ulong PathAddress, string Path,
        ulong Resource, ulong Compiled, ulong Begin, byte[] Original);
    private const int Size = 6 * 0x20, Offset = 2 * 0x20 + 0xC;
    private const string LeftMask = @"Game:\Media\cars\_library\textures\lightglows\swatches\min_fenderlightmask_001_lite_07c6834e-92ba-4149-9da6-db730cfcd6a9.swatchbin";
    private const string RightMask = @"Game:\Media\_library\textures\carpacktypes\swatches\ch1_normao_diffopac_lite_562303fa-94b9-44d0-aa78-7cdd8845774c.swatchbin";
    private readonly Func<ulong, int, byte[]> read;
    private readonly Action<ulong, byte[]> write;
    private readonly Func<ulong, int, bool> writable;
    private readonly ulong module;
    private readonly Binding left, right;
    private readonly byte[] applied;
    private bool attempted;
    internal IEnumerable<(ulong Address, int Size)> Storage => new[] { (left.Begin, Size), (right.Begin, Size) };
    private static bool Heap(ulong p) => p is >= 0x10000000 and < 0x7FF000000000 && (p & 7) == 0;
    private byte[] Bytes(ulong p, int size)
    {
        if (size <= 0 || p > ulong.MaxValue - (ulong)size) throw new InvalidOperationException("RS6 mask read bounds invalid.");
        var value = read(p, size);
        if (value.Length != size) throw new InvalidOperationException("Short RS6 mask read.");
        return value;
    }
    private ulong Pointer(ulong p) => BitConverter.ToUInt64(Bytes(p, 8));
    internal Rs6MirrorMaskCandidate(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<ulong, int, bool> writable, ulong module, ulong leftParameter, ulong leftCompiled,
        ulong rightParameter, ulong rightCompiled)
    {
        this.read = read; this.write = write; this.writable = writable; this.module = module;
        left = Capture(leftParameter, leftCompiled, LeftMask); right = Capture(rightParameter, rightCompiled, RightMask);
        if (Overlaps(left.Begin, Size, right.Begin, Size) || left.Parameter == right.Parameter ||
            (BitConverter.ToUInt32(left.Header, 9) & 0xFFFFFF) != (BitConverter.ToUInt32(right.Header, 9) & 0xFFFFFF))
            throw new InvalidOperationException("RS6 mask source/binding pair ambiguous.");
        if (!left.Original.AsSpan(0, 0x40).SequenceEqual(right.Original.AsSpan(0, 0x40)) ||
            !left.Original.AsSpan(0x60).SequenceEqual(right.Original.AsSpan(0x60)))
            throw new InvalidOperationException("RS6 common texture-slot profile changed.");
        applied = left.Original.AsSpan(Offset, 4).ToArray();
        var index = BitConverter.ToUInt32(applied); var originalIndex = BitConverter.ToUInt32(right.Original, Offset);
        if (index == originalIndex || index >= 0x100000 || originalIndex >= 0x100000 ||
            BitConverter.ToUInt32(left.Original, Offset - 4) != uint.MaxValue ||
            BitConverter.ToUInt32(right.Original, Offset - 4) != uint.MaxValue)
            throw new InvalidOperationException("RS6 cached mask-index profile changed.");
        Validate();
        Console.WriteLine($"READ-ONLY PREPARED: diagnostic RIGHT EmissiveCH1 cached index {originalIndex:X} -> LEFT {index:X}; resource/view pointers unchanged; live effect unverified.");
    }
    private Binding Capture(ulong parameter, ulong compiled, string expectedPath)
    {
        if (!Heap(parameter) || !Heap(compiled)) throw new InvalidOperationException("RS6 mask object invalid.");
        var header = Bytes(parameter, 0x40); var length = BitConverter.ToUInt64(header, 0x28);
        var capacity = BitConverter.ToUInt64(header, 0x30); var pathAddress = BitConverter.ToUInt64(header, 0x18);
        var resource = BitConverter.ToUInt64(header, 0x10);
        if (BitConverter.ToUInt64(header) != module + 0x6578638 || header[8] != 5 || length is < 16 or > 512 ||
            capacity < length || capacity > 1024 || !Heap(pathAddress) || !Heap(resource) || Pointer(resource) != module + 0x6577DD0)
            throw new InvalidOperationException("RS6 exact typed mask parameter changed.");
        var path = Encoding.ASCII.GetString(Bytes(pathAddress, checked((int)length)));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RS6 exact authored mask texture path changed.");
        var begin = Pointer(compiled + 0x28);
        if (!Heap(begin) || Pointer(compiled + 0x30) != begin + Size || Pointer(compiled + 0x38) != begin + Size || !writable(begin, Size))
            throw new InvalidOperationException("RS6 exact six-texture writable cache changed.");
        var binding = new Binding(parameter, header, pathAddress, path, resource, compiled, begin, Bytes(begin, Size));
        ValidateObjects(binding);
        foreach (var range in new[] { (parameter, 0x40), (compiled, 0x80), (resource, 0x40), (pathAddress, (int)length) })
            if (Overlaps(begin, Size, range.Item1, range.Item2)) throw new InvalidOperationException("RS6 mask cache aliases a source object.");
        return binding;
    }
    private void ValidateObjects(Binding binding)
    {
        for (var slot = 0; slot < 6; slot++)
        {
            var objectAddress = BitConverter.ToUInt64(binding.Original, slot * 0x20);
            var view = BitConverter.ToUInt64(binding.Original, slot * 0x20 + 0x10);
            if (!Heap(objectAddress) || !Heap(view) || Pointer(objectAddress) != module + 0x63F51C0 ||
                Pointer(view) != module + 0x6887EB0 || BitConverter.ToUInt64(binding.Original, slot * 0x20 + 0x18) != view - 0x10)
                throw new InvalidOperationException("RS6 cached texture/view identity changed.");
        }
    }
    internal void Validate(bool restoring = false)
    {
        foreach (var binding in new[] { left, right })
        {
            if (!Bytes(binding.Parameter, 0x40).SequenceEqual(binding.Header) || Pointer(binding.Resource) != module + 0x6577DD0 ||
                !Encoding.ASCII.GetString(Bytes(binding.PathAddress, binding.Path.Length)).Equals(binding.Path, StringComparison.Ordinal) ||
                Pointer(binding.Compiled + 0x28) != binding.Begin || Pointer(binding.Compiled + 0x30) != binding.Begin + Size ||
                Pointer(binding.Compiled + 0x38) != binding.Begin + Size || !writable(binding.Begin, Size))
                throw new InvalidOperationException("RS6 mask source/cache ownership changed; no stale writes.");
            ValidateObjects(binding);
            var owned = binding == right && attempted ? new[] { (Offset, applied) } : Array.Empty<(int, byte[])>();
            if (!ChargerDrlRoute.IsOwnedBuffer(Bytes(binding.Begin, Size), binding.Original, owned, restoring))
                throw new InvalidOperationException("RS6 mask cache has foreign bytes; restoration stopped.");
        }
    }
    internal void Apply()
    {
        Validate(); attempted = true;
        write(right.Begin + Offset, applied); Validate();
        Console.WriteLine("APPLIED: RIGHT-only experimental cached mask-index DWORD; intensity remains5.657; all resource/view pointers unchanged.");
    }
    internal void Restore()
    {
        Validate(restoring: true);
        if (attempted) { write(right.Begin + Offset, right.Original.AsSpan(Offset, 4).ToArray()); attempted = false; }
        Validate();
        Console.WriteLine("RESTORED AND VERIFIED: both complete original six-texture caches and source headers/paths.");
    }
    internal static bool Overlaps(ulong a, int aSize, ulong b, int bSize) => a > ulong.MaxValue - (ulong)aSize ||
        b > ulong.MaxValue - (ulong)bSize || a < b + (ulong)bSize && b < a + (ulong)aSize;
}
