namespace ForzavistaFreeRoam;

internal sealed record NativeLightStatus(bool Available, bool HeadlightsOn, bool DrlOn, string Message)
{
    internal bool RunningLightsOn { get; init; } = HeadlightsOn;
    internal bool HeadlightControlAvailable { get; init; } = true;
    internal bool RearControlAvailable { get; init; } = true;
    internal bool Fading { get; init; }
    internal bool AllOn => HeadlightsOn && DrlOn && RunningLightsOn;
    internal bool AnyOn => HeadlightsOn || DrlOn || RunningLightsOn;
}

internal sealed record NativeLightActionResult(bool Success, string Message)
{
    internal NativeLightStatus? LightStatus { get; init; }
}

internal static partial class NativeCarControl
{
    private const int LightSelector = 0x9231, AutomaticLights = 0x9232,
        ManualLights = 0x9248, DrlLights = 0x924A;
    private static readonly object LightGate = new();
    private static LightOwnership? _lightOwnership;
    private static readonly System.Diagnostics.Stopwatch LightFadeClock = System.Diagnostics.Stopwatch.StartNew();
    private static float _nativeStrobeFadeLevel = 1;
    internal static float NativeStrobeFadeLevel => Volatile.Read(ref _nativeStrobeFadeLevel);
    internal static bool HasLightFade { get { lock (LightGate) return _lightOwnership?.Fades.Count > 0; } }

    internal sealed record LightIdentity(int Pid, ulong Module, ulong Vehicle, ulong Component,
        string Car, ulong Sentinel, ulong Count);
    private sealed class LightOwnership(LightIdentity identity)
    {
        internal LightIdentity Identity { get; } = identity;
        internal Dictionary<int, (byte Original, byte Last)> Bytes { get; } = [];
        internal Dictionary<int, byte> PendingBytes { get; } = [];
        internal Dictionary<uint, HeadlampBranch> Headlamps { get; } = [];
        internal Dictionary<uint, HeadlampBranch> RearLamps { get; } = [];
        internal Dictionary<uint, HeadlampBranch> FrontLamps { get; } = [];
        internal Dictionary<string, LightFadeRamp> Fades { get; } = [];
    }

    private sealed record HeadlampBranch(uint Output, ulong Controller, ulong Sentinel,
        ulong[] Nodes, byte[][] Originals)
    {
        internal uint OriginalSource { get; } = BitConverter.ToUInt32(Originals[0], 8);
        internal OwnedLightInstruction Instruction { get; } = new(Originals[0]);
        internal uint Source => Instruction.Source;
    }

    internal static NativeLightStatus GetLightStatus()
    {
        lock (LightGate)
        {
            try
            {
                using var context = Locate(action: false);
                var identity = ValidateLightIdentity(context);
                ReconcileLightOwnership(context, identity);
                return ReadLightStatus(context, identity);
            }
            catch (Exception ex) { return new(false, false, false, ex.Message); }
        }
    }

