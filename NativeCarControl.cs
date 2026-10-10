using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal sealed record NativeControlStatus(bool Ready, bool PresentationActive, int? ProcessId,
    ulong? Vehicle, ulong? Component, string? CarToken, bool WindowControlsMapped, string Message);

internal static partial class NativeCarControl
{
    // Free Roam window controls are available only on builds with mapped render wrappers.
    // An explicit opt-out is retained for cautious test sessions.
    internal static readonly bool WindowControlsEnabled =
        Environment.GetEnvironmentVariable("FORZAVISTA_DISABLE_WINDOWS") != "1";
    private static readonly string[] GameProcessNames = ["forzahorizon6"];
    private static DynamicContextHint? _dynamicContextHint;
    private static WindowWrapperCache? _windowWrapperCache;
    private static PresentationOwnership? _presentationOwnership;

    internal static void InvalidateWindowCache() => _windowWrapperCache = null;

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
        "opentrunk", "closetrunk",
        "openaero", "closeaero",
        "openheadlights", "closeheadlights"
    ];
    internal static IReadOnlyCollection<string> SupportedWindowActions { get; } =
    [
        "openwindowLF", "closewindowLF",
        "openwindowRF", "closewindowRF",
        "openwindowLR", "closewindowLR",
        "openwindowRR", "closewindowRR"
    ];

    internal static string SetWindowOpen(string action)
    {
        if (!WindowControlsEnabled)
            throw new InvalidOperationException("Window controls were disabled for this session.");
        if (!SupportedWindowActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsupported window control.");

        var opening = action.StartsWith("open", StringComparison.OrdinalIgnoreCase);
        var requested = action[^2..].ToUpperInvariant();
        using var context = Locate(action: true);
        if (!HasWindowMapping(context.Profile))
            throw new InvalidOperationException("Window controls are not mapped for this game build.");
        var target = ValidateAnimationTarget(context);
        var wrappers = ResolveWindowWrappers(context, target);
        var current = ValidateAnimationTarget(context);
        if (current.Vehicle != target.Vehicle || current.Component != target.Component)
            throw new InvalidOperationException("Current car changed during window discovery; try again.");
        var actualStem = requested switch
        {
            "LF" => "glassLF",
            "RF" => "glassRF",
            "LR" => wrappers.Any(item => WindowStem(item.Path).Equals("glassLM", StringComparison.OrdinalIgnoreCase))
                ? "glassLM" : "glassLR",
            "RR" => wrappers.Any(item => WindowStem(item.Path).Equals("glassRM", StringComparison.OrdinalIgnoreCase))
                ? "glassRM" : "glassRR",
            _ => throw new InvalidOperationException("Unknown window position.")
        };
        var selected = wrappers.Where(item =>
            WindowStem(item.Path).Equals(actualStem, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException($"{requested} has no separately rendered side-window layers on this car.");

        foreach (var item in selected)
        {
            current = ValidateAnimationTarget(context);
            if (current.Vehicle != target.Vehicle || current.Component != target.Component)
                throw new InvalidOperationException("Current car changed before window update completed.");
            ValidateWindowPlayerOwner(context, target, item.Pose);
            var vector = ValidateWindowVector(context, item);
            var wantedEnd = opening ? vector.Begin : vector.Capacity;
            if (vector.End == wantedEnd) continue;
            Write(context.Handle, item.Wrapper + 0x308, BitConverter.GetBytes(wantedEnd));
            ValidateWindowPlayerOwner(context, target, item.Pose);
            var verified = ValidateWindowVector(context, item);
            if (verified.End != wantedEnd)
                throw new InvalidOperationException($"Window render state did not persist for {item.Path}.");
        }
        return $"{requested} window {(opening ? "opened" : "closed")} on {selected[0].CarToken} " +
            $"({selected.Length} glass layer{(selected.Length == 1 ? "" : "s")})";
    }

    internal static string[] GetAvailableWindowPositions()
    {
        if (!WindowControlsEnabled) return [];
        using var context = Locate(action: false);
        if (!HasWindowMapping(context.Profile)) return [];
        var target = ValidateAnimationTarget(context);
        WindowWrapper[] wrappers;
        try { wrappers = ResolveWindowWrappers(context, target); }
        catch (InvalidOperationException ex) when (ex.Message == "current-car window render wrappers were not found")
        {
            return [];
        }
        var stems = wrappers.Select(item => WindowStem(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positions = new List<string>(4);
        if (stems.Contains("glassLF")) positions.Add("LF");
        if (stems.Contains("glassRF")) positions.Add("RF");
        if (stems.Contains("glassLM") || stems.Contains("glassLR")) positions.Add("LR");
        if (stems.Contains("glassRM") || stems.Contains("glassRR")) positions.Add("RR");
        return positions.ToArray();
    }

    internal static string RestoreTrackedWindows()
    {
        var cache = _windowWrapperCache;
        if (cache is null) return "no tracked windows";
        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        if (cache.ProcessId != context.ProcessId || cache.Vehicle != target.Vehicle ||
            cache.Component != target.Component)
        {
            _windowWrapperCache = null;
            return "tracked car changed";
        }
        ValidateWindowPlayerOwner(context, target, cache.Pose);
        var restored = 0;
        foreach (var item in cache.Wrappers)
        {
            var current = ValidateAnimationTarget(context);
            if (current.Vehicle != target.Vehicle || current.Component != target.Component)
                throw new InvalidOperationException("Current car changed during window restoration.");
            ValidateWindowPlayerOwner(context, target, cache.Pose);
            var vector = ValidateWindowVector(context, item);
            if (vector.End == vector.Capacity) continue;
            Write(context.Handle, item.Wrapper + 0x308, BitConverter.GetBytes(vector.Capacity));
            ValidateWindowPlayerOwner(context, target, cache.Pose);
            if (ValidateWindowVector(context, item).End != vector.Capacity)
                throw new InvalidOperationException($"Window restoration did not persist for {item.Path}.");
            restored++;
        }
        return restored == 0 ? "windows already restored" : $"restored {restored} window layers";
    }

    private static WindowWrapper[] ResolveWindowWrappers(NativeContext context, AnimationTarget target)
    {
        var currentCarToken = TryGetCurrentCarToken(context, target);
        if (currentCarToken is null)
            throw new InvalidOperationException("current-car window identity could not be verified; no windows changed");
        // Updated Steam and Xbox builds have a verified player -> Scene -> Pose chain.
        // Matching that exact pose excludes pooled and preview copies of this car.
        ChargerRenderOwner? renderOwner = null;
        if (DrlBuildMapping.ForProfile(context.Profile) is { } colorBuild)
            renderOwner = ChargerRenderOwner.Capture((address, count) => Read(context.Handle, address, count), () =>
            {
                var current = ValidateAnimationTarget(context);
                return current.Vehicle == target.Vehicle && current.Component == target.Component &&
                    string.Equals(TryGetCurrentCarToken(context, current), currentCarToken, StringComparison.OrdinalIgnoreCase);
            }, context.Module, target.Vehicle, target.Component, colorBuild);
        var cached = _windowWrapperCache;
        if (cached is not null && cached.ProcessId == context.ProcessId &&
            cached.Vehicle == target.Vehicle && cached.Component == target.Component &&
            cached.CarToken.Equals(currentCarToken, StringComparison.OrdinalIgnoreCase) &&
            cached.Pose == renderOwner?.Pose &&
            (HasAllWindowPositions(cached.Wrappers) || Environment.TickCount64 - cached.CapturedAt < 5000) &&
            cached.Wrappers.All(item => HasUsableWindowVector(context, item)))
            return cached.Wrappers;
        _windowWrapperCache = null;

        var profile = context.Profile;
        if (profile.CarRenderModelWrapperVtableRva is not { } wrapperVtableRva ||
            profile.CarModelInstanceVtableRva is not { } instanceVtableRva ||
            profile.CarModelResourceVtableRva is not { } modelVtableRva)
            throw new InvalidOperationException("window controls are not mapped for this game build yet");

        // A car's render wrappers are not guaranteed to occupy one 64 MB
        // allocator segment. After a car swap the DBX's eight side-window
        // layers were observed across more than 100 MB of committed memory.
        const ulong arenaSize = 0x10000000;
        const ulong radius = arenaSize / 2;
        const int chunkSize = 0x10000;
        var aligned = target.Component & ~0xFFFFUL;
        if (aligned < radius) throw new InvalidOperationException("current-car render arena is unavailable");
        var start = aligned - radius;
        var wrapperVtable = context.Module + wrapperVtableRva;
        var instanceVtable = context.Module + instanceVtableRva;
        var modelVtable = context.Module + modelVtableRva;
        var matches = new List<WindowWrapper>();
        var clock = Stopwatch.StartNew();
        var stop = start + arenaSize;
        var chunk = new byte[chunkSize];
        // Query committed regions first. Failed ReadProcessMemory calls must not
        // construct thousands of exceptions and keep the menu action gate busy.
        for (var cursor = start; cursor < stop;)
        {
            CheckWindowDiscoveryBudget(clock);
            if (QueryWindowMemory(context.Handle, (nuint)cursor, out var region, (nuint)Marshal.SizeOf<WindowMemoryRegion>()) != (nuint)Marshal.SizeOf<WindowMemoryRegion>() ||
                region.Size == 0 || region.Base > cursor || region.Size > ulong.MaxValue - region.Base)
                throw new InvalidOperationException("current-car window memory regions could not be inspected; no windows changed");
            var regionEnd = Math.Min(stop, region.Base + region.Size);
            if (regionEnd <= cursor)
                throw new InvalidOperationException("current-car window memory regions changed; no windows changed");
            var readable = region.State == 0x1000 && (region.Protection & 0x100) == 0 &&
                (region.Protection & 0xFF) is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
            if (readable)
            for (var address = cursor; address < regionEnd; address += (ulong)chunkSize)
            {
                CheckWindowDiscoveryBudget(clock);
                var length = (int)Math.Min((ulong)chunkSize, regionEnd - address);
                if (!ReadProcessMemory(context.Handle, (nuint)address, chunk, (nuint)length, out var actual) || actual != (nuint)length)
                    continue;
                for (var index = 0; index <= length - 8; index += 8)
                {
                    if (BitConverter.ToUInt64(chunk, index) != wrapperVtable) continue;
                    CheckWindowDiscoveryBudget(clock);
                    try
                    {
                        var wrapper = address + (ulong)index;
                        var header = index <= length - 0x70 ? chunk.AsSpan(index, 0x70).ToArray()
                            : Read(context.Handle, wrapper, 0x70);
                        var instance = BitConverter.ToUInt64(header, 0x60);
                        if (instance < 0x10000 || BitConverter.ToUInt64(header, 0x68) != instance - 0x10) continue;
                        var instanceHeader = Read(context.Handle, instance, 0x40);
                        if (BitConverter.ToUInt64(instanceHeader) != instanceVtable ||
                            renderOwner is not null && BitConverter.ToUInt64(instanceHeader, 0x38) != renderOwner.Pose) continue;
                        var model = BitConverter.ToUInt64(instanceHeader, 0x20);
                        if (model < 0x10000) continue;
                        var modelHeader = Read(context.Handle, model, 0x88);
                        if (BitConverter.ToUInt64(modelHeader) != modelVtable) continue;
                        var pathAddress = BitConverter.ToUInt64(modelHeader, 0x80);
                        if (pathAddress < 0x10000) continue;
                        var pathBytes = Read(context.Handle, pathAddress, 512);
                        var end = Array.IndexOf(pathBytes, (byte)0);
                        if (end is <= 0 or >= 512) continue;
                        var path = Encoding.ASCII.GetString(pathBytes, 0, end).Replace('/', '\\');
                        if (!IsExteriorWindow(path) && !IsInteriorWindow(path)) continue;
                        var carToken = ExtractCarToken(path);
                        if (!string.Equals(carToken, currentCarToken, StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add(new(wrapper, path, currentCarToken, renderOwner?.Pose));
                    }
                    catch (Win32Exception) { } // A pooled object may disappear while scanning.
                }
            }
            cursor = regionEnd;
        }
        renderOwner?.Validate();

        var candidate = matches.GroupBy(item => item.CarToken, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                CarToken = group.Key,
                Items = SelectMovableWindows(group.Where(item => HasUsableWindowVector(context, item)).ToArray(), target.Component),
                Distance = group.Min(item => item.Wrapper > target.Component
                    ? item.Wrapper - target.Component : target.Component - item.Wrapper)
            })
            // Rooted ownership establishes the player's car independently of
            // either front window being allocated. An empty LF vector on the
            // updated RS6 must not disable its other verified window positions.
            // Older profiles retain their original two-front-window guard.
            .Where(group => (renderOwner is not null ? group.Items.Length > 0 :
                            group.Items.Any(item => WindowStem(item.Path).Equals("glassLF", StringComparison.OrdinalIgnoreCase)) &&
                            group.Items.Any(item => WindowStem(item.Path).Equals("glassRF", StringComparison.OrdinalIgnoreCase))) &&
                            group.CarToken.Equals(currentCarToken, StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.Distance)
            .ThenByDescending(group => group.Items.Length)
            .FirstOrDefault() ?? throw new InvalidOperationException("current-car window render wrappers were not found");

        var selected = candidate.Items;
        foreach (var item in selected) _ = ValidateWindowVector(context, item);
        _windowWrapperCache = new(context.ProcessId, target.Vehicle, target.Component, candidate.CarToken,
            renderOwner?.Pose, Environment.TickCount64, selected);
        return selected;
    }

    private static void CheckWindowDiscoveryBudget(Stopwatch clock)
    {
        if (clock.ElapsedMilliseconds >= 3000)
            throw new InvalidOperationException("Window discovery reached its three-second limit; no windows changed. Try again once the car has finished loading.");
    }

    private static void ValidateWindowPlayerOwner(NativeContext context, AnimationTarget target, ulong? pose)
    {
        if (pose is null) return; // Older profiles retain their existing guards.
        var build = DrlBuildMapping.ForProfile(context.Profile)
            ?? throw new InvalidOperationException("Current game build has no window scene mapping; no windows changed.");
        var owner = ChargerRenderOwner.Capture((address, count) => Read(context.Handle, address, count), () =>
        {
            var current = ValidateAnimationTarget(context);
            return current.Vehicle == target.Vehicle && current.Component == target.Component;
        }, context.Module, target.Vehicle, target.Component, build);
        if (owner.Pose != pose)
            throw new InvalidOperationException("Current player window scene changed; no windows changed.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMemoryRegion
    {
        internal ulong Base, AllocationBase;
        internal uint AllocationProtection;
        internal ushort Partition, Padding;
        internal ulong Size;
        internal uint State, Protection, Type, Padding2;
    }
    [DllImport("kernel32.dll", EntryPoint = "VirtualQueryEx", SetLastError = true)]
    private static extern nuint QueryWindowMemory(SafeProcessHandle process, nuint address,
        out WindowMemoryRegion region, nuint length);

    private static string? TryGetCurrentCarToken(NativeContext context, AnimationTarget target)
    {
        // The validated player's animation component owns a runtime clip path.
        // Unlike the vehicle object, this component changes with every car swap.
        try
        {
            var runtime = ReadUInt64(context.Handle, target.Component + 0x28);
            if (runtime < 0x10000) return null;
            var clip = ReadUInt64(context.Handle, runtime + 0x80);
            if (clip < 0x10000) return null;
            var path = Encoding.ASCII.GetString(Read(context.Handle, clip, 256)).Replace('/', '\\');
            const string prefix = "game:\\media\\cars\\";
            var start = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start is < 0 or > 64) return null;
            start += prefix.Length;
            const string suffix = "\\scene\\animations\\mojo\\clip\\carclips_";
            var end = path.IndexOf(suffix, start, StringComparison.OrdinalIgnoreCase);
            if (end <= start || end - start > 96) return null;
            var token = path[start..end];
            return token.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? token : null;
        }
        catch { return null; }
    }

    private static WindowWrapper[] SelectMovableWindows(WindowWrapper[] all, ulong component)
    {
        var hasLm = all.Any(item => WindowStem(item.Path).Equals("glassLM", StringComparison.OrdinalIgnoreCase));
        var hasRm = all.Any(item => WindowStem(item.Path).Equals("glassRM", StringComparison.OrdinalIgnoreCase));
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "glassLF", "glassRF", hasLm ? "glassLM" : "glassLR", hasRm ? "glassRM" : "glassRR"
        };
        // Different cars have different numbers of side windows. Keep one live
        // wrapper for each available exterior/interior layer of each position.
        // Livery and old pooled instances may duplicate a path; the nearest
        // validated wrapper is the one belonging to this render arena.
        return all.Where(item => stems.Contains(WindowStem(item.Path)))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Wrapper > component
                ? item.Wrapper - component : component - item.Wrapper).First())
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static WindowVector ValidateWindowVector(NativeContext context, WindowWrapper item)
    {
        var expectedVtable = context.Profile.CarRenderModelWrapperVtableRva is { } rva
            ? context.Module + rva : 0;
        if (expectedVtable == 0 || ReadUInt64(context.Handle, item.Wrapper) != expectedVtable)
            throw new InvalidOperationException($"Window wrapper identity changed for {item.Path}.");
        var instance = ReadUInt64(context.Handle, item.Wrapper + 0x60);
        if (instance < 0x10000 || ReadUInt64(context.Handle, item.Wrapper + 0x68) != instance - 0x10 ||
            context.Profile.CarModelInstanceVtableRva is not { } instanceRva ||
            ReadUInt64(context.Handle, instance) != context.Module + instanceRva ||
            context.Profile.CarModelResourceVtableRva is not { } modelRva)
            throw new InvalidOperationException($"Window instance changed for {item.Path}.");
        var model = ReadUInt64(context.Handle, instance + 0x20);
        if (model < 0x10000 || ReadUInt64(context.Handle, model) != context.Module + modelRva)
            throw new InvalidOperationException($"Window model changed for {item.Path}.");
        if (item.Pose is { } pose && ReadUInt64(context.Handle, instance + 0x38) != pose)
            throw new InvalidOperationException($"Window player pose changed for {item.Path}.");
        var pathAddress = ReadUInt64(context.Handle, model + 0x80);
        var pathBytes = Read(context.Handle, pathAddress, 512);
        var pathEnd = Array.IndexOf(pathBytes, (byte)0);
        if (pathEnd <= 0 || !Encoding.ASCII.GetString(pathBytes, 0, pathEnd).Replace('/', '\\')
                .Equals(item.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Window path changed for {item.Path}.");
        var bytes = Read(context.Handle, item.Wrapper + 0x300, 0x18);
        var begin = BitConverter.ToUInt64(bytes, 0);
        var end = BitConverter.ToUInt64(bytes, 8);
        var capacity = BitConverter.ToUInt64(bytes, 16);
        if (begin < 0x10000 || capacity <= begin || capacity - begin > 0x10000 ||
            ((begin | end | capacity) & 0xF) != 0 || end < begin || end > capacity ||
            (end != begin && end != capacity))
            throw new InvalidOperationException($"Window render-entry vector shape mismatch for {item.Path}.");
        return new(begin, end, capacity);
    }

    private static bool HasUsableWindowVector(NativeContext context, WindowWrapper item)
    {
        try { _ = ValidateWindowVector(context, item); return true; }
        catch { return false; }
    }

    private static string WindowStem(string path) => Path.GetFileNameWithoutExtension(path)
        .Replace("_a", "", StringComparison.OrdinalIgnoreCase)
        .Replace("Int", "", StringComparison.OrdinalIgnoreCase)
        .Replace("Livery", "", StringComparison.OrdinalIgnoreCase);

    private static bool HasAllWindowPositions(WindowWrapper[] wrappers)
    {
        var stems = wrappers.Select(item => WindowStem(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return stems.Contains("glassLF") && stems.Contains("glassRF") &&
            (stems.Contains("glassLM") || stems.Contains("glassLR")) &&
            (stems.Contains("glassRM") || stems.Contains("glassRR"));
    }

    private static bool IsExteriorWindow(string path) =>
        path.Contains("\\exterior\\windows\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteriorWindow(string path) =>
        path.Contains("\\interior\\interiorwindows\\", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractCarToken(string path)
    {
        const string prefix = "game:\\media\\cars\\";
        var start = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += prefix.Length;
        var end = path.IndexOf("\\scene\\", start, StringComparison.OrdinalIgnoreCase);
        return end > start ? path[start..end] : null;
    }

    private sealed record WindowWrapper(ulong Wrapper, string Path, string CarToken, ulong? Pose);
    private sealed record WindowWrapperCache(int ProcessId, ulong Vehicle, ulong Component,
        string CarToken, ulong? Pose, long CapturedAt, WindowWrapper[] Wrappers);
    private readonly record struct WindowVector(ulong Begin, ulong End, ulong Capacity);
    private static bool HasWindowMapping(GameBuildProfile profile) =>
        profile.CarRenderModelWrapperVtableRva.HasValue &&
        profile.CarModelInstanceVtableRva.HasValue &&
        profile.CarModelResourceVtableRva.HasValue;

    internal static NativeControlStatus GetStatus()
    {
        try
        {
            using var context = Locate(action: false);
            var target = ValidateAnimationTarget(context);
            var presentation = InspectPresentation(context);
            var presentationActive = presentation == "active";
            return new(true, presentationActive, context.ProcessId, target.Vehicle, target.Component,
                TryGetCurrentCarToken(context, target), HasWindowMapping(context.Profile),
                presentationActive
                    ? "external free-roam panels ready; garage presentation also active"
                    : "external free-roam panel controls ready");
        }
        catch (Exception ex)
        {
            // The game may have exited between Locate and this error path.
            // Re-enumerating it here can throw (notably on StartTime) and
            // turn an ordinary disconnected status into an app crash.
            return new(false, false, null, null, null, null, false, ex.Message);
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
        using var allocation = new RemoteCallMemoryLease(
            () => VirtualFreeEx(context.Handle, (nuint)remote, 0, MemRelease),
            () => SessionLog.Write("remote_call_memory_retained", $"roof callback; remote=0x{remote:X}; completion not verified", gamePid: context.ProcessId));
        {
            var code = BuildCall(context.CallbackOwner, context.Module + context.Profile.HandlerRva);
            Write(context.Handle, remote, code);
            FlushInstructionCache(context.Handle, (nuint)remote, (nuint)code.Length);
            using var thread = CreateRemoteThread(context.Handle, 0, 0, (nuint)remote, 0, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
            allocation.ThreadStarted();
            var wait = WaitForSingleObject(thread, 5000);
            SessionLog.Write("native_call_wait", $"roof callback; wait=0x{wait:X}", gamePid: context.ProcessId);
            if (wait != 0) throw new InvalidOperationException($"Native roof callback did not finish (wait 0x{wait:X}).");
            allocation.ThreadCompleted();
            return "roof toggled";
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
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x56, 0x48];
        if (!Read(context.Handle, context.Module + context.Profile.BooleanTriggerSetterRva, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");
        var opening = action.StartsWith("open", StringComparison.OrdinalIgnoreCase);
        if (Read(context.Handle, target.Vehicle + 0x82A3, 1)[0] == 0 && !opening)
            throw new InvalidOperationException("free-roam panel presentation is not active");
        EnableOwnedPresentation(context, target);

        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + context.Profile.BooleanTriggerSetterRva, Fnv1a(eventName), true);
        // CinematicCar event delivery asserts the converted key for one update
        // and then rolls it back. Leaving both *_open and *_close latched true
        // makes the animation graph fight itself, so reproduce that pulse.
        Thread.Sleep(50);
        ValidateUnchangedAnimationTarget(context, target);
        DispatchBooleanTrigger(context.Handle, target.Component,
            context.Module + context.Profile.BooleanTriggerSetterRva, Fnv1a(eventName), false);
        return $"{eventName} pulsed externally on vehicle 0x{target.Vehicle:X}";
    }

    internal static string TriggerPopupHeadlights(bool open)
    {
        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x56, 0x48];
        var setter = context.Module + context.Profile.BooleanTriggerSetterRva;
        if (!Read(context.Handle, setter, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native headlight-trigger signature mismatch.");
        EnableOwnedPresentation(context, target);

        var suffix = open ? "open" : "close";
        var hashes = new[] { Fnv1a($"headlightL_{suffix}"), Fnv1a($"headlightR_{suffix}") };
        DispatchBooleanTriggers(context.Handle, target.Component, setter, hashes, true);
        Thread.Sleep(50); // Keep the known event pulse; both sides share one update.
        ValidateUnchangedAnimationTarget(context, target);
        DispatchBooleanTriggers(context.Handle, target.Component, setter, hashes, false);
        return $"popup headlights {(open ? "opened" : "closed")} on vehicle 0x{target.Vehicle:X}";
    }

    internal static string TriggerFreeRoamExplode(bool open)
    {
        string[] eventNames = open
            ? ["doorLF_open", "doorRF_open", "doorLR_open", "doorRR_open", "hood_open", "trunk_open",
               "wing_open", "headlightL_open", "headlightR_open"]
            : ["doorLF_close", "doorRF_close", "doorLR_close", "doorRR_close", "hood_close", "trunk_close",
               "wing_close", "headlightL_close", "headlightR_close"];

        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        byte[] expected = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x56, 0x48];
        var setter = context.Module + context.Profile.BooleanTriggerSetterRva;
        if (!Read(context.Handle, setter, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Native panel-trigger signature mismatch.");
        EnableOwnedPresentation(context, target);

        var hashes = eventNames.Select(Fnv1a).ToArray();
        DispatchBooleanTriggers(context.Handle, target.Component, setter, hashes, true);
        Thread.Sleep(50);
        ValidateUnchangedAnimationTarget(context, target);
        DispatchBooleanTriggers(context.Handle, target.Component, setter, hashes, false);
        return $"{(open ? "exploded" : "imploded")} panels, active aero, and pop-up headlights on vehicle 0x{target.Vehicle:X}";
    }

    internal static string RestoreFreeRoamPresentationFlag()
    {
        if (_presentationOwnership is not { } owned) return "no menu-owned presentation flag to restore";
        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        if (!owned.Matches(context.ProcessId, context.Module, target.Vehicle, target.Component))
        {
            _presentationOwnership = null;
            SessionLog.Write("presentation_restore_skipped", "Current car/process changed; previous car's flag left untouched", gamePid: context.ProcessId);
            return "previous-car presentation tracking cleared";
        }
        var flagAddress = target.Vehicle + 0x82A3;
        var flag = Read(context.Handle, flagAddress, 1)[0];
        if (flag == owned.Original)
        {
            _presentationOwnership = null;
            return "free-roam presentation already restored";
        }
        if (flag != 1) throw new InvalidOperationException("vehicle Autovista flag is invalid");
        ValidateUnchangedAnimationTarget(context, target);
        Write(context.Handle, flagAddress, [owned.Original]);
        if (Read(context.Handle, flagAddress, 1)[0] != owned.Original)
            throw new InvalidOperationException("vehicle Autovista flag restore did not persist");
        _presentationOwnership = null;
        SessionLog.Write("presentation_restored", $"vehicle=0x{target.Vehicle:X}; original={owned.Original}", gamePid: context.ProcessId,
            carToken: TryGetCurrentCarToken(context, target), component: target.Component);
        return $"free-roam presentation restored on vehicle 0x{target.Vehicle:X}";
    }

    private static void ValidateUnchangedAnimationTarget(NativeContext context, AnimationTarget target)
    {
        if (ValidateAnimationTarget(context) != target)
            throw new InvalidOperationException("Current car changed before the panel update; no further writes.");
    }

    private static void EnableOwnedPresentation(NativeContext context, AnimationTarget target)
    {
        ValidateUnchangedAnimationTarget(context, target);
        var address = target.Vehicle + 0x82A3;
        var original = Read(context.Handle, address, 1)[0];
        if (original > 1) throw new InvalidOperationException("vehicle Autovista flag is invalid");
        if (original == 1) return; // Preserve presentation supplied by the game.
        _presentationOwnership = new(context.ProcessId, context.Module, target.Vehicle, target.Component, original);
        Write(context.Handle, address, [1]);
        SessionLog.Write("presentation_enabled", $"vehicle=0x{target.Vehicle:X}; original={original}", gamePid: context.ProcessId,
            carToken: TryGetCurrentCarToken(context, target), component: target.Component);
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
            SessionLog.Write("render_mode_request", $"build={profile.Name}; module=0x{module:X}; controller=0x{controller:X}; {current} -> {mode}");
            Write(handle, controller + 0x2BC, BitConverter.GetBytes(0));
            Write(handle, controller + 0x2B4, BitConverter.GetBytes(current));
            Write(handle, controller + 0x2B0, BitConverter.GetBytes(mode));
            Write(handle, controller + 0x2B8, [1]);
            SessionLog.Write("render_mode_published", $"{current} -> {mode}; pending request published");
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
        .OrderByDescending(p =>
        {
            try { return p.StartTime; }
            catch (Win32Exception) { return DateTime.MinValue; }
            catch (InvalidOperationException) { return DateTime.MinValue; }
        })
        .FirstOrDefault();

    private static NativeContext LocateDynamic(int processId, SafeProcessHandle handle,
        ulong module, uint sizeOfImage, GameBuildProfile profile)
    {
        // Once a package-build chain has been fully verified, reuse its stable
        // callback owner/controller for the rest of that process.  Revalidate
        // the owner identity every time and fall back to discovery if the game
        // rebuilt the objects (for example after a session transition).
        var cached = _dynamicContextHint;
        if (cached is not null && cached.ProcessId == processId && cached.Module == module)
        {
            try
            {
                var ownerBytes = Read(handle, cached.CallbackOwner, 0x50);
                if (BitConverter.ToUInt64(ownerBytes, 0) == module + profile.OwnerVtableRva &&
                    BitConverter.ToUInt64(ownerBytes, 0x40) == cached.CarController)
                    return new NativeContext(processId, module, cached.CallbackOwner,
                        cached.CarController, profile, handle);
            }
            catch { }
            _dynamicContextHint = null;
        }

        // In the Microsoft Store layout the service registry used by this
        // subscription chain lives next to the render-system object.  Resolve
        // that stable relationship before considering the large .data scan.
        if (profile.RenderSystemGlobalRva is { } renderGlobalRva)
        {
            try
            {
                var renderGlobal = ReadUInt64(handle, module + renderGlobalRva);
                var renderSystem = ReadUInt64(handle, renderGlobal + 0x1D8);
                var direct = TryLocateDynamicRoot(processId, handle, module,
                    renderSystem + 0x80, profile);
                if (direct is not null) return direct;
            }
            catch { }
        }

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
                        _dynamicContextHint = new(processId, module, callbackOwner, carController);
                        return new NativeContext(processId, module, callbackOwner, carController, profile, handle);
                    }
                }
            }
        }
        throw new InvalidOperationException("supported convertible not active");
    }

    private static NativeContext? TryLocateDynamicRoot(int processId, SafeProcessHandle handle,
        ulong module, ulong root, GameBuildProfile profile)
    {
        static bool HeapPointer(ulong value) =>
            value is >= 0x10000 and < 0x0000800000000000 && (value & 7) == 0;
        if (!HeapPointer(root)) return null;

        ulong table;
        ulong slots;
        try
        {
            table = ReadUInt64(handle, root + 8);
            slots = HeapPointer(table) ? ReadUInt64(handle, table + 0xE8) : 0;
        }
        catch { return null; }
        if (!HeapPointer(slots)) return null;

        for (var slotIndex = 0; slotIndex < 0x400; slotIndex++)
        {
            ulong owner;
            try { owner = ReadUInt64(handle, slots + (ulong)slotIndex * 0x10); }
            catch { continue; }
            if (!HeapPointer(owner)) continue;

            ulong begin, end, capacity;
            try
            {
                begin = ReadUInt64(handle, owner + 0xD8);
                end = ReadUInt64(handle, owner + 0xE0);
                capacity = ReadUInt64(handle, owner + 0xE8);
            }
            catch { continue; }
            if (!HeapPointer(begin) || end < begin || capacity < end ||
                ((end - begin) & 7) != 0 || end - begin is 0 or > 0x8000) continue;

            var count = checked((int)((end - begin) / 8));
            for (var item = 0; item < count; item++)
            {
                ulong subscription;
                try { subscription = ReadUInt64(handle, begin + (ulong)item * 8); }
                catch { continue; }
                if (!HeapPointer(subscription)) continue;

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
                    var wrapperAddress = BitConverter.ToUInt64(mappings, mappingIndex * 0x10 + 8);
                    if (!HeapPointer(wrapperAddress)) continue;
                    byte[] wrapper;
                    try { wrapper = Read(handle, wrapperAddress, 0x68); }
                    catch { continue; }
                    if (BitConverter.ToUInt64(wrapper, 0) != module + profile.CallbackVtableRva ||
                        BitConverter.ToUInt64(wrapper, 0x20) != module + profile.CallbackInterfaceVtableRva ||
                        BitConverter.ToUInt64(wrapper, 0x28) != module + profile.LambdaVtableRva ||
                        BitConverter.ToUInt64(wrapper, 0x30) != module + profile.HandlerRva ||
                        BitConverter.ToUInt64(wrapper, 0x60) != wrapperAddress + 0x28) continue;

                    var callbackOwner = BitConverter.ToUInt64(wrapper, 0x40);
                    if (!HeapPointer(callbackOwner)) continue;
                    byte[] ownerBytes;
                    try { ownerBytes = Read(handle, callbackOwner, 0x50); }
                    catch { continue; }
                    if (BitConverter.ToUInt64(ownerBytes, 0) != module + profile.OwnerVtableRva) continue;
                    var carController = BitConverter.ToUInt64(ownerBytes, 0x40);
                    if (!HeapPointer(carController)) continue;

                    _dynamicContextHint = new(processId, module, callbackOwner, carController);
                    return new NativeContext(processId, module, callbackOwner, carController, profile, handle);
                }
            }
        }
        return null;
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

    private static void DispatchBooleanTrigger(SafeProcessHandle process, ulong component, ulong setter, uint hash, bool value) =>
        DispatchBooleanTriggers(process, component, setter, [hash], value);

    private static void DispatchBooleanTriggers(SafeProcessHandle process, ulong component, ulong setter,
        IReadOnlyList<uint> hashes, bool value)
    {
        if (hashes.Count == 0) return;
        var remote = (ulong)VirtualAllocEx(process, 0, 0x1000, MemCommitReserve, PageExecuteReadWrite);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
        using var allocation = new RemoteCallMemoryLease(
            () => VirtualFreeEx(process, (nuint)remote, 0, MemRelease),
            () => SessionLog.Write("remote_call_memory_retained", $"panel triggers; component=0x{component:X}; remote=0x{remote:X}; completion not verified"));
        {
            const ulong hashDataOffset = 0x800;
            var code = new List<byte>();
            code.AddRange([0x48, 0x83, 0xEC, 0x28]);
            for (var index = 0; index < hashes.Count; index++)
            {
                code.AddRange([0x48, 0xB9]); code.AddRange(BitConverter.GetBytes(component));
                code.AddRange([0x48, 0xBA]);
                code.AddRange(BitConverter.GetBytes(remote + hashDataOffset + (ulong)index * 4));
                code.AddRange([0x41, 0xB8]); code.AddRange(BitConverter.GetBytes(value ? 1u : 0u));
                code.AddRange([0x48, 0xB8]); code.AddRange(BitConverter.GetBytes(setter));
                code.AddRange([0xFF, 0xD0]);
            }
            code.AddRange([0xB8, 0x01, 0x00, 0x00, 0x00, 0x48, 0x83, 0xC4, 0x28, 0xC3]);
            if (code.Count >= (int)hashDataOffset || hashes.Count * sizeof(uint) > 0x800)
                throw new InvalidOperationException("Native panel-trigger batch is too large.");
            Write(process, remote, code.ToArray());
            var hashData = new byte[hashes.Count * sizeof(uint)];
            for (var index = 0; index < hashes.Count; index++)
                BitConverter.GetBytes(hashes[index]).CopyTo(hashData, index * sizeof(uint));
            Write(process, remote + hashDataOffset, hashData);
            FlushInstructionCache(process, (nuint)remote, (nuint)code.Count);
            using var thread = CreateRemoteThread(process, 0, 0, (nuint)remote, 0, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
            allocation.ThreadStarted();
            var wait = WaitForSingleObject(thread, 5000);
            SessionLog.Write("native_call_wait", $"panel triggers; component=0x{component:X}; count={hashes.Count}; value={value}; wait=0x{wait:X}", component: component);
            if (wait != 0) throw new InvalidOperationException($"Native panel trigger did not finish (wait 0x{wait:X}).");
            allocation.ThreadCompleted();
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

    private sealed record DynamicContextHint(int ProcessId, ulong Module, ulong CallbackOwner,
        ulong CarController);

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
