namespace ForzavistaFreeRoam;

// Verified running-tail layouts. Output 69's brake operand and combine stay stock.
internal static class RearLampExpressionPolicy
{
    internal static bool IsSupported(uint output, IReadOnlyList<byte[]> instructions)
    {
        if (instructions.Count == 0 || instructions[0].Length != 24 ||
            BitConverter.ToUInt32(instructions[0], 8) is not (0 or 5 or 11)) return false;
        var first = instructions[0].ToArray();
        BitConverter.GetBytes(5u).CopyTo(first, 8);
        if (output == 32)
            return instructions.Count == 1 && first.SequenceEqual(Convert.FromHexString(
                "010000001B00000005000000000000000000000000000000"));
        return output == 69 && instructions.Count == 3 &&
            first.SequenceEqual(Convert.FromHexString(
                "09000000130000000500000000000000000000000000003E")) &&
            instructions[1].SequenceEqual(Convert.FromHexString(
                "09000000140000000100000000000000000000000000803F")) &&
            instructions[2].SequenceEqual(Convert.FromHexString(
                "0E0000001B00000013000000140000000000000000000000"));
    }
}