    internal static NativeLightActionResult ToggleLights(string action, double fadeSeconds = 0)
    {
        var actions = action.StartsWith("lightsgroup:", StringComparison.Ordinal)
            ? action["lightsgroup:".Length..].Split(',').Distinct().ToArray() : [action];
        if (actions.Length == 0 || actions.Any(item => item is not ("lightsdrl" or "lightsrear" or "lightsheadlights" or "lightsall")))
            throw new ArgumentException("Unknown lighting action.", nameof(action));
        var all = actions.Contains("lightsall");
        var front = all || actions.Contains("lightsdrl");
        var head = all || actions.Contains("lightsheadlights");
        var rear = all || actions.Contains("lightsrear");
        lock (LightGate)
        {
            using var context = Locate(action: true);
            var identity = ValidateLightIdentity(context);
            ReconcileLightOwnership(context, identity);
            var state = ReadLightStatus(context, identity);
            // Shared bindings align only their selected groups; a mixed group turns on.
            var on = !((!front || state.DrlOn) && (!head || state.HeadlightsOn) &&
                (!rear || state.RunningLightsOn));
            var owner = _lightOwnership ??= new(identity);
            try
            {
                ValidateOwnedLightBytes(context, owner);
                string? fadeFallback = null;
                if (fadeSeconds > 0)
                {
                    try
                    {
                        PreflightLightFade(context, owner, front, head, rear);
                    }
                    catch (InvalidOperationException ex) { fadeFallback = ex.Message; }
                    if (fadeFallback is null)
                        return BeginLightFade(context, owner, front, head, rear, all, on, fadeSeconds);
                }
                // Disabling Fade affects future toggles. An immediate toggle
                // commits the selected groups' current targets before replacing them.
                FinishSelectedLightFades(context, owner, front, head, rear, on);
                if (!all && (head || rear))
                {
                    if (head && !state.HeadlightControlAvailable)
                        throw new InvalidOperationException("Independent headlamp outputs are not mapped for this car.");
                    if (rear && !state.RearControlAvailable)
                        throw new InvalidOperationException("Independent rear running-light outputs are not mapped for this car.");
                    // Register 0 is initialized to zero; register 11 is the
                    // active-engine input. Neither changes the native popup command.
                    if (on && ReadLightByte(context, identity, 0x924E) != 1)
                        throw new InvalidOperationException("Start the engine before enabling independent lights.");
                    if (head) SetHeadlampBranches(context, owner, on ? 11u : 0u);
                    if (rear) SetLampBranches(context, owner, owner.RearLamps, rear: true, on ? 11u : 0u);
                }
                if (all)
                {
                    RestoreHeadlampBranches(context, owner);
                    RestoreLampBranches(context, owner, owner.RearLamps);
                    // Known OFF/engine operands can survive another instance.
                    // The manual flag alone cannot affect a source-zero branch.
                    // Capture that current form before taking ownership; exit
                    // restores its exact captured operand, not an assumed 5.
                    var recoverHead = state.HeadlightControlAvailable &&
                        CaptureHeadlampBranches(context, identity).Values.Any(b => b.Source != 5);
                    var recoverRear = state.RearControlAvailable &&
                        CaptureLampBranches(context, identity, rear: true).Values.Any(b => b.Source != 5);
                    if (on && (recoverHead || recoverRear) && ReadLightByte(context, identity, 0x924E) != 1)
                        throw new InvalidOperationException("Start the engine before enabling independent lights.");
                    if (recoverHead) SetHeadlampBranches(context, owner, on ? 11u : 0u);
                    if (recoverRear) SetLampBranches(context, owner, owner.RearLamps, rear: true, on ? 11u : 0u);
                    // Select the native manual branch, not the auto byte that
                    // the time-of-day system overwrites. Stock expressions stay intact.
                    WriteOwnedLightByte(context, owner, ManualLights, on ? (byte)1 : (byte)0);
                    WriteOwnedLightByte(context, owner, LightSelector, 0);
                }
                if (front)
                    WriteOwnedLightByte(context, owner, DrlLights, on ? (byte)1 : (byte)0);
                var after = ReadLightStatus(context, identity);
                if ((head && after.HeadlightsOn != on) || (front && after.DrlOn != on) ||
                    (rear && after.RunningLightsOn != on))
                    throw new InvalidOperationException("The game did not retain the requested lighting state.");
                var label = all ? "All normal lights" : string.Join(" + ", actions.Select(item => item switch
                {
                    "lightsdrl" => "Front DRLs", "lightsrear" => "Rear running lights", _ => "Headlights"
                }));
                return new(true, $"{label}: {(on ? "ON" : "OFF")}. " +
                    (fadeFallback is null ? "RESET STATE restores original lighting." : "Switched instantly; fade unavailable: " + fadeFallback))
                    { LightStatus = after };
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "Lighting flags changed outside the menu; no further write attempted." ||
                ex.Message == "Native lighting write did not persist." ||
                ex.Message == "The game did not retain the requested lighting state.")
            {
                // A normal scene transition is a retryable outcome, not an
                // exception escaping the worker task into the VS debugger.
                try { ReconcileLightOwnership(context, ValidateLightIdentity(context)); } catch { }
                return new(false, "Lighting changed during the scene transition. State refreshed; try the toggle again when Photo Mode is loaded.");
            }
            catch
            {
                // Never roll back into a different car or overwrite externally changed bytes.
                try { RestoreLightOwnership(context, owner); } catch { }
                throw;
            }
        }
    }

