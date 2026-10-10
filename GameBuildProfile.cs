using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal sealed record GameBuildProfile
{
    internal required string Name { get; init; }
    internal string? Sha256 { get; init; }
    internal uint? PeTimestamp { get; init; }
    internal uint? SizeOfImage { get; init; }
    // Null selects live service-chain discovery for builds whose registry
    // alias moves between sessions.
    internal ulong? RootRegistryRva { get; init; }
    internal ulong? RootRegistryHintRva { get; init; }
    // Some builds expose the active service slot as a .data global; others
    // keep it in a runtime-owned structure.  A null value selects the bounded
    // live-slot discovery path in NativeCarControl.
    internal ulong? SlotIndexRva { get; init; }
    internal required ulong SubscriptionVtableRva { get; init; }
    internal required ulong CallbackVtableRva { get; init; }
    internal required ulong CallbackInterfaceVtableRva { get; init; }
    internal required ulong LambdaVtableRva { get; init; }
    internal required ulong HandlerRva { get; init; }
    internal required ulong OwnerVtableRva { get; init; }
    internal required ulong BooleanTriggerSetterRva { get; init; }
    internal ulong? PresentationServiceGlobalRva { get; init; }
    internal ulong? PresentationEventHubGlobalRva { get; init; }
    internal ulong? PresentationServiceVtableRva { get; init; }
    internal ulong? PresentationEventHubVtableRva { get; init; }
    internal ulong? RenderSystemGlobalRva { get; init; }
    internal ulong? CarRenderModelWrapperVtableRva { get; init; }
    internal ulong? CarModelInstanceVtableRva { get; init; }
    internal ulong? CarModelResourceVtableRva { get; init; }
    // Lighting is opt-in per verified build; older/Store mappings are not guessed.
    internal ulong? LightEvaluatorRva { get; init; }
    internal ulong? LightVehicleVtableRva { get; init; }
}

internal static class GameBuildProfiles
{
    private static readonly KnownExecutableFingerprintCache Fingerprints = new(IsSupportedDigest);

