using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal sealed record NativeControlStatus(bool Ready, bool PresentationActive, int? ProcessId,
    ulong? Vehicle, string Message);

internal static class NativeCarControl
{
    private static readonly string[] GameProcessNames = ["forzahorizon6"];

    internal static bool IsGameProcessName(string processName) =>
        GameProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    internal static bool IsSupportedDigest(string? digest) => GameBuildProfiles.IsSupportedDigest(digest);

    internal static string? GetCurrentBuildName()
    {
        using var game = TryGetGame();
        if (game is null || !game.Responding) return null;
        try
        {
            var moduleInfo = game.MainModule;
            if (moduleInfo is null) return null;
            using var handle = OpenProcess(ReadAccess, false, game.Id);
            if (handle.IsInvalid) return null;
            var identity = ReadModuleIdentity(handle, (ulong)moduleInfo.BaseAddress);
            return GameBuildProfiles.MatchExecutable(moduleInfo.FileName,
                identity.PeTimestamp, identity.SizeOfImage).Name;
        }
        catch { return null; }
    }

    private const uint ReadAccess = 0x0010 | 0x0400;
    private const uint ActionAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageExecuteReadWrite = 0x40;
    private const int ConvertibleCommand = 0x10E;
    // Dynamic render-mode ids. Freeroam is the normal world mode. MaxDetail is the
    // mode the toggle switches to for full car detail (CarDrawAutovista=1).
    // ThreeTwoOne(3)/PreRace are the smallest scenarios known to carry it;
    // Homespace(8) is confirmed to force full car detail but swaps more scene.
    // Change MaxDetailRenderMode to retarget the toggle without other edits.
    internal const int FreeroamRenderMode = 9;
    // Homespace(8) is the mode confirmed (HANDOFF §109) to snap the car to full
    // Autovista/max detail. ThreeTwoOne(3) is a transient pre-race countdown
    // scenario and does not persist. Change here to retarget the toggle.
    internal const int MaxDetailRenderMode = 8;
    private static readonly IReadOnlyDictionary<string, (string Trigger, bool Value)> ActionTriggers =
        new Dictionary<string, (string Trigger, bool Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["opendoorLF"] = ("doorLF_open", true), ["closedoorLF"] = ("doorLF_open", false),
            ["opendoorRF"] = ("doorRF_open", true), ["closedoorRF"] = ("doorRF_open", false),
            ["opendoorLR"] = ("doorLR_open", true), ["closedoorLR"] = ("doorLR_open", false),
            ["opendoorRR"] = ("doorRR_open", true), ["closedoorRR"] = ("doorRR_open", false),
            ["openhood"] = ("hood_open", true), ["closehood"] = ("hood_open", false),
            ["opentrunk"] = ("trunk_open", true), ["closetrunk"] = ("trunk_open", false)
        };
    private static readonly IReadOnlyDictionary<string, string> FreeRoamEventTriggers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["opendoorLF"] = "doorLF_open", ["closedoorLF"] = "doorLF_close",
            ["opendoorRF"] = "doorRF_open", ["closedoorRF"] = "doorRF_close",
            ["opendoorLR"] = "doorLR_open", ["closedoorLR"] = "doorLR_close",
            ["opendoorRR"] = "doorRR_open", ["closedoorRR"] = "doorRR_close",
            ["openhood"] = "hood_open", ["closehood"] = "hood_close",
            ["opentrunk"] = "trunk_open", ["closetrunk"] = "trunk_close",
            ["openroof"] = "roof_open", ["closeroof"] = "roof_close",
            ["openstorage"] = "storage_open", ["closestorage"] = "storage_close",
            ["openaero"] = "wing_open", ["closeaero"] = "wing_close",
            ["openvents"] = "vent_open", ["closevents"] = "vent_close"
        };
    // This legacy managed path remains garage-scoped. The V8 internal proof now
    // provides a separate, visually verified free-roam path; wire that session
    // controller into the form before enabling these buttons in free roam.
    internal static IReadOnlyCollection<string> SupportedPanelActions { get; } = ActionTriggers.Keys.ToArray();
    internal static IReadOnlyCollection<string> SupportedFreeRoamPanelActions { get; } =
    [
        "opendoorLF", "closedoorLF",
        "opendoorRF", "closedoorRF",
        "opendoorLR", "closedoorLR",
        "opendoorRR", "closedoorRR",
        "openhood", "closehood",
        "opentrunk", "closetrunk"
    ];
    internal static NativeControlStatus GetStatus()
    {
        try
        {
            using var context = Locate(action: false);
            var target = ValidateAnimationTarget(context);
            var presentation = InspectPresentation(context);
            var presentationActive = presentation == "active";
            return new(true, presentationActive, context.ProcessId, target.Vehicle,
                presentationActive
                    ? "external free-roam panels ready; garage presentation also active"
                    : "external free-roam panel controls ready");
        }
        catch (Exception ex)
        {
            return new(false, false, TryGetGame()?.Id, null, ex.Message);
        }
    }

    private static string InspectPresentation(NativeContext context)
    {
        var profile = context.Profile;
        if (profile.PresentationServiceGlobalRva is not { } serviceGlobalRva ||
            profile.PresentationEventHubGlobalRva is not { } eventHubGlobalRva ||
            profile.PresentationServiceVtableRva is not { } serviceVtableRva ||
            profile.PresentationEventHubVtableRva is not { } eventHubVtableRva)
            return "unavailable";

        var service = ReadUInt64(context.Handle, context.Module + serviceGlobalRva);
        var eventHub = ReadUInt64(context.Handle, context.Module + eventHubGlobalRva);
        if (service == 0 && eventHub == 0) return "inactive";
        if (service < 0x10000 || eventHub < 0x10000)
            throw new InvalidOperationException("presentation state is incomplete");
        if (ReadUInt64(context.Handle, service) != context.Module + serviceVtableRva ||
            ReadUInt64(context.Handle, eventHub) != context.Module + eventHubVtableRva)
            throw new InvalidOperationException("presentation object identity mismatch");
        return "active";
    }

    internal static string ToggleRoof()
    {
        using var context = Locate(action: true);
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x30];
        if (!Read(context.Handle, context.Module + context.Profile.HandlerRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native roof-handler signature mismatch.");

        var remote = (ulong)VirtualAllocEx(context.Handle, 0, 0x1000, MemCommitReserve, PageExecuteReadWrite);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
        try
        {
            var code = BuildCall(context.CallbackOwner, context.Module + context.Profile.HandlerRva);
            Write(context.Handle, remote, code);
            FlushInstructionCache(context.Handle, (nuint)remote, (nuint)code.Length);
            using var thread = CreateRemoteThread(context.Handle, 0, 0, (nuint)remote, 0, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
            var wait = WaitForSingleObject(thread, 5000);
            if (wait != 0) throw new InvalidOperationException($"Native roof callback did not finish (wait 0x{wait:X}).");
            return "roof toggled";
        }
        finally
        {
            VirtualFreeEx(context.Handle, (nuint)remote, 0, MemRelease);
        }
    }

    internal static string TriggerPanel(string action)
    {
        if (!ActionTriggers.TryGetValue(action, out var command))
            throw new InvalidOperationException("Unsupported panel control.");

        using var context = Locate(action: true);
        if (InspectPresentation(context) != "active")
            throw new InvalidOperationException("panel controls require active garage presentation");
        var component = ValidateAnimationComponent(context);
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x56, 0x48];
        if (!Read(context.Handle, context.Module + context.Profile.BooleanTriggerSetterRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");

        DispatchBooleanTrigger(context.Handle, component, context.Module + context.Profile.BooleanTriggerSetterRva,
            Fnv1a(command.Trigger), command.Value);
        return $"{command.Trigger}={(command.Value ? "open" : "closed")} queued; visible movement may wait for a presentation refresh";
    }

    internal static string TriggerFreeRoamPanel(string action)
    {
        if (!FreeRoamEventTriggers.TryGetValue(action, out var eventName))
            throw new InvalidOperationException("Unsupported free-roam panel control.");

        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        var flagAddress = target.Vehicle + 0x82A3;
        var flag = Read(context.Handle, flagAddress, 1)[0];
        var opening = action.StartsWith("open", StringComparison.OrdinalIgnoreCase);
        if (flag > 1)
            throw new InvalidOperationException("vehicle Autovista flag is invalid");
        if (opening && flag == 0)
            Write(context.Handle, flagAddress, [1]);
        else if (!opening && flag == 0)
            throw new InvalidOperationException("free-roam panel presentation is not active");

        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x56, 0x48];
        if (!Read(context.Handle, context.Module + context.Profile.BooleanTriggerSetterRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");
        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + context.Profile.BooleanTriggerSetterRva, Fnv1a(eventName), true);
        // CinematicCar event delivery asserts the converted key for one update
        // and then rolls it back. Leaving both *_open and *_close latched true
        // makes the animation graph fight itself, so reproduce that pulse.
        Thread.Sleep(50);
        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + context.Profile.BooleanTriggerSetterRva, Fnv1a(eventName), false);
        return $"{eventName} pulsed externally on vehicle 0x{target.Vehicle:X}";
    }

    internal static string RestoreFreeRoamPresentationFlag()
    {
        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        var flagAddress = target.Vehicle + 0x82A3;
        var flag = Read(context.Handle, flagAddress, 1)[0];
        if (flag == 0) return "free-roam presentation already restored";
        if (flag != 1) throw new InvalidOperationException("vehicle Autovista flag is invalid");
        Write(context.Handle, flagAddress, [0]);
        if (Read(context.Handle, flagAddress, 1)[0] != 0)
            throw new InvalidOperationException("vehicle Autovista flag restore did not persist");
        return $"free-roam presentation restored on vehicle 0x{target.Vehicle:X}";
    }

    // ---- Max-detail render-mode toggle -------------------------------------
    // Reproduces the state update of dynamic-render-mode request routine
    // RVA 0x0287A340: publish option/previous/current, then set the pending byte
    // last so the game never observes a partial request. Read-only GetRenderMode
    // resolves the same controller. No game code is called.

    internal static int GetRenderMode()
    {
        var (handle, module, profile) = OpenGameModule(action: false);
        using (handle)
        {
            var controller = ResolveRenderController(handle, module, profile);
            return ReadInt32(handle, controller + 0x2B0);
        }
    }

    internal static string SetRenderMode(int mode)
    {
        var (handle, module, profile) = OpenGameModule(action: true);
        using (handle)
        {
            var controller = ResolveRenderController(handle, module, profile);
            var current = ReadInt32(handle, controller + 0x2B0);
            if (current == mode) return $"render mode already {mode}";
            Write(handle, controller + 0x2BC, BitConverter.GetBytes(0));
            Write(handle, controller + 0x2B4, BitConverter.GetBytes(current));
            Write(handle, controller + 0x2B0, BitConverter.GetBytes(mode));
            Write(handle, controller + 0x2B8, [1]);
            return $"render mode {current} -> {mode} requested";
        }
    }

    internal static string SetMaxDetail(bool on) =>
        SetRenderMode(on ? MaxDetailRenderMode : FreeroamRenderMode);

    // Opens the verified game process for module-relative work (render mode),
    // without the convertible-car locator, so it works on any car.
    private static (SafeProcessHandle Handle, ulong Module, GameBuildProfile Profile) OpenGameModule(bool action)
    {
        var game = TryGetGame() ?? throw new InvalidOperationException("game not running");
        try
        {
            if (!game.Responding) throw new InvalidOperationException("game is not responding");
            var moduleInfo = game.MainModule ?? throw new InvalidOperationException("game module unavailable");
            var handle = OpenProcess(action ? ActionAccess : ReadAccess, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            try
            {
                var module = (ulong)moduleInfo.BaseAddress;
                var identity = ReadModuleIdentity(handle, module);
                var profile = GameBuildProfiles.MatchExecutable(moduleInfo.FileName,
                    identity.PeTimestamp, identity.SizeOfImage);
                return (handle, module, profile);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { game.Dispose(); }
    }

    private static ulong ResolveRenderController(SafeProcessHandle handle, ulong module, GameBuildProfile profile)
    {
        if (profile.RenderSystemGlobalRva is not { } renderSystemGlobalRva)
            throw new InvalidOperationException("max-detail mode is not available for this game build yet");
        var global = ReadUInt64(handle, module + renderSystemGlobalRva);
        var renderSystem = ReadUInt64(handle, global + 0x1D8);
        var wantedType = ReadUInt32(handle, renderSystem + 0x18);
        var sentinel = ReadUInt64(handle, renderSystem + 0x08);
        var node = ReadUInt64(handle, sentinel);
        for (var visited = 0; node != 0 && node != sentinel && visited < 256; visited++)
        {
            var candidate = ReadUInt64(handle, node + 0x10);
            if (candidate != 0 && ReadUInt16(handle, candidate + 0xC0) == (ushort)wantedType)
                return candidate;
            node = ReadUInt64(handle, node);
        }
        throw new InvalidOperationException("dynamic render-mode controller not found");
    }

    private static ulong ValidateAnimationComponent(NativeContext context)
        => ValidateAnimationTarget(context).Component;

    private static AnimationTarget ValidateAnimationTarget(NativeContext context)
    {
        var sharedCar = ReadUInt64(context.Handle, context.CarController + 0xF0);
        var ownerBase = ReadUInt64(context.Handle, sharedCar + 0x28);
        var component = ReadUInt64(context.Handle, ownerBase + 0x7920);
        var runtime = ReadUInt64(context.Handle, component + 0x28);
        if (sharedCar < 0x10000 || ownerBase < 0x10000 || component < 0x10000 || runtime < 0x10000 ||
            Read(context.Handle, runtime + 0x31, 1)[0] == 0 || ReadInt32(context.Handle, runtime + 0x210) == 0)
            throw new InvalidOperationException("current-car animation controls not ready");
        return new(ownerBase, component);
    }

    private static NativeContext Locate(bool action)
    {
        var game = TryGetGame() ?? throw new InvalidOperationException("game not running");
        try
        {
            if (!game.Responding) throw new InvalidOperationException("game is not responding");
            var moduleInfo = game.MainModule ?? throw new InvalidOperationException("game module unavailable");
            var handle = OpenProcess(action ? ActionAccess : ReadAccess, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            try
            {
                var module = (ulong)moduleInfo.BaseAddress;
                var identity = ReadModuleIdentity(handle, module);
                var profile = GameBuildProfiles.MatchExecutable(moduleInfo.FileName,
                    identity.PeTimestamp, identity.SizeOfImage);
                if (profile.RootRegistryRva is null)
                    return LocateDynamic(game.Id, handle, module, identity.SizeOfImage, profile);
                var root = ReadUInt64(handle, module + profile.RootRegistryRva!.Value);
                var table = root >= 0x10000 ? ReadUInt64(handle, root + 8) : 0;
                if (root < 0x10000 || table < 0x10000)
                    throw new InvalidOperationException("native service root unavailable");
                var slots = ReadUInt64(handle, table + 0xE8);
                if (slots < 0x10000)
                    throw new InvalidOperationException("native input service unavailable");

                // Steam currently exposes the active slot as a global.  The
                // Microsoft Store build keeps the value in runtime-owned
                // state, so scan the bounded 0x400-entry table and select the
                // entry whose subscription chain matches the verified wrapper.
                IEnumerable<int> slotIndices;
                if (profile.SlotIndexRva is { } slotIndexRva)
                {
                    var slotIndex = ReadInt32(handle, module + slotIndexRva);
                    if (slotIndex is < 0 or > 1024)
                        throw new InvalidOperationException("native service slot unavailable");
                    slotIndices = [slotIndex];
                }
                else
                {
                    slotIndices = Enumerable.Range(0, 0x400);
                }

                foreach (var slotIndex in slotIndices)
                {
                    var owner = ReadUInt64(handle, slots + (ulong)slotIndex * 0x10);
                    if (owner < 0x10000) continue;
                    var begin = ReadUInt64(handle, owner + 0xD8);
                    var end = ReadUInt64(handle, owner + 0xE0);
                    var capacity = ReadUInt64(handle, owner + 0xE8);
                    if (begin < 0x10000 || end < begin || capacity < end ||
                        ((end - begin) & 7) != 0 || end - begin > 0x8000) continue;

                    var count = checked((int)((end - begin) / 8));
                    for (var i = 0; i < count; i++)
                    {
                        var subscription = ReadUInt64(handle, begin + (ulong)i * 8);
                        if (subscription < 0x10000) continue;
                        byte[] data;
                        try { data = Read(handle, subscription, 0xB0); }
                        catch { continue; }
                        if (BitConverter.ToUInt64(data, 0) != module + profile.SubscriptionVtableRva ||
                            BitConverter.ToInt32(data, 0xA0) != ConvertibleCommand) continue;

                        var mappingCount = BitConverter.ToInt32(data, 0x44) & 0x3FF;
                        var mappingTable = BitConverter.ToUInt64(data, 0x38);
                        if (mappingCount is <= 0 or > 64 || mappingTable < 0x10000) continue;
                        var mappings = Read(handle, mappingTable, checked(mappingCount * 0x10));
                        for (var mappingIndex = 0; mappingIndex < mappingCount; mappingIndex++)
                        {
                            var candidate = BitConverter.ToUInt64(mappings, mappingIndex * 0x10 + 8);
                            if (candidate < 0x10000) continue;
                            try
                            {
                                var wrapper = Read(handle, candidate, 0x68);
                                if (BitConverter.ToUInt64(wrapper, 0) != module + profile.CallbackVtableRva ||
                                    BitConverter.ToUInt64(wrapper, 0x20) != module + profile.CallbackInterfaceVtableRva ||
                                    BitConverter.ToUInt64(wrapper, 0x28) != module + profile.LambdaVtableRva ||
                                    BitConverter.ToUInt64(wrapper, 0x30) != module + profile.HandlerRva ||
                                    BitConverter.ToUInt64(wrapper, 0x60) != candidate + 0x28) continue;
                                var callbackOwner = BitConverter.ToUInt64(wrapper, 0x40);
                                var ownerBytes = Read(handle, callbackOwner, 0x50);
                                var ownerLink = BitConverter.ToUInt64(data, 0xA8);
                                var ownerIdentity = BitConverter.ToUInt64(ownerBytes, 0);
                                // The package build changed the subscription's
                                // owner-link layout.  Its wrapper/car identity
                                // is still verified, but do not dereference the
                                // legacy link field when live-slot discovery is
                                // active (it is not a pointer in that build).
                                if (ownerIdentity != module + profile.OwnerVtableRva ||
                                    (profile.SlotIndexRva is not null &&
                                     (ownerLink < 0x10000 || BitConverter.ToUInt64(ownerBytes, 0x38) != ReadUInt64(handle, ownerLink))) ||
                                    BitConverter.ToUInt64(ownerBytes, 0x40) < 0x10000) continue;
                                var carController = BitConverter.ToUInt64(ownerBytes, 0x40);
                                return new NativeContext(game.Id, module, callbackOwner, carController, profile, handle);
                            }
                            catch { }
                        }
                    }
                }
                throw new InvalidOperationException("supported convertible not active");
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            game.Dispose();
        }
    }

    private static Process? TryGetGame() => GameProcessNames
        .SelectMany(Process.GetProcessesByName)
        .OrderByDescending(p => p.StartTime)
        .FirstOrDefault();

    private static NativeContext LocateDynamic(int processId, SafeProcessHandle handle,
        ulong module, uint sizeOfImage, GameBuildProfile profile)
    {
        var header = Read(handle, module, 0x1000);
        var peOffset = BitConverter.ToInt32(header, 0x3C);
        var sectionCount = BitConverter.ToUInt16(header, peOffset + 6);
        var optionalSize = BitConverter.ToUInt16(header, peOffset + 20);
        var sectionOffset = peOffset + 24 + optionalSize;
        uint dataRva = 0, dataSize = 0;
        for (var index = 0; index < sectionCount; index++)
        {
            var offset = sectionOffset + index * 40;
            var name = System.Text.Encoding.ASCII.GetString(header, offset, 8).TrimEnd('\0');
            if (!name.Equals(".data", StringComparison.Ordinal)) continue;
            dataSize = BitConverter.ToUInt32(header, offset + 8);
            dataRva = BitConverter.ToUInt32(header, offset + 12);
            break;
        }
        if (dataRva == 0 || dataSize == 0)
            throw new InvalidOperationException("native service data section unavailable");

        var data = Read(handle, module + dataRva, checked((int)dataSize));
        var pages = new Dictionary<ulong, byte[]>();
        var moduleEnd = module + sizeOfImage;
        bool TryReadU64(ulong address, out ulong value)
        {
            value = 0;
            var page = address & ~0xFFFUL;
            var pageOffset = checked((int)(address - page));
            if (pageOffset > 0xFF8) return false;
            if (!pages.TryGetValue(page, out var pageBytes))
            {
                if (pages.Count >= 32768) return false;
                try { pageBytes = Read(handle, page, 0x1000); }
                catch { return false; }
                pages[page] = pageBytes;
            }
            value = BitConverter.ToUInt64(pageBytes, pageOffset);
            return true;
        }
        static bool HeapPointer(ulong value) => value is >= 0x10000 and < 0x0000800000000000 && (value & 7) == 0;
        bool InModule(ulong value) => value >= module && value < moduleEnd;

        IEnumerable<int> rootOffsets = Enumerable.Range(0, (data.Length - 8) / 8 + 1)
            .Select(index => index * 8);
        if (profile.RootRegistryHintRva is { } hintRva && hintRva >= dataRva &&
            hintRva <= dataRva + (uint)data.Length - 8 && ((hintRva - dataRva) & 7) == 0)
        {
            var hintOffset = checked((int)(hintRva - dataRva));
            rootOffsets = new[] { hintOffset }.Concat(rootOffsets.Where(offset => offset != hintOffset));
        }

        foreach (var offset in rootOffsets)
        {
            var root = BitConverter.ToUInt64(data, offset);
            if (!HeapPointer(root) || !TryReadU64(root + 8, out var table) || !HeapPointer(table) ||
                !TryReadU64(table + 0xE8, out var slots) || !HeapPointer(slots)) continue;

            for (var slotIndex = 0; slotIndex < 0x400; slotIndex++)
            {
                if (!TryReadU64(slots + (ulong)slotIndex * 0x10, out var owner) || !HeapPointer(owner)) continue;
                if (!TryReadU64(owner + 0xD8, out var begin) || !TryReadU64(owner + 0xE0, out var end) ||
                    !TryReadU64(owner + 0xE8, out var capacity) || !HeapPointer(begin) || end < begin ||
                    capacity < end || ((end - begin) & 7) != 0 || end - begin is 0 or > 0x8000) continue;

                var count = checked((int)((end - begin) / 8));
                for (var item = 0; item < count; item++)
                {
                    if (!TryReadU64(begin + (ulong)item * 8, out var subscription) || !HeapPointer(subscription)) continue;
                    byte[] subscriptionData;
                    try { subscriptionData = Read(handle, subscription, 0xB0); }
                    catch { continue; }
                    if (BitConverter.ToUInt64(subscriptionData, 0) != module + profile.SubscriptionVtableRva ||
                        BitConverter.ToInt32(subscriptionData, 0xA0) != ConvertibleCommand) continue;
                    var mappingCount = BitConverter.ToInt32(subscriptionData, 0x44) & 0x3FF;
                    var mappingTable = BitConverter.ToUInt64(subscriptionData, 0x38);
                    if (mappingCount is <= 0 or > 64 || !HeapPointer(mappingTable)) continue;
                    byte[] mappings;
                    try { mappings = Read(handle, mappingTable, checked(mappingCount * 0x10)); }
                    catch { continue; }
                    for (var mappingIndex = 0; mappingIndex < mappingCount; mappingIndex++)
                    {
                        var candidate = BitConverter.ToUInt64(mappings, mappingIndex * 0x10 + 8);
                        if (!HeapPointer(candidate)) continue;
                        byte[] wrapper;
                        try { wrapper = Read(handle, candidate, 0x68); }
                        catch { continue; }
                        if (!InModule(BitConverter.ToUInt64(wrapper, 0)) ||
                            !InModule(BitConverter.ToUInt64(wrapper, 0x20)) ||
                            !InModule(BitConverter.ToUInt64(wrapper, 0x28)) ||
                            BitConverter.ToUInt64(wrapper, 0x30) != module + profile.HandlerRva ||
                            BitConverter.ToUInt64(wrapper, 0x60) != candidate + 0x28) continue;
                        var callbackOwner = BitConverter.ToUInt64(wrapper, 0x40);
                        if (!HeapPointer(callbackOwner)) continue;
                        byte[] ownerBytes;
                        try { ownerBytes = Read(handle, callbackOwner, 0x50); }
                        catch { continue; }
                        if (BitConverter.ToUInt64(ownerBytes, 0) != module + profile.OwnerVtableRva) continue;
                        var carController = BitConverter.ToUInt64(ownerBytes, 0x40);
                        if (!HeapPointer(carController)) continue;
                        return new NativeContext(processId, module, callbackOwner, carController, profile, handle);
                    }
                }
            }
        }
        throw new InvalidOperationException("supported convertible not active");
    }

    private static byte[] BuildCall(ulong owner, ulong handler)
    {
        var code = new List<byte>();
        code.AddRange([0x48, 0x83, 0xEC, 0x28]);
        code.AddRange([0x48, 0xB9]); code.AddRange(BitConverter.GetBytes(owner));
        code.AddRange([0x48, 0xB8]); code.AddRange(BitConverter.GetBytes(handler));
        code.AddRange([0xFF, 0xD0, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x48, 0x83, 0xC4, 0x28, 0xC3]);
        return code.ToArray();
    }

    private static uint Fnv1a(string value)
    {
        var hash = 0x811C9DC5u;
        foreach (var character in value)
        {
            hash ^= checked((byte)character);
            hash = unchecked(hash * 0x01000193u);
        }
        return hash;
    }

    private sealed record ModuleIdentity(uint PeTimestamp, uint SizeOfImage);

    // WindowsApps packages commonly deny direct file reads even when the game
    // process itself is readable.  Read the two stable PE identity fields from
    // the mapped image so metadata-matched profiles work for both packages.
    private static ModuleIdentity ReadModuleIdentity(SafeProcessHandle process, ulong module)
    {
        var header = Read(process, module, 0x1000);
        var peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset < 0x40 || peOffset + 0x60 > header.Length ||
            header[peOffset] != (byte)'P' || header[peOffset + 1] != (byte)'E' ||
            header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
            throw new InvalidOperationException("game PE header unavailable");
        var timestamp = BitConverter.ToUInt32(header, peOffset + 8);
        var sizeOfImage = BitConverter.ToUInt32(header, peOffset + 24 + 56);
        if (sizeOfImage == 0) throw new InvalidOperationException("game PE image size unavailable");
        return new(timestamp, sizeOfImage);
    }

    private static void DispatchBooleanTrigger(SafeProcessHandle process, ulong component, ulong setter, uint hash, bool value)
    {
        var remote = (ulong)VirtualAllocEx(process, 0, 0x1000, MemCommitReserve, PageExecuteReadWrite);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
        try
        {
            var code = new List<byte>();
            code.AddRange([0x48, 0x83, 0xEC, 0x28]);
            code.AddRange([0x48, 0xB9]); code.AddRange(BitConverter.GetBytes(component));
            code.AddRange([0x48, 0xBA]); code.AddRange(BitConverter.GetBytes(remote + 0x100));
            code.AddRange([0x41, 0xB8]); code.AddRange(BitConverter.GetBytes(value ? 1u : 0u));
            code.AddRange([0x48, 0xB8]); code.AddRange(BitConverter.GetBytes(setter));
            code.AddRange([0xFF, 0xD0, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x48, 0x83, 0xC4, 0x28, 0xC3]);
            Write(process, remote, code.ToArray());
            Write(process, remote + 0x100, BitConverter.GetBytes(hash));
            FlushInstructionCache(process, (nuint)remote, (nuint)code.Count);
            using var thread = CreateRemoteThread(process, 0, 0, (nuint)remote, 0, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
            var wait = WaitForSingleObject(thread, 5000);
            if (wait != 0) throw new InvalidOperationException($"Native panel trigger did not finish (wait 0x{wait:X}).");
        }
        finally
        {
            VirtualFreeEx(process, (nuint)remote, 0, MemRelease);
        }
    }

    private static int ReadInt32(SafeProcessHandle process, ulong address) => BitConverter.ToInt32(Read(process, address, 4));
    private static uint ReadUInt32(SafeProcessHandle process, ulong address) => BitConverter.ToUInt32(Read(process, address, 4));
    private static ushort ReadUInt16(SafeProcessHandle process, ulong address) => BitConverter.ToUInt16(Read(process, address, 2));
    private static ulong ReadUInt64(SafeProcessHandle process, ulong address) => BitConverter.ToUInt64(Read(process, address, 8));
    private static byte[] Read(SafeProcessHandle process, ulong address, int count)
    {
        var data = new byte[count];
        if (!ReadProcessMemory(process, (nuint)address, data, (nuint)count, out var read) || read != (nuint)count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory 0x{address:X}");
        return data;
    }

    private static void Write(SafeProcessHandle process, ulong address, byte[] data)
    {
        if (!WriteProcessMemory(process, (nuint)address, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory 0x{address:X}");
    }

    private sealed record NativeContext(int ProcessId, ulong Module, ulong CallbackOwner, ulong CarController,
        GameBuildProfile Profile, SafeProcessHandle Handle) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private sealed record AnimationTarget(ulong Vehicle, ulong Component);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nuint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stackSize, nuint startAddress, nint parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(SafeProcessHandle process, nuint address, nuint size);
}
