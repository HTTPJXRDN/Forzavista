using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Shared query-only guard: no allocation, protection change, renderer call or memory write.
internal static class MappedRenderMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtection;
        public ushort Partition, Padding;
        public ulong Size;
        public uint State, Protection, Type, Padding2;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, ulong address, out Region region, nuint length);

    internal static bool ContainsWritable(ulong address, int length, ulong regionBase, ulong regionSize,
        uint state, uint protection) => length > 0 && state == 0x1000 &&
        (protection & 0x100) == 0 && (protection & 0xFF) is 4 or 8 &&
        address >= regionBase && address - regionBase < regionSize &&
        (ulong)length <= regionSize - (address - regionBase);

    internal static bool IsWritable(SafeProcessHandle process, ulong address, int length)
    {
        if (Marshal.SizeOf<Region>() != 48) throw new InvalidOperationException("Unexpected Windows memory-region layout.");
        return VirtualQueryEx(process, address, out var region, 48) == 48 &&
            ContainsWritable(address, length, region.Base, region.Size, region.State, region.Protection);
    }

    internal static void SelfTest()
    {
        var checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Mapped-memory guard regression."); checks++; }
        Check(Marshal.SizeOf<Region>() == 48);
        foreach (var protection in new uint[] { 4, 8, 0x204, 0x408 })
            Check(ContainsWritable(0x1100, 480, 0x1000, 0x1000, 0x1000, protection));
        foreach (var protection in new uint[] { 0, 1, 2, 0x20, 0x40, 0x80, 0x104, 0x108 })
            Check(!ContainsWritable(0x1100, 480, 0x1000, 0x1000, 0x1000, protection));
        Check(!ContainsWritable(0x1000, 4, 0x1000, 0x1000, 0x2000, 4));
        Check(!ContainsWritable(0xFFF, 4, 0x1000, 0x1000, 0x1000, 4));
        Check(!ContainsWritable(0x2000, 4, 0x1000, 0x1000, 0x1000, 4));
        Check(!ContainsWritable(0x1FFE, 4, 0x1000, 0x1000, 0x1000, 4));
        Check(ContainsWritable(0x1FFC, 4, 0x1000, 0x1000, 0x1000, 4));
        Check(!ContainsWritable(0x1100, 0, 0x1000, 0x1000, 0x1000, 4));
        Check(!ContainsWritable(0x1100, -1, 0x1000, 0x1000, 0x1000, 4));
        Check(!ContainsWritable(ulong.MaxValue, 4, 0x1000, 0x1000, 0x1000, 4));
        Console.WriteLine($"PASS: {checks} mapped-memory guard checks; no game opened.");
    }
}
