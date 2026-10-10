namespace ForzavistaFreeRoam;

// Read-only proof of the current car's authored, engine-gated lightbar strobe.
// Never changes controllers, their timing, instructions or output values.
internal sealed class NativeDrlStrobeSource
{
    internal const uint Trigger = 49;
    private readonly Func<ulong, int, byte[]> read;
    private readonly Func<bool> sameCar;
    private readonly List<(ulong Address, byte[] Bytes)> snapshots = [];
    private readonly ulong vehicle;
    private ulong controller;
    private static readonly byte[][] Expressions =
    [Convert.FromHexString("08000000130000000F000000100000000000000000000000"),
     Convert.FromHexString("080000001B000000130000000B0000000000000000000000")];

    internal NativeDrlStrobeSource(Func<ulong, int, byte[]> read, Func<bool> sameCar,
        ulong vehicle, ulong sentinel, ulong count)
    {
        this.read = read; this.sameCar = sameCar; this.vehicle = vehicle;
        Require(sameCar() && Heap(vehicle) && Heap(sentinel) && count is > 0 and <= 128, "Native strobe car/graph bounds changed.");
        var graph = Capture(vehicle + 0x9258, 16);
        Require(U64(graph) == sentinel && U64(graph, 8) == count, "Native strobe graph identity changed.");
        var ends = Capture(sentinel, 16);
        var node = U64(ends); var previous = sentinel;
        var nodes = new HashSet<ulong>(); var outputs = new HashSet<uint>();
        while (node != sentinel && (ulong)nodes.Count < count)
        {
            Require(Heap(node) && nodes.Add(node), "Native strobe controller list is invalid.");
            var header = Capture(node, 24); var owner = U64(header, 16);
            Require(U64(header, 8) == previous && Heap(owner), "Native strobe controller owner/link changed.");
            var output = BitConverter.ToUInt32(Capture(owner, 4));
            Require(outputs.Add(output), "Native strobe output ID is duplicated.");
            if (output == Trigger)
            {
                controller = owner;
                var definition = Capture(owner + 0x18, 32);
                var instructionSentinel = U64(definition, 16);
                Require(Heap(U64(definition)) && U64(definition, 8) == 2 && Heap(instructionSentinel) &&
                    U64(definition, 24) == 2, "Native strobe49 kind/count changed.");
                var instructionEnds = Capture(instructionSentinel, 16);
                var instruction = U64(instructionEnds); var back = instructionSentinel;
                var instructions = new HashSet<ulong>();
                foreach (var expected in Expressions)
                {
                    Require(Heap(instruction) && instruction != instructionSentinel && instructions.Add(instruction),
                        "Native strobe49 instruction list changed.");
                    var bytes = Capture(instruction, 40);
                    Require(U64(bytes, 8) == back && bytes.AsSpan(16, 24).SequenceEqual(expected),
                        "Native strobe49 authored engine-gated definition changed.");
                    back = instruction; instruction = U64(bytes);
                }
                Require(instruction == instructionSentinel && U64(instructionEnds, 8) == back,
                    "Native strobe49 instruction tail changed.");
            }
            previous = node; node = U64(header);
        }
        Require(node == sentinel && (ulong)nodes.Count == count && U64(ends, 8) == previous && controller != 0,
            "Complete current-car graph with native strobe49 required.");
        Validate();
    }

    internal void Validate()
    {
        Require(sameCar(), "Native strobe player identity changed.");
        foreach (var snapshot in snapshots)
            Require(Bytes(snapshot.Address, snapshot.Bytes.Length).SequenceEqual(snapshot.Bytes),
                "Native strobe49 graph/definition changed; no controller writes attempted.");
        var value = BitConverter.ToSingle(Bytes(controller + 0x10, 4));
        Require(float.IsFinite(value) && value is >= 0 and <= 1.01f, "Native strobe49 output is invalid.");
        Require(sameCar(), "Native strobe identity changed during validation.");
    }

    internal const string EngineOffMessage = "Start the engine before enabling native DRL strobe. RGB and fixed colors can be used without strobe.";
    internal bool EngineRunning
    {
        get
        {
            var value = Bytes(vehicle + 0x924E, 1)[0];
            Require(value <= 1, "Native engine input is invalid.");
            return value == 1;
        }
    }
    internal void RequireEngine() => Require(EngineRunning, EngineOffMessage);

    internal bool DrlEnabled
    {
        get
        {
            var value = Bytes(vehicle + 0x924A, 1)[0];
            Require(value <= 1, "Native DRL input is invalid.");
            return value == 1;
        }
    }

    private byte[] Capture(ulong address, int size)
    {
        var bytes = Bytes(address, size); snapshots.Add((address, bytes)); return bytes;
    }
    private byte[] Bytes(ulong address, int size)
    {
        Require(Heap(address) && size is > 0 and <= 0x100, "Native strobe read exceeds bounds.");
        var bytes = read(address, size); Require(bytes.Length == size, "Native strobe read is incomplete."); return bytes;
    }
    private static bool Heap(ulong address) => address is >= 0x10000 and < 0x800000000000;
    private static ulong U64(byte[] bytes, int offset = 0) => BitConverter.ToUInt64(bytes, offset);
    private static void Require(bool result, string message)
    { if (!result) throw new InvalidOperationException(message); }
}