    internal static string RestoreTrackedLights()
    {
        lock (LightGate)
        {
            if (_lightOwnership is not { } owner) return "Automatic lighting unchanged.";
            using var context = Locate(action: true);
            var identity = ValidateLightIdentity(context);
            ReconcileLightOwnership(context, identity);
            if (_lightOwnership is null) return "Scene-managed lighting left unchanged.";
            if (identity != owner.Identity)
            {
                _lightOwnership = null;
                return "Previous-car lighting tracking cleared; no stale addresses written.";
            }
            RestoreLightOwnership(context, owner);
            return "Original lighting state restored.";
        }
    }

    private static void DropStaleLightOwnership(LightIdentity identity)
    {
        if (_lightOwnership is { } owner && owner.Identity != identity)
        {
            _lightOwnership = null;
            Volatile.Write(ref _nativeStrobeFadeLevel, 1);
        }
    }

    private static void ReconcileLightOwnership(NativeContext context, LightIdentity identity)
    {
        DropStaleLightOwnership(identity);
        if (_lightOwnership is not { } owner) return;
        ResolvePendingLightFlags(context, owner);
        // Photo Mode may reset these valid flags without replacing the vehicle.
        // Release ownership of the normal selector/manual pair together so reset
        // never restores a pre-transition pair over the game's new selection.
        var released = LightFlagOwnershipPolicy.ChangedOffsets(
            owner.Bytes.ToDictionary(pair => pair.Key, pair => pair.Value.Last),
            offset => ReadLightByte(context, identity, offset));
        var normalChanged = released.Contains(LightSelector) || released.Contains(ManualLights);
        var drlChanged = released.Contains(DrlLights);
        foreach (var offset in released) owner.Bytes.Remove(offset);
        if (normalChanged || drlChanged)
            SessionLog.Write("light_scene_rebase", $"normalChanged={normalChanged}; drlChanged={drlChanged}; stale flag ownership released",
                gamePid: identity.Pid, carToken: identity.Car, component: identity.Component);
        // Only relinquish an expression if the complete original list payloads
        // are back. Any unknown external expression still fails closed.
        var rebased = new HashSet<string>();
        var liveControllers = owner.Headlamps.Count + owner.RearLamps.Count + owner.FrontLamps.Count > 0
            ? ReadCurrentLightControllers(context, identity) : null;
        foreach (string group in new[] { "head", "rear", "front" })
        {
            var tracked = FadeBranches(owner, group);
            if (tracked.Count == 0) continue;
            if (tracked.Values.Any(branch => !liveControllers!.TryGetValue(branch.Output, out var live) ||
                    live != branch.Controller))
            {
                // Photo Mode can replace graph objects under the same vehicle.
                // Never write the obsolete controller addresses.
                tracked.Clear();
                rebased.Add(group);
                SessionLog.Write("light_graph_rebase", "Current-car light graph replaced; obsolete lamp ownership released",
                    gamePid: identity.Pid, carToken: identity.Car, component: identity.Component);
            }
            foreach (var output in tracked.Keys.ToArray())
            {
                var branch = tracked[output];
                if (!branch.Instruction.Current.SequenceEqual(branch.Originals[0]) && branch.Nodes.Select((node, index) =>
                        Read(context.Handle, node + 0x10, 24).SequenceEqual(branch.Originals[index])).All(equal => equal))
                {
                    tracked.Remove(output);
                    rebased.Add(group);
                }
            }
        }
        foreach (string group in owner.Fades.Keys.ToArray())
            if (rebased.Contains(group) || FadeBranches(owner, group).Count == 0 || group == "front" && drlChanged ||
                owner.Fades[group].NativeSwitch && normalChanged)
            {
                // The game reset known flags/operands under the same current
                // owner. Relinquish the other qualified operands in this group
                // so they cannot keep overriding the scene's new lighting.
                owner.Fades.Remove(group);
                RestoreLampBranches(context, owner, FadeBranches(owner, group));
            }
        if (!owner.Fades.ContainsKey("front")) Volatile.Write(ref _nativeStrobeFadeLevel, 1);
    }

