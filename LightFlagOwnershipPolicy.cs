namespace ForzavistaFreeRoam;

internal static class LightFlagOwnershipPolicy
{
    internal static int[] ChangedOffsets(IReadOnlyDictionary<int, byte> last, Func<int, byte> read)
    {
        var changed = last.Where(pair => read(pair.Key) != pair.Value).Select(pair => pair.Key).ToHashSet();
        if (changed.Contains(0x9231) || changed.Contains(0x9248))
        {
            if (last.ContainsKey(0x9231)) changed.Add(0x9231);
            if (last.ContainsKey(0x9248)) changed.Add(0x9248);
        }
        return changed.ToArray();
    }
}