    internal static IReadOnlyList<GameBuildProfile> All { get; } =
    [
        new()
        {
            Name = "Steam 6.430.771.0",
            Sha256 = "B62B5EC1933B2D11A6B80941AE0D2B38C4A5AAEFDD880E487453D178081D7B44",
            PeTimestamp = 0x6A8B7D44,
            SizeOfImage = 0x0B3A5000,
            RootRegistryRva = 0x0A81DD38,
            SlotIndexRva = 0x0AB04B9C,
            SubscriptionVtableRva = 0x07085C18,
            CallbackVtableRva = 0x066B9BF8,
            CallbackInterfaceVtableRva = 0x066B9C38,
            LambdaVtableRva = 0x06FE9D00,
            HandlerRva = 0x04A208A0,
            OwnerVtableRva = 0x06FE8E80,
            BooleanTriggerSetterRva = 0x00798F70,
            PresentationServiceGlobalRva = 0x0A86F4F8,
            PresentationEventHubGlobalRva = 0x0A86F5A0,
            PresentationServiceVtableRva = 0x068C7418,
            PresentationEventHubVtableRva = 0x069FCB78,
            RenderSystemGlobalRva = 0x0A8D87D8
        },
        new()
        {
            Name = "Steam 6.440.853.0",
            Sha256 = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD",
            PeTimestamp = 0x6A995082,
            SizeOfImage = 0x0B3C4000,
            RootRegistryRva = 0x0A885A08,
            SlotIndexRva = 0x0AAFAFC8,
            SubscriptionVtableRva = 0x06CDC350,
            CallbackVtableRva = 0x06C1D3D8,
            CallbackInterfaceVtableRva = 0x06C1D418,
            LambdaVtableRva = 0x06F94E78,
            HandlerRva = 0x04A90D50,
            OwnerVtableRva = 0x06F93FF8,
            BooleanTriggerSetterRva = 0x02A8A1A0,
            RenderSystemGlobalRva = 0x0A8AF088,
            CarRenderModelWrapperVtableRva = 0x065ADCA8,
            CarModelInstanceVtableRva = 0x0645DA08,
            CarModelResourceVtableRva = 0x0645D878,
            LightEvaluatorRva = 0x02A9C1D0,
            LightVehicleVtableRva = 0x0648C718
        },
        new()
        {
            Name = "Steam 6.461.691.0",
            Sha256 = "B8C18EC88AB3143DA03C3BB9BD9D0761F23E00F809B1BD9D50CF07496E5D1CE5",
            PeTimestamp = 0x6ABA8552,
            SizeOfImage = 0x0B3C0000,
            RootRegistryRva = 0x0A882A18,
            SlotIndexRva = 0x0AAF8328,
            SubscriptionVtableRva = 0x06CF6260,
            CallbackVtableRva = 0x06C37318,
            CallbackInterfaceVtableRva = 0x06C37358,
            LambdaVtableRva = 0x06FAF328,
            HandlerRva = 0x04AAAD70,
            OwnerVtableRva = 0x06FAE4A8,
            BooleanTriggerSetterRva = 0x02A982B0,
            RenderSystemGlobalRva = 0x0A8AC0C8,
            CarRenderModelWrapperVtableRva = 0x065C6408,
            CarModelInstanceVtableRva = 0x06474E38,
            CarModelResourceVtableRva = 0x06474CA8,
            LightEvaluatorRva = 0x02AAA2E0,
            LightVehicleVtableRva = 0x064A3D38
        },
        new()
        {
            Name = "Microsoft Store 3.461.691.0",
            PeTimestamp = 0x6ABA856E,
            SizeOfImage = 0x0B3AD000,
            RootRegistryRva = null,
            SlotIndexRva = null,
            SubscriptionVtableRva = 0x06CE4520,
            CallbackVtableRva = 0x06C24018,
            CallbackInterfaceVtableRva = 0x06C24058,
            LambdaVtableRva = 0x06F9D9B8,
            HandlerRva = 0x04A3EEE0,
            OwnerVtableRva = 0x06F9CB38,
            BooleanTriggerSetterRva = 0x02A19D40,
            CarRenderModelWrapperVtableRva = 0x065B3608,
            CarModelInstanceVtableRva = 0x06461D78,
            CarModelResourceVtableRva = 0x06461BE8,
            RenderSystemGlobalRva = 0x0A8A4848,
            LightEvaluatorRva = 0x02A2BD70,
            LightVehicleVtableRva = 0x06490C18
        },
        new()
        {
            Name = "Microsoft Store 3.440.853.0",
            PeTimestamp = 0x6A995049,
            SizeOfImage = 0x0B3B1000,
            RootRegistryRva = null,
            RootRegistryHintRva = 0x0A7D7DF0,
            SlotIndexRva = null,
            SubscriptionVtableRva = 0x06CCB4D0,
            CallbackVtableRva = 0x06C0B048,
            CallbackInterfaceVtableRva = 0x06C0B088,
            LambdaVtableRva = 0x06F84198,
            HandlerRva = 0x04A26470,
            OwnerVtableRva = 0x06F83318,
            BooleanTriggerSetterRva = 0x02A0D190,
            RenderSystemGlobalRva = 0x0A8278F0,
            CarRenderModelWrapperVtableRva = 0x0659BB68,
            CarModelInstanceVtableRva = 0x0644B808,
            CarModelResourceVtableRva = 0x0644B678
        }
    ];

    internal static bool IsSupportedDigest(string? digest) =>
        digest is not null && All.Any(profile =>
            string.Equals(profile.Sha256, digest, StringComparison.OrdinalIgnoreCase));