    private static LightIdentity ValidateLightIdentity(NativeContext context)
    {
        if (context.Profile.LightEvaluatorRva is not { } evaluator ||
            context.Profile.LightVehicleVtableRva is not { } vtable)
            throw new InvalidOperationException("Lighting is not mapped for this game build yet.");
        // Read the contiguous consumer code once, retaining every pinned check.
        var consumerCode = Read(context.Handle, context.Module + evaluator, 0x100);
        // These guards verify both the normal/manual selector and the DRL consumer.
        foreach (var (offset, hex) in new (ulong, string)[]
        {
            (0x16, "80B93192000000"), (0x24, "BA48920000"),
            (0x39, "B8329200000F44C2"), (0xF9, "80B94A92000000")
        })
        {
            var expected = Convert.FromHexString(hex);
            if (!consumerCode.AsSpan((int)offset, expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("Native light controller signature changed; lighting disabled.");
        }
        var target = ValidateAnimationTarget(context);
        if (ReadUInt64(context.Handle, target.Vehicle) != context.Module + vtable)
            throw new InvalidOperationException("Current-player vehicle type does not match the lighting mapping.");
        var car = TryGetCurrentCarToken(context, target);
        if (string.IsNullOrWhiteSpace(car))
            throw new InvalidOperationException("Current car identity is not ready for lighting.");
        var sentinel = ReadUInt64(context.Handle, target.Vehicle + 0x9258);
        var count = ReadUInt64(context.Handle, target.Vehicle + 0x9260);
        if (sentinel < 0x10000 || count is < 1 or > 128)
            throw new InvalidOperationException("Current-car light controller graph is not ready.");
        return new(context.ProcessId, context.Module, target.Vehicle, target.Component,
            car.ToUpperInvariant(), sentinel, count);
    }

    private static byte ReadLightByte(NativeContext context, LightIdentity identity, int offset)
    {
        var value = Read(context.Handle, identity.Vehicle + (ulong)offset, 1)[0];
        if (value > 1) throw new InvalidOperationException("Unexpected native lighting flag; no write attempted.");
        return value;
    }

    private static NativeLightStatus ReadLightStatus(NativeContext context, LightIdentity identity)
    {
        var selector = ReadLightByte(context, identity, LightSelector);
        var normal = ReadLightByte(context, identity, selector == 1 ? AutomaticLights : ManualLights);
        var drl = ReadLightByte(context, identity, DrlLights);
        var headlights = normal == 1;
        var headMapped = false;
        var running = normal == 1;
        var rearMapped = false;
        try
        {
            if (_lightOwnership is { } owner && owner.Identity == identity && owner.Headlamps.Count != 0)
            {
                ValidateHeadlampBranches(context, owner);
                headlights = LampGroupOn(owner.Headlamps, normal, ReadLightByte(context, identity, 0x924E));
            }
            else headlights = LampGroupOn(CaptureHeadlampBranches(context, identity), normal,
                ReadLightByte(context, identity, 0x924E));
            headMapped = true;
        }
        catch (Exception) { /* Keep verified native DRL/all-light controls usable. */ }
        try
        {
            if (_lightOwnership is { } owner && owner.Identity == identity && owner.RearLamps.Count != 0)
            {
                ValidateLampBranches(context, owner, owner.RearLamps);
                running = LampGroupOn(owner.RearLamps, normal, ReadLightByte(context, identity, 0x924E));
            }
            else running = LampGroupOn(CaptureLampBranches(context, identity, rear: true), normal,
                ReadLightByte(context, identity, 0x924E));
            rearMapped = true;
        }
        catch (Exception) { /* Unknown rear expressions never disable the native all-light switch. */ }
        return new(true, headlights, drl == 1,
            headMapped ? "Independent headlamp outputs mapped" : "This car's independent headlamp expression is not mapped.")
        {
            HeadlightControlAvailable = headMapped,
            RearControlAvailable = rearMapped,
            Fading = _lightOwnership?.Fades.Count > 0,
            HeadlightsOn = _lightOwnership?.Fades.GetValueOrDefault("head")?.On ?? headlights,
            DrlOn = _lightOwnership?.Fades.GetValueOrDefault("front")?.On ?? (drl == 1),
            RunningLightsOn = _lightOwnership?.Fades.GetValueOrDefault("rear")?.On ?? running
        };
    }

    private static bool LampGroupOn(Dictionary<uint, HeadlampBranch> branches, byte normal, byte engine) =>
        branches.Count != 0 && branches.Values.All(branch => branch.Source switch
        { 5 => normal == 1, 11 => engine == 1, _ => false });

    private static void ValidateOwnedLightBytes(NativeContext context, LightOwnership owner)
    {
        if (ValidateLightIdentity(context) != owner.Identity)
            throw new InvalidOperationException("Current car changed; lighting action cancelled.");
        ResolvePendingLightFlags(context, owner);
        foreach (var (offset, value) in owner.Bytes)
            if (ReadLightByte(context, owner.Identity, offset) != value.Last)
                throw new InvalidOperationException("Lighting flags changed outside the menu; no further write attempted.");
        var controllers = owner.Headlamps.Count + owner.RearLamps.Count + owner.FrontLamps.Count > 0
            ? ReadCurrentLightControllers(context, owner.Identity) : null;
        ValidateLampBranches(context, owner, owner.Headlamps, controllers);
        ValidateLampBranches(context, owner, owner.RearLamps, controllers);
        ValidateLampBranches(context, owner, owner.FrontLamps, controllers);
    }

    private static void ResolvePendingLightFlags(NativeContext context, LightOwnership owner)
    {
        foreach (var (offset, pending) in owner.PendingBytes.ToArray())
        {
            byte live = ReadLightByte(context, owner.Identity, offset);
            if (live == pending) owner.Bytes[offset] = (owner.Bytes[offset].Original, pending);
            else if (live != owner.Bytes[offset].Last)
                throw new InvalidOperationException("Lighting flags changed outside the menu; no further write attempted.");
            owner.PendingBytes.Remove(offset);
        }
    }

    private static void WriteOwnedLightByte(NativeContext context, LightOwnership owner, int offset, byte value)
    {
        ValidateOwnedLightBytes(context, owner);
        var original = ReadLightByte(context, owner.Identity, offset);
        if (!owner.Bytes.ContainsKey(offset)) owner.Bytes.Add(offset, (original, original));
        owner.PendingBytes[offset] = value;
        Write(context.Handle, owner.Identity.Vehicle + (ulong)offset, [value]);
        // Track immediately after a successful write, even if a subsequent read fails.
        owner.Bytes[offset] = (owner.Bytes[offset].Original, value);
        owner.PendingBytes.Remove(offset);
        if (ReadLightByte(context, owner.Identity, offset) != value)
            throw new InvalidOperationException("Native lighting write did not persist.");
    }

    private static void RestoreLightOwnership(NativeContext context, LightOwnership owner)
    {
        ValidateOwnedLightBytes(context, owner);
        owner.Fades.Clear();
        Volatile.Write(ref _nativeStrobeFadeLevel, 1);
        RestoreLampBranches(context, owner, owner.FrontLamps);
        RestoreHeadlampBranches(context, owner);
        RestoreLampBranches(context, owner, owner.RearLamps);
        // Restore manual/DRL first and the automatic selector last.
        foreach (var offset in owner.Bytes.Keys.OrderBy(offset => offset == LightSelector ? 1 : 0).ToArray())
        {
            var original = owner.Bytes[offset].Original;
            WriteOwnedLightByte(context, owner, offset, original);
            owner.Bytes.Remove(offset);
        }
        _lightOwnership = null;
    }

    private static Dictionary<uint, HeadlampBranch> CaptureHeadlampBranches(NativeContext context, LightIdentity identity)
        => CaptureLampBranches(context, identity, rear: false);

    private static Dictionary<uint, HeadlampBranch> CaptureLampBranches(NativeContext context, LightIdentity identity, bool rear)
        => CaptureLightGroup(context, identity, rear ? "rear" : "head");

    private static Dictionary<uint, HeadlampBranch> CaptureLightGroup(NativeContext context, LightIdentity identity, string group)
    {
        var evaluator = context.Module + context.Profile.LightEvaluatorRva!.Value;
        foreach (var (offset, hex) in new (ulong, string)[]
        {
            (0x1D, "0F57C0"), (0x50, "0F11442420"),
            (0x155, "80B94E92000000"), (0x173, "F30F1144244C")
        })
        {
            var expected = Convert.FromHexString(hex);
            if (!Read(context.Handle, evaluator + offset, expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("Independent headlight input signatures changed.");
        }
        var branches = new Dictionary<uint, HeadlampBranch>();
        var node = ReadUInt64(context.Handle, identity.Sentinel);
        var seen = new HashSet<ulong>();
        while (node != identity.Sentinel && (ulong)seen.Count < identity.Count)
        {
            if (node < 0x10000 || !seen.Add(node))
                throw new InvalidOperationException("Light controller list changed.");
            var header = Read(context.Handle, node, 24);
            var controller = BitConverter.ToUInt64(header, 16);
            if (controller < 0x10000) throw new InvalidOperationException("Invalid light controller.");
            var output = ReadUInt32(context.Handle, controller);
            if (group == "front" ? output == 15 : group == "rear" ? output is 32 or 69 : output is 13 or 17 or 71)
            {
                var expectedCount = output is 13 or 15 or 32 ? 1UL : 3UL;
                if (ReadUInt64(context.Handle, controller + 0x20) != 0 ||
                    ReadUInt64(context.Handle, controller + 0x30) != expectedCount)
                    throw new InvalidOperationException("Unmapped headlight sequence/expression layout.");
                var sentinel = ReadUInt64(context.Handle, controller + 0x28);
                if (sentinel < 0x10000) throw new InvalidOperationException("Invalid headlight instruction list.");
                var nodes = new ulong[(int)expectedCount];
                var originals = new byte[nodes.Length][];
                var instruction = ReadUInt64(context.Handle, sentinel);
                for (var index = 0; index < nodes.Length; index++)
                {
                    if (instruction < 0x10000 || instruction == sentinel || nodes.Contains(instruction))
                        throw new InvalidOperationException("Invalid headlight instruction node.");
                    nodes[index] = instruction;
                    originals[index] = Read(context.Handle, instruction + 0x10, 24);
                    instruction = ReadUInt64(context.Handle, instruction);
                }
                bool supported = group == "front" ? originals.Length == 1 && originals[0].SequenceEqual(
                    Convert.FromHexString("010000001B00000007000000000000000000000000000000")) :
                    group == "rear" ? RearLampExpressionPolicy.IsSupported(output, originals) :
                        HeadlampExpressionPolicy.IsSupported(output, originals);
                if (instruction != sentinel || !supported)
                    throw new InvalidOperationException("Lamp expression is not one of the verified layouts.");
                if (!branches.TryAdd(output, new(output, controller, sentinel, nodes, originals)))
                    throw new InvalidOperationException("Duplicate headlight output.");
            }
            node = BitConverter.ToUInt64(header);
        }
        if (node != identity.Sentinel || (ulong)seen.Count != identity.Count ||
            (group == "front" ? !branches.ContainsKey(15) : group == "rear" ? branches.Count == 0 : !branches.ContainsKey(13)))
            throw new InvalidOperationException("Complete player-car lamp graph was not found.");
        return branches;
    }

    private static void ValidateHeadlampBranches(NativeContext context, LightOwnership owner)
        => ValidateLampBranches(context, owner, owner.Headlamps);

    private static void ValidateLampBranches(NativeContext context, LightOwnership owner,
        Dictionary<uint, HeadlampBranch> branches, Dictionary<uint, ulong>? controllers = null)
    {
        if (branches.Count == 0) return;
        var liveControllers = controllers ?? ReadCurrentLightControllers(context, owner.Identity);
        foreach (var branch in branches.Values)
        {
            var header = Read(context.Handle, branch.Controller, 0x38);
            if (!liveControllers.TryGetValue(branch.Output, out var live) || live != branch.Controller ||
                BitConverter.ToUInt32(header, 0) != branch.Output ||
                BitConverter.ToUInt64(header, 0x20) != 0 ||
                BitConverter.ToUInt64(header, 0x28) != branch.Sentinel ||
                BitConverter.ToUInt64(header, 0x30) != (ulong)branch.Nodes.Length)
                throw new InvalidOperationException("Tracked headlamp controller changed.");
            var node = ReadUInt64(context.Handle, branch.Sentinel);
            for (var index = 0; index < branch.Nodes.Length; index++)
            {
                if (node != branch.Nodes[index])
                    throw new InvalidOperationException("Tracked headlamp instruction node changed.");
                var nodeBytes = Read(context.Handle, node, 0x28);
                var instruction = nodeBytes.AsSpan(0x10, 24).ToArray();
                if (index == 0) branch.Instruction.Validate(() => instruction);
                else if (!instruction.SequenceEqual(branch.Originals[index]))
                    throw new InvalidOperationException("Tracked headlamp expression changed outside the menu.");
                node = BitConverter.ToUInt64(nodeBytes, 0);
            }
            if (node != branch.Sentinel) throw new InvalidOperationException("Tracked headlamp list changed.");
        }
    }

    private static void SetHeadlampBranches(NativeContext context, LightOwnership owner, uint source)
        => SetLampBranches(context, owner, owner.Headlamps, rear: false, source);

    private static void SetLampBranches(NativeContext context, LightOwnership owner,
        Dictionary<uint, HeadlampBranch> branches, bool rear, uint source)
    {
        ValidateOwnedLightBytes(context, owner);
        if (branches.Count == 0)
            foreach (var (output, branch) in CaptureLampBranches(context, owner.Identity, rear))
                branches.Add(output, branch);
        foreach (var branch in branches.Values)
        {
            ValidateOwnedLightBytes(context, owner);
            // Only the first running/normal operand. Brake/high-beam branches,
            // material assets, output ids, lists and popup flags remain untouched.
            WriteLightInstruction(context, owner, branch, HeadlampExpressionPolicy.WithNormalSource(branch.Originals[0], source));
            ValidateOwnedLightBytes(context, owner);
        }
    }

    private static void RestoreHeadlampBranches(NativeContext context, LightOwnership owner)
        => RestoreLampBranches(context, owner, owner.Headlamps);

    private static void RestoreLampBranches(NativeContext context, LightOwnership owner,
        Dictionary<uint, HeadlampBranch> branches)
    {
        foreach (var output in branches.Keys.ToArray())
        {
            ValidateOwnedLightBytes(context, owner);
            var branch = branches[output];
            WriteLightInstruction(context, owner, branch, branch.Originals[0]);
            ValidateOwnedLightBytes(context, owner);
            branches.Remove(output);
        }
    }

    private static Dictionary<uint, ulong> ReadCurrentLightControllers(NativeContext context, LightIdentity identity)
    {
        var result = new Dictionary<uint, ulong>();
        var visited = new HashSet<ulong>();
        var node = ReadUInt64(context.Handle, identity.Sentinel);
        while (node != identity.Sentinel && (ulong)visited.Count < identity.Count)
        {
            if (node < 0x10000 || !visited.Add(node))
                throw new InvalidOperationException("Current-car light graph changed while reading.");
            var bytes = Read(context.Handle, node, 24);
            var controller = BitConverter.ToUInt64(bytes, 16);
            if (controller < 0x10000 || !result.TryAdd(ReadUInt32(context.Handle, controller), controller))
                throw new InvalidOperationException("Invalid/duplicate current light controller.");
            node = BitConverter.ToUInt64(bytes);
        }
        if (node != identity.Sentinel || (ulong)visited.Count != identity.Count)
            throw new InvalidOperationException("Current-car light graph bounds changed.");
        return result;
    }
}
