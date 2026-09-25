using System.IO;
using System.Security.Cryptography;

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
}

internal static class GameBuildProfiles
{
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
            CarModelResourceVtableRva = 0x0645D878
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
        try
        {
            using var file = File.OpenRead(path);
            var digest = Convert.ToHexString(SHA256.HashData(file));
            var byHash = All.FirstOrDefault(profile => profile.Sha256 is not null &&
                profile.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
            if (byHash is not null) return byHash;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        var byMetadata = All.FirstOrDefault(profile =>
            profile.PeTimestamp == peTimestamp && profile.SizeOfImage == sizeOfImage);
        return byMetadata ?? throw new InvalidOperationException("unsupported game build");
    }
}