    internal static GameBuildProfile MatchExecutable(string path, uint? peTimestamp = null, uint? sizeOfImage = null)
    {
        string? digest = null;
        try
        {
            digest = ReadExecutableDigest(path);
            var byHash = All.FirstOrDefault(profile => profile.Sha256 is not null &&
                profile.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
            if (byHash is not null)
            {
                if ((peTimestamp is not null && byHash.PeTimestamp != peTimestamp) ||
                    (sizeOfImage is not null && byHash.SizeOfImage != sizeOfImage))
                    throw new InvalidOperationException("running game module and executable fingerprint disagree");
                return byHash;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        var byMetadata = All.FirstOrDefault(profile =>
            profile.PeTimestamp == peTimestamp && profile.SizeOfImage == sizeOfImage);
        // A Steam profile with a pinned digest must never be admitted by PE
        // metadata alone after an update or a modified executable. The Store
        // profile has no readable disk digest and retains its metadata mapping.
        if (byMetadata?.Sha256 is not null)
            throw new InvalidOperationException(digest is null
                ? "game executable fingerprint could not be verified"
                : "unsupported game executable fingerprint");
        return byMetadata ?? throw new InvalidOperationException("unsupported game build");
    }

    private static string ReadExecutableDigest(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        try
        {
            // Opening the file on every lookup also prevents an unreadable or
            // deleted executable from inheriting a previous known fingerprint.
            // FileShare.Read excludes writers/replacement while this handle lives.
            using var file = File.OpenRead(canonicalPath);
            var before = ReadFileIdentity(file.SafeFileHandle);
            return Fingerprints.GetOrCompute(canonicalPath, before, () =>
            {
                var digest = Convert.ToHexString(SHA256.HashData(file));
                var after = ReadFileIdentity(file.SafeFileHandle);
                if (before is not null && before != after)
                    throw new IOException("game executable changed during fingerprint verification");
                return digest;
            });
        }
        catch
        {
            Fingerprints.Forget(canonicalPath);
            throw;
        }
    }

    private static ExecutableFileIdentity? ReadFileIdentity(SafeFileHandle handle)
    {
        // A handle identity closes the path/stat replacement race and adds
        // volume/file ID to length and UTC file times. If unavailable, full
        // hashing still works but no successful result is cached.
        if (!GetFileInformationByHandle(handle, out var info)) return null;
        var length = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (length > (ulong)long.MaxValue) return null;
        return new((long)length,
            ((ulong)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow,
            ((ulong)info.CreationTimeHigh << 32) | info.CreationTimeLow,
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow, CreationTimeHigh;
        internal uint LastAccessTimeLow, LastAccessTimeHigh;
        internal uint LastWriteTimeLow, LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh, FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh, FileIndexLow;
    }
}

// Separate pure cache policy permits fixtures without touching a game/file.
// UTC FILETIME values retain the full on-disk timestamp precision.
internal readonly record struct ExecutableFileIdentity(long Length, ulong LastWriteTimeUtc,
    ulong CreationTimeUtc, uint VolumeSerialNumber, ulong FileIndex);

internal sealed class KnownExecutableFingerprintCache(Func<string, bool> isKnownDigest, Func<long>? clock = null)
{
    private const int MaximumEntries = 4;
    private const long MaximumAgeMilliseconds = 5 * 60 * 1000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);

    internal string GetOrCompute(string canonicalPath, ExecutableFileIdentity? identity, Func<string> computeDigest)
    {
        lock (_gate)
        {
            var now = _clock();
            if (identity is not null && _entries.TryGetValue(canonicalPath, out var cached) &&
                cached.Identity == identity && now >= cached.VerifiedAt && now - cached.VerifiedAt < MaximumAgeMilliseconds)
                return cached.Digest;

            // Changed/unavailable metadata, expired entries and failed hashes
            // must not leave a stale positive entry behind.
            _entries.Remove(canonicalPath);
            var digest = computeDigest();
            if (identity is not null && isKnownDigest(digest))
            {
                if (_entries.Count >= MaximumEntries) _entries.Remove(_entries.Keys.First());
                _entries[canonicalPath] = new(identity.Value, digest, _clock());
            }
            return digest;
        }
    }

    internal void Forget(string canonicalPath)
    {
        lock (_gate) _entries.Remove(canonicalPath);
    }

    private sealed record Entry(ExecutableFileIdentity Identity, string Digest, long VerifiedAt);
}
