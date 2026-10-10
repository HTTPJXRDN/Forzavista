namespace ForzavistaFreeRoam;

// Only the normal/running operand is scaled. The brake/high-beam instructions
// and their MAX combine remain byte-for-byte stock.
internal static class LightFadePolicy
{
    internal const double DefaultSeconds = 0.5, MinimumSeconds = 0.1, MaximumSeconds = 5;
    internal static double Duration(double seconds) => double.IsFinite(seconds)
        ? Math.Clamp(Math.Round(seconds * 10) / 10, MinimumSeconds, MaximumSeconds) : DefaultSeconds;

    internal static byte[] Scale(byte[] original, uint source, float level)
    {
        if (original.Length != 24 || !float.IsFinite(level) || level is < 0 or > 1 ||
            BitConverter.ToUInt32(original, 0) is not (1 or 9) ||
            BitConverter.ToUInt32(original, 4) is not (19 or 27) ||
            BitConverter.ToUInt32(original, 8) is not (0 or 5 or 7 or 11) ||
            source is not (0 or 5 or 7 or 11))
            throw new InvalidOperationException("Unsupported normal-light fade operand.");
        var result = original.ToArray();
        BitConverter.GetBytes(9u).CopyTo(result, 0); // dst = input * immediate float
        BitConverter.GetBytes(source).CopyTo(result, 8);
        var authored = BitConverter.ToUInt32(original, 0) == 9 ? BitConverter.ToSingle(original, 20) : 1f;
        if (!float.IsFinite(authored) || authored is <= 0 or > 1)
            throw new InvalidOperationException("Unsupported normal-light fade intensity.");
        BitConverter.GetBytes(authored * level).CopyTo(result, 20);
        return result;
    }
}

internal sealed record LightFadeRamp(float Start, bool On, double StartedAt, double Seconds, bool NativeSwitch)
{
    internal float Level(double now)
    {
        double t = Math.Clamp((now - StartedAt) / Seconds, 0, 1);
        t = t * t * (3 - 2 * t);
        return (float)(Start + ((On ? 1 : 0) - Start) * t);
    }
    internal bool Complete(double now) => now - StartedAt >= Seconds;
}

// A failed Write/Read can occur before or after a DWORD reached the game. Own
// both exact possibilities until a read resolves it; never accept foreign bytes.
internal sealed class OwnedLightInstruction(byte[] original)
{
    internal byte[] Original { get; } = original.ToArray();
    internal byte[] Current { get; private set; } = original.ToArray();
    private byte[]? _pending;
    internal uint Source => BitConverter.ToUInt32(Current, 8);

    internal void Validate(Func<byte[]> read)
    {
        var live = read();
        if (live.SequenceEqual(Current)) { _pending = null; return; }
        if (_pending is not null && live.SequenceEqual(_pending))
        { Current = _pending; _pending = null; return; }
        throw new InvalidOperationException("Tracked light expression changed outside the menu.");
    }

    internal bool Set(byte[] desired, Func<byte[]> read, Action<int, byte[]> write,
        Action guard, Func<bool>? yield = null)
    {
        if (desired.Length != 24) throw new ArgumentException("Invalid light instruction.");
        foreach (int offset in new[] { 4, 12, 16 })
            if (!desired.AsSpan(offset, 4).SequenceEqual(Original.AsSpan(offset, 4)))
                throw new InvalidOperationException("The fade cannot replace other light operands.");
        // Prepare the multiplier before replacing COPY with MUL. When returning
        // to COPY, restore its opcode before clearing the now-unused multiplier.
        guard(); Validate(read);
        bool copy = BitConverter.ToUInt32(desired, 0) == 1;
        // Finishing OFF on a stock MUL branch must change its source to the
        // inactive input BEFORE restoring the authored gain. Otherwise that
        // one intermediate DWORD would briefly relight the lamp at full gain.
        bool changeSourceFirst = BitConverter.ToUInt32(Current, 0) == 9 &&
            BitConverter.ToUInt32(desired, 8) != Source &&
            BitConverter.ToSingle(desired, 20) > BitConverter.ToSingle(Current, 20);
        int[] order = copy ? [8, 0, 20] : changeSourceFirst ? [8, 20, 0] : [20, 0, 8];
        foreach (int offset in order)
        {
            if (yield?.Invoke() == true) return false;
            // The entry check has validated the complete current instruction.
            // Unchanged DWORDs need no additional graph traversal or readback.
            if (Current.AsSpan(offset, 4).SequenceEqual(desired.AsSpan(offset, 4))) continue;
            guard(); Validate(read);
            if (Current.AsSpan(offset, 4).SequenceEqual(desired.AsSpan(offset, 4))) continue;
            _pending = Current.ToArray();
            desired.AsSpan(offset, 4).CopyTo(_pending.AsSpan(offset, 4));
            write(offset, desired.AsSpan(offset, 4).ToArray());
            Validate(read); guard();
        }
        if (!Current.SequenceEqual(desired)) throw new InvalidOperationException("Incomplete light instruction update.");
        return true;
    }
}
