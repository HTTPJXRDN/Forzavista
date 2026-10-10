// Fixed Steam RS6 composition. LEFT/RIGHT owns front ID12 + upgraded mirror
// + rear ID5 on that side. Native22 is renamed70 while held OFF, preserving
// the earlier accepted mirror path. Exact bumper/trunk70 consumers and the
// opposite mirror are disconnected to undriven22. Front/mirror amber,
// authored rear red. No separate UI action.
internal sealed class Rs6SignalRoute
{
    private readonly Rs6MirrorRoute mirror;
    private readonly Rs6MainLampRoute front, rear, isolation;
    private readonly uint output;
    internal Rs6SignalRoute(Func<ulong, int, byte[]> read, Action<ulong, byte[]> write,
        Func<bool> sameCar, Func<ulong, int, bool> writable, ulong module, ulong vehicle, ulong component,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models, uint output, bool correctRightMask = false)
    {
        if (output is not (20 or 21 or 22)) throw new ArgumentOutOfRangeException(nameof(output));
        if (correctRightMask && output != 20) throw new ArgumentException("Diagnostic mask candidate is RIGHT-only.");
        if (models.Count != 8 || models.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 8)
            throw new InvalidOperationException("Exact RS6 six-lamp plus bumper/trunk isolation model set required; no writes.");
        this.output = output;
        // These callbacks run only after all three read-only captures finish.
        // Attempts are already tracked by each route, so a pre-write check
        // accepts exact original/applied attempted DWORDs, never foreign bytes.
        void GuardedWrite(ulong address, byte[] bytes)
        {
            Validate(restoring: true);
            write(address, bytes);
            Validate(restoring: true);
        }
        mirror = new Rs6MirrorRoute(read, GuardedWrite, sameCar, writable, module, vehicle, component,
            models, brighter: true, output: output, nativeTrigger: 70, correctRightMask: correctRightMask);
        front = new Rs6MainLampRoute(read, GuardedWrite, sameCar, writable, module, vehicle, component,
            models, Rs6LampCandidate.FrontDrl, output, nativeTrigger: 70);
        rear = new Rs6MainLampRoute(read, GuardedWrite, sameCar, writable, module, vehicle, component,
            models, Rs6LampCandidate.RearRegion5, output, nativeTrigger: 70);
        isolation = new Rs6MainLampRoute(read, GuardedWrite, sameCar, writable, module, vehicle, component,
            models, Rs6LampCandidate.IsolateStock70, 22);
        Validate();
        Console.WriteLine($"Prepared RS6 {(output == 22 ? "ALL SIX hazards" : output == 21 ? "ALL THREE LEFT" : "ALL THREE RIGHT")} without writes.");
    }
    private static bool Overlap(ulong first, ulong second) => first > ulong.MaxValue - 0x100 ||
        second > ulong.MaxValue - 0x100 || first < second + 0x100 && second < first + 0x100;
    internal static bool Disjoint(IReadOnlyList<(ulong Mapped, ulong Cpu)> buffers) =>
        buffers.Count == 8 && buffers.All(b => b.Mapped <= ulong.MaxValue - 0x100 && b.Cpu <= ulong.MaxValue - 0x100) &&
        !buffers.Where((b, i) => buffers.Where((other, j) => i != j).Any(other => Overlap(b.Mapped, other.Mapped))).Any() &&
        !buffers.Any(b => buffers.Any(other => Overlap(b.Mapped, other.Cpu)));
    internal void Validate(bool restoring = false)
    {
        mirror.Validate(restoring); front.Validate(restoring); rear.Validate(restoring); isolation.Validate(restoring);
        var buffers = mirror.Buffers.Concat(front.Buffers).Concat(rear.Buffers).Concat(isolation.Buffers).ToArray();
        if (!Disjoint(buffers))
            throw new InvalidOperationException("RS6 complete eight-buffer renderer stores alias each other or a CPU source; no writes.");
        foreach (var storage in mirror.ExtraStorage)
            foreach (var buffer in buffers)
                if (Rs6MirrorMaskCandidate.Overlaps(storage.Address, storage.Size, buffer.Mapped, 0x100) ||
                    Rs6MirrorMaskCandidate.Overlaps(storage.Address, storage.Size, buffer.Cpu, 0x100))
                    throw new InvalidOperationException("RS6 experimental texture cache aliases a lamp/isolation shader buffer.");
    }
    internal void Apply()
    {
        Validate(); isolation.Apply(); Validate(); mirror.Apply(); Validate(); front.Apply(); Validate(); rear.Apply(); Validate();
        Console.WriteLine($"RS6 {(output == 22 ? "six hazard" : output == 21 ? "three LEFT" : "three RIGHT")} lamps routed together.");
    }
    internal void Restore()
    {
        // Caller holds native zero until these complete mapped buffers restore.
        Validate(restoring: true); rear.Restore(); front.Restore(); mirror.Restore(); isolation.Restore(); Validate();
        Console.WriteLine("RESTORED AND VERIFIED: all eight complete original RS6 lamp/isolation buffers.");
    }
    internal static void SelfTest()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("RS6 six-lamp alias guard failed."); count++; }
        var buffers = Enumerable.Range(0, 8).Select(i => (Mapped: 0x40000000UL + (ulong)i * 0x1000,
            Cpu: 0x50000000UL + (ulong)i * 0x1000)).ToArray();
        Check(Disjoint(buffers)); Check(!Disjoint(buffers[..5]));
        for (var first = 0; first < 8; first++)
        for (var other = 0; other < 8; other++)
        {
            var conflict = buffers.ToArray(); conflict[first].Mapped = buffers[other].Mapped;
            Check(Disjoint(conflict) == (first == other));
            conflict = buffers.ToArray(); conflict[first].Mapped = buffers[other].Cpu;
            Check(!Disjoint(conflict));
            conflict = buffers.ToArray(); conflict[first].Mapped = buffers[other].Cpu + 0xF8;
            Check(!Disjoint(conflict));
        }
        var overflow = buffers.ToArray(); overflow[0].Mapped = ulong.MaxValue;
        Check(!Disjoint(overflow)); overflow = buffers.ToArray(); overflow[0].Cpu = ulong.MaxValue;
        Check(!Disjoint(overflow));
        foreach (var invalid in new uint[] { 0, 19, 23, 70, uint.MaxValue })
        {
            var reads = 0; var writes = 0; var rejected = false;
            try
            {
                _ = new Rs6SignalRoute((_, n) => { reads++; return new byte[n]; }, (_, _) => { writes++; },
                    () => true, (_, _) => true, 0, 0, 0, Array.Empty<(ulong, ulong, ulong, string)>(), invalid);
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && reads == 0 && writes == 0);
        }
        Console.WriteLine($"PASS: {count} pure RS6 combined eight-buffer admission/alias checks; no game process opened.");
    }
}

