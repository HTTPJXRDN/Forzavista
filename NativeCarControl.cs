using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal sealed record NativeControlStatus(bool Ready, bool PresentationActive, int? ProcessId,
    ulong? Vehicle, string Message);

internal static class NativeCarControl
{
    internal const string SupportedSha256 = "B62B5EC1933B2D11A6B80941AE0D2B38C4A5AAEFDD880E487453D178081D7B44";

    private const uint ReadAccess = 0x0010 | 0x0400;
    private const uint ActionAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageExecuteReadWrite = 0x40;
    private const ulong RootRegistryRva = 0x0A81DD38, SlotIndexRva = 0x0AB04B9C;
    private const ulong SubscriptionVtableRva = 0x07085C18;
    private const ulong CallbackVtableRva = 0x066B9BF8, CallbackInterfaceVtableRva = 0x066B9C38;
    private const ulong LambdaVtableRva = 0x06FE9D00, HandlerRva = 0x04A208A0, OwnerVtableRva = 0x06FE8E80;
    private const ulong BooleanTriggerSetterRva = 0x00798F70;
    private const ulong PresentationServiceGlobalRva = 0x0A86F4F8;
    private const ulong PresentationEventHubGlobalRva = 0x0A86F5A0;
    private const ulong PresentationServiceVtableRva = 0x068C7418;
    private const ulong PresentationEventHubVtableRva = 0x069FCB78;
    private const int ConvertibleCommand = 0x10E;
    private const ulong RenderSystemGlobalRva = 0x0A8D87D8;
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
        var service = ReadUInt64(context.Handle, context.Module + PresentationServiceGlobalRva);
        var eventHub = ReadUInt64(context.Handle, context.Module + PresentationEventHubGlobalRva);
        if (service == 0 && eventHub == 0) return "inactive";
        if (service < 0x10000 || eventHub < 0x10000)
            throw new InvalidOperationException("presentation state is incomplete");
        if (ReadUInt64(context.Handle, service) != context.Module + PresentationServiceVtableRva ||
            ReadUInt64(context.Handle, eventHub) != context.Module + PresentationEventHubVtableRva)
            throw new InvalidOperationException("presentation object identity mismatch");
        return "active";
    }

    internal static string ToggleRoof()
    {
        using var context = Locate(action: true);
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x30];
        if (!Read(context.Handle, context.Module + HandlerRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native roof-handler signature mismatch.");

        var remote = (ulong)VirtualAllocEx(context.Handle, 0, 0x1000, MemCommitReserve, PageExecuteReadWrite);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
        try
        {
            var code = BuildCall(context.CallbackOwner, context.Module + HandlerRva);
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
        if (!Read(context.Handle, context.Module + BooleanTriggerSetterRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");

        DispatchBooleanTrigger(context.Handle, component, context.Module + BooleanTriggerSetterRva,
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
        if (!Read(context.Handle, context.Module + BooleanTriggerSetterRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");
        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + BooleanTriggerSetterRva, Fnv1a(eventName), true);
        // CinematicCar event delivery asserts the converted key for one update
        // and then rolls it back. Leaving both *_open and *_close latched true
        // makes the animation graph fight itself, so reproduce that pulse.
        Thread.Sleep(50);
        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + BooleanTriggerSetterRva, Fnv1a(eventName), false);
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
        var (handle, module) = OpenGameModule(action: false);
        using (handle)
        {
            var controller = ResolveRenderController(handle, module);
            return ReadInt32(handle, controller + 0x2B0);
        }
    }

    internal static string SetRenderMode(int mode)
    {
        var (handle, module) = OpenGameModule(action: true);
        using (handle)
        {
            var controller = ResolveRenderController(handle, module);
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
    private static (SafeProcessHandle Handle, ulong Module) OpenGameModule(bool action)
    {
        var game = TryGetGame() ?? throw new InvalidOperationException("game not running");
        try
        {
            var moduleInfo = game.MainModule ?? throw new InvalidOperationException("game module unavailable");
            using (var file = File.OpenRead(moduleInfo.FileName))
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(SupportedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unsupported game build");
            var handle = OpenProcess(action ? ActionAccess : ReadAccess, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            return (handle, (ulong)moduleInfo.BaseAddress);
        }
        finally { game.Dispose(); }
    }

    private static ulong ResolveRenderController(SafeProcessHandle handle, ulong module)
    {
        var global = ReadUInt64(handle, module + RenderSystemGlobalRva);
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
            var moduleInfo = game.MainModule ?? throw new InvalidOperationException("game module unavailable");
            using (var file = File.OpenRead(moduleInfo.FileName))
            {
                var digest = Convert.ToHexString(SHA256.HashData(file));
                if (!digest.Equals(SupportedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unsupported game build");
            }

            var handle = OpenProcess(action ? ActionAccess : ReadAccess, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            try
            {
                var module = (ulong)moduleInfo.BaseAddress;
                var root = ReadUInt64(handle, module + RootRegistryRva);
                var table = ReadUInt64(handle, root + 8);
                var slotIndex = ReadInt32(handle, module + SlotIndexRva);
                if (root < 0x10000 || table < 0x10000 || slotIndex is < 0 or > 1024)
                    throw new InvalidOperationException("native service root unavailable");
                var slots = ReadUInt64(handle, table + 0xE8);
                var owner = ReadUInt64(handle, slots + (ulong)slotIndex * 0x10);
                if (slots < 0x10000 || owner < 0x10000)
                    throw new InvalidOperationException("native input service unavailable");

                var begin = ReadUInt64(handle, owner + 0xD8);
                var end = ReadUInt64(handle, owner + 0xE0);
                var capacity = ReadUInt64(handle, owner + 0xE8);
                if (begin < 0x10000 || end < begin || capacity < end || ((end - begin) & 7) != 0 || end - begin > 0x8000)
                    throw new InvalidOperationException("native command list unavailable");

                var count = checked((int)((end - begin) / 8));
                for (var i = 0; i < count; i++)
                {
                    var subscription = ReadUInt64(handle, begin + (ulong)i * 8);
                    if (subscription < 0x10000) continue;
                    byte[] data;
                    try { data = Read(handle, subscription, 0xB0); }
                    catch { continue; }
                    if (BitConverter.ToUInt64(data, 0) != module + SubscriptionVtableRva ||
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
                            if (BitConverter.ToUInt64(wrapper, 0) != module + CallbackVtableRva ||
                                BitConverter.ToUInt64(wrapper, 0x20) != module + CallbackInterfaceVtableRva ||
                                BitConverter.ToUInt64(wrapper, 0x28) != module + LambdaVtableRva ||
                                BitConverter.ToUInt64(wrapper, 0x30) != module + HandlerRva ||
                                BitConverter.ToUInt64(wrapper, 0x60) != candidate + 0x28) continue;
                            var callbackOwner = BitConverter.ToUInt64(wrapper, 0x40);
                            var ownerBytes = Read(handle, callbackOwner, 0x50);
                            var ownerLink = BitConverter.ToUInt64(data, 0xA8);
                            if (ownerLink < 0x10000 || BitConverter.ToUInt64(ownerBytes, 0) != module + OwnerVtableRva ||
                                BitConverter.ToUInt64(ownerBytes, 0x38) != ReadUInt64(handle, ownerLink) ||
                                BitConverter.ToUInt64(ownerBytes, 0x40) < 0x10000) continue;
                            var carController = BitConverter.ToUInt64(ownerBytes, 0x40);
                            return new NativeContext(game.Id, module, callbackOwner, carController, handle);
                        }
                        catch { }
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

    private static Process? TryGetGame() => Process.GetProcessesByName("forzahorizon6").OrderByDescending(p => p.StartTime).FirstOrDefault();

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

    private sealed record NativeContext(int ProcessId, ulong Module, ulong CallbackOwner, ulong CarController, SafeProcessHandle Handle) : IDisposable
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
