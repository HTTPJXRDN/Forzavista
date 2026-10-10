namespace ForzavistaFreeRoam;

internal static partial class NativeCarControl
{
    private static Dictionary<uint, HeadlampBranch> FadeBranches(LightOwnership owner, string group) =>
        group == "front" ? owner.FrontLamps : group == "rear" ? owner.RearLamps : owner.Headlamps;

    private static IEnumerable<string> FadeGroups(bool front, bool head, bool rear)
    {
        if (front) yield return "front";
        if (head) yield return "head";
        if (rear) yield return "rear";
    }

    private static void ValidateFadeEvaluator(NativeContext context)
    {
        var mapping = DrlBuildMapping.ForProfile(context.Profile) ??
            throw new InvalidOperationException("This build's fade evaluator is not mapped.");
        ulong evaluator = context.Module + mapping.LightEvaluatorRva;
        // Exact platform-specific COPY/MUL and dispatch guards; no code patches.
        foreach (var (offset, hex) in mapping.FadeSignatures)
        {
            var expected = Convert.FromHexString(hex);
            if (!Read(context.Handle, (ulong)((long)evaluator + offset), expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("The light fade evaluator signature changed.");
        }
    }

    private static void PreflightLightFade(NativeContext context, LightOwnership owner, bool front, bool head, bool rear)
    {
        ValidateFadeEvaluator(context);
        if ((head || rear) && ReadLightByte(context, owner.Identity, 0x924E) != 1)
            throw new InvalidOperationException("Start the engine for headlight/rear fades.");
        var captured = new Dictionary<string, Dictionary<uint, HeadlampBranch>>();
        foreach (string group in FadeGroups(front, head, rear))
            if (FadeBranches(owner, group).Count == 0)
                captured.Add(group, CaptureLightGroup(context, owner.Identity, group));
        ValidateOwnedLightBytes(context, owner);
        foreach (var (group, branches) in captured)
            foreach (var (output, branch) in branches) FadeBranches(owner, group).Add(output, branch);
    }

    private static bool WriteLightInstruction(NativeContext context, LightOwnership owner,
        HeadlampBranch branch, byte[] desired, Func<bool>? yield = null) => branch.Instruction.Set(desired,
            () => Read(context.Handle, branch.Nodes[0] + 0x10, 24),
            (offset, bytes) => Write(context.Handle, branch.Nodes[0] + 0x10 + (ulong)offset, bytes),
            () => ValidateOwnedLightBytes(context, owner), yield);

    private static NativeLightActionResult BeginLightFade(NativeContext context, LightOwnership owner,
        bool front, bool head, bool rear, bool all, bool on, double seconds)
    {
        var before = ReadLightStatus(context, owner.Identity);
        double now = LightFadeClock.Elapsed.TotalSeconds;
        seconds = LightFadePolicy.Duration(seconds);
        foreach (string group in FadeGroups(front, head, rear))
        {
            var branches = FadeBranches(owner, group);
            // Reverse at the last committed brightness, not an unrendered future
            // timestamp. All operands in a group share that same level.
            float start = owner.Fades.ContainsKey(group) ? FadeLevel(branches.Values.First()) :
                (group == "front" ? before.DrlOn : group == "head" ? before.HeadlightsOn : before.RunningLightsOn) ? 1 : 0;
            var ramp = new LightFadeRamp(start, on, now, seconds, all);
            foreach (var branch in branches.Values)
                WriteLightInstruction(context, owner, branch, LightFadePolicy.Scale(branch.Originals[0], group == "front" ? 7u : 11u, start));
            owner.Fades[group] = ramp;
            if (group == "front")
            {
                Volatile.Write(ref _nativeStrobeFadeLevel, start);
                WriteOwnedLightByte(context, owner, DrlLights, 1);
            }
        }
        if (all)
        {
            // Normal branches now use the engine plus owned gain, so changing
            // the native switch cannot interrupt their fade. At completion the
            // stock manual path is returned without a second visible step.
            WriteOwnedLightByte(context, owner, ManualLights, on ? (byte)1 : (byte)0);
            WriteOwnedLightByte(context, owner, LightSelector, 0);
        }
        return new(true, $"Lights fading {(on ? "ON" : "OFF")} over {seconds:0.0}s.")
            { LightStatus = ReadLightStatus(context, owner.Identity) };
    }

    private static float FadeLevel(HeadlampBranch branch)
    {
        var original = branch.Originals[0];
        float authored = BitConverter.ToUInt32(original, 0) == 9 ? BitConverter.ToSingle(original, 20) : 1;
        return BitConverter.ToUInt32(branch.Instruction.Current, 0) == 9
            ? Math.Clamp(BitConverter.ToSingle(branch.Instruction.Current, 20) / authored, 0, 1) : 1;
    }

    private static void CompleteLightFade(NativeContext context, LightOwnership owner, string group)
    {
        var ramp = owner.Fades[group];
        var branches = FadeBranches(owner, group);
        if (group == "front")
        {
            WriteOwnedLightByte(context, owner, DrlLights, ramp.On ? (byte)1 : (byte)0);
            RestoreLampBranches(context, owner, branches);
            Volatile.Write(ref _nativeStrobeFadeLevel, 1);
        }
        else
        {
            foreach (uint output in branches.Keys.ToArray())
            {
                var branch = branches[output];
                uint source = ramp.NativeSwitch && branch.OriginalSource == 5 ? 5u : ramp.On ? 11u : 0u;
                WriteLightInstruction(context, owner, branch, HeadlampExpressionPolicy.WithNormalSource(branch.Originals[0], source));
                if (source == branch.OriginalSource) branches.Remove(output);
            }
        }
        owner.Fades.Remove(group);
    }

    private static void FinishSelectedLightFades(NativeContext context, LightOwnership owner, bool front, bool head, bool rear, bool on)
    {
        foreach (string group in FadeGroups(front, head, rear))
            if (owner.Fades.TryGetValue(group, out var ramp))
            {
                owner.Fades[group] = ramp with { On = on };
                CompleteLightFade(context, owner, group);
            }
    }

    internal static NativeLightActionResult UpdateLightFades(Func<bool>? shouldYield = null)
    {
        lock (LightGate)
        {
            if (_lightOwnership is not { } owner || owner.Fades.Count == 0) return new(true, "Lights settled.");
            try
            {
                if (shouldYield?.Invoke() == true) return new(true, "Lights fading.");
                using var context = Locate(action: true);
                var identity = ValidateLightIdentity(context);
                ReconcileLightOwnership(context, identity);
                if (_lightOwnership != owner) return new(false, "Car changed; previous fade discarded without stale writes.");
                ValidateFadeEvaluator(context);
                ValidateOwnedLightBytes(context, owner);
                double now = LightFadeClock.Elapsed.TotalSeconds;
                foreach (string group in owner.Fades.Keys.ToArray())
                {
                    if (shouldYield?.Invoke() == true) break;
                    var ramp = owner.Fades[group];
                    float level = ramp.Level(now);
                    bool written = true;
                    foreach (var branch in FadeBranches(owner, group).Values)
                    {
                        if (!WriteLightInstruction(context, owner, branch,
                            LightFadePolicy.Scale(branch.Originals[0], group == "front" ? 7u : 11u, level), shouldYield))
                        { written = false; break; }
                    }
                    if (!written) break;
                    if (group == "front") Volatile.Write(ref _nativeStrobeFadeLevel, level);
                    if (ramp.Complete(now)) CompleteLightFade(context, owner, group);
                }
                return new(true, owner.Fades.Count == 0 ? "Light fade complete." : "Lights fading.")
                    { LightStatus = owner.Fades.Count == 0 ? ReadLightStatus(context, identity) : null };
            }
            catch (Exception error)
            {
                // Stop scheduling writes. Keep ownership (including a pending
                // DWORD) so RESET/exit can restore only exact qualified bytes.
                owner.Fades.Clear();
                Volatile.Write(ref _nativeStrobeFadeLevel, 1);
                SessionLog.Write("light_fade_stopped", error.Message);
                return new(false, "Light fade stopped: " + error.Message + " Use RESET STATE.");
            }
        }
    }
}
