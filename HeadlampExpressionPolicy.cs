namespace ForzavistaFreeRoam;

// Pure byte-layout policy: no game access. Preserve every operand except source 5.
internal static class HeadlampExpressionPolicy
{
    internal static bool IsSupported(uint output, IReadOnlyList<byte[]> instructions)
    {
        if (instructions.Count == 0 || instructions[0].Length != 24 ||
            BitConverter.ToUInt32(instructions[0], 8) is not (0 or 5 or 11)) return false;
        // A previous menu instance may have exited with its known OFF/engine
        // operand. Recognize only those operands in the otherwise exact stock
        // expression; capture the actual operand as this session's original.
        var first = instructions[0].ToArray();
        BitConverter.GetBytes(5u).CopyTo(first, 8);
        if (output == 13)
            return instructions.Count == 1 && first.SequenceEqual(Convert.FromHexString(
                "010000001B00000005000000000000000000000000000000"));
        if (output is not (17 or 71) || instructions.Count != 3) return false;
        return first.SequenceEqual(Convert.FromHexString(
                "09000000130000000500000000000000000000000000803F")) &&
            instructions[1].SequenceEqual(Convert.FromHexString(output == 17
                ? "090000001400000008000000000000000000000000000040"
                : "09000000140000000800000000000000000000000000C03F")) &&
            instructions[2].SequenceEqual(Convert.FromHexString(
                "0E0000001B00000013000000140000000000000000000000"));
    }

    internal static byte[] WithNormalSource(byte[] original, uint source)
    {
        if (original.Length != 24 || source is not (0 or 5 or 11) ||
            BitConverter.ToUInt32(original, 8) is not (0 or 5 or 11))
            throw new ArgumentException("Unverified headlight source replacement.");
        var changed = original.ToArray();
        BitConverter.GetBytes(source).CopyTo(changed, 8);
        return changed;
    }
}
