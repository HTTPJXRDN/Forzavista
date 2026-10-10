using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForzavistaFreeRoam;

// One short-lived identity-keyed entry, not a cache of writable routes or
// original render bytes. Cached addresses still undergo all route guards.
internal sealed class SignalDiscoveryCache<TKey, TValue>(Func<long>? clock = null) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _generation;
    private (TKey Key, TValue? Value, string? Error, long Expires)? _entry;

    internal long Generation { get { lock (_gate) return _generation; } }
    internal bool TryGet(TKey key, out TValue? value, out string? error)
    {
        lock (_gate)
        {
            value = default; error = null;
            if (_entry is not { } entry || !EqualityComparer<TKey>.Default.Equals(key, entry.Key) ||
                _clock() >= entry.Expires) return false;
            value = entry.Value; error = entry.Error;
            return true;
        }
    }

    internal void Remember(TKey key, TValue? value, string? error, long generation)
    {
        lock (_gate)
        {
            if (_generation != generation) return; // A cancelled old car cannot repopulate the cache.
            _entry = (key, value, error, _clock() + (error is null ? 30000 : 10000));
        }
    }

    internal void Clear()
    {
        lock (_gate) { _entry = null; _generation++; }
    }
}

internal static class SignalScanPolicy
{
    // Keep each arena bounded to 256 MB. The fallback covers only the part of
    // the component arena outside the pose arena, so no page is scanned twice.
    internal static IEnumerable<(ulong Start, ulong Stop)> DrlSearchRanges(ulong pose, ulong component)
    {
        const ulong radius = 0x8000000;
        static (ulong Start, ulong Stop) Arena(ulong anchor)
        {
            if (anchor < radius + 0x10000 || anchor > ulong.MaxValue - radius)
                throw new InvalidOperationException("Lamp arena bounds invalid.");
            var start = (anchor & ~0xFFFFUL) - radius;
            return (start, start + radius * 2);
        }
        var primary = Arena(pose);
        var fallback = Arena(component);
        yield return primary;
        if (fallback.Start < primary.Start)
            yield return (fallback.Start, Math.Min(fallback.Stop, primary.Start));
        if (fallback.Stop > primary.Stop)
            yield return (Math.Max(fallback.Start, primary.Stop), fallback.Stop);
    }

    // Span.IndexOf is optimized in both Debug and Release. Avoid 33 million
    // individually interpreted BitConverter calls in the Debug activation path.
    internal static int[] AlignedMatches(byte[] bytes, ulong vtable, int? validLength = null)
    {
        var length = validLength ?? bytes.Length;
        if (length < 0 || length > bytes.Length) throw new ArgumentOutOfRangeException(nameof(validLength));
        var words = MemoryMarshal.Cast<byte, ulong>(bytes.AsSpan(0, length & ~7));
        var matches = new List<int>();
        var start = 0;
        while (start < words.Length)
        {
            var found = words[start..].IndexOf(vtable);
            if (found < 0) break;
            start += found;
            matches.Add(start * 8);
            start++;
        }
        return matches.ToArray();
    }
}
