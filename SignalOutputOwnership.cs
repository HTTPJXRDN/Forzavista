namespace ForzavistaFreeRoam;

// Pure policy for the menu's owned hazard output-ID transition. An attempted
// write may have left either complete ID; malformed/foreign/duplicate ownership
// is never accepted, including during restoration. No process access here.
internal static class SignalOutputOwnership
{
    internal static bool TryResolve(ulong controller, bool attempted, uint actualId,
        IReadOnlyDictionary<uint, ulong> live, out bool routed70)
    {
        routed70 = false;
        if (controller < 0x10000) return false;
        if (actualId == 22)
            return live.TryGetValue(22, out var original) && original == controller && !live.ContainsKey(70);
        if (actualId == 70 && attempted)
        {
            if (!live.TryGetValue(70, out var current) || current != controller || live.ContainsKey(22)) return false;
            routed70 = true;
            return true;
        }
        return false;
    }
}
