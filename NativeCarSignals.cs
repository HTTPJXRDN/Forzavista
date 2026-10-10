using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ForzavistaFreeRoam;

internal static partial class NativeCarControl
{
    private static readonly object SignalGate = new();
    private static SignalSession? _signalSession;
    private static string? _signalNotice;
    private static readonly SignalDiscoveryCache<LightIdentity,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)>> SignalModels = new();

    internal sealed record SignalPreparation(LightIdentity Identity, SignalMode Requested,
        SignalMode PreviousMode, IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> Models);

    internal static void InvalidateSignalDiscovery() => SignalModels.Clear();

    // This entire phase uses a READ-ONLY handle. It never owns SignalGate while
    // scanning and cannot apply/restore a route, even if cancellation races exit.
    internal static SignalPreparation PrepareSignals(string action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!LightingFeaturePolicy.ExperimentalSignalsEnabled)
            throw new InvalidOperationException(LightingFeaturePolicy.SignalsDeferredMessage);
        using var context = Locate(action: false);
        var identity = ValidateLightIdentity(context);
        var requested = SignalPolicy.Requested(action);
        var capabilities = SignalPolicy.Capabilities(context.Profile.Name, identity.Car);
        if (requested == SignalMode.Hazards ? !capabilities.Hazards : !capabilities.Directions)
            throw new InvalidOperationException(capabilities.Description);
        SignalMode previous;
        lock (SignalGate)
        {
            if (_signalSession is { Faulted: true })
                throw new InvalidOperationException("Use RESET STATE before retrying signals.");
            previous = _signalSession?.Identity == identity ? _signalSession.Mode : SignalMode.Off;
        }
        var next = SignalPolicy.Toggle(previous, requested);
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models = [];
        var clock = Stopwatch.StartNew();
        if (NeedsSignalModels(identity.Car, next))
        {
            var generation = SignalModels.Generation;
            if (SignalModels.TryGet(identity, out var cached, out var error))
            {
                if (error is not null)
                    throw new InvalidOperationException(error + " Recent failed search cached for 10 seconds; RESET STATE clears it.");
                models = cached!;
            }
            else
            {
                try
                {
                    var nativeProfile = NativeHazardProfileCatalog.ForCar(identity.Car);
                    var nativePaths = nativeProfile is null ? null : nativeProfile.Left.Concat(nativeProfile.Right)
                        .Select(p => @"Game:\Media\cars\" + identity.Car + "\\" + p.Path.Replace('/', '\\'))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    models = DiscoverSignalModels(context, identity, cancellationToken: cancellationToken, drlColorPaths: nativePaths);
                    if (models.Count == 0)
                        throw new InvalidOperationException("Current-player lamp owners were not found; no writes.");
                    cancellationToken.ThrowIfCancellationRequested();
                    SignalModels.Remember(identity, models, null, generation);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Do not preserve a failure from a player/scene that already changed.
                    if (ValidateLightIdentity(context) == identity)
                        SignalModels.Remember(identity, null, ex.GetBaseException().Message, generation);
                    throw;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ValidateLightIdentity(context) != identity)
            throw new InvalidOperationException("Car/scene changed during signal preparation; no writes.");
        SessionLog.Write("signals_prepared", $"read-only; elapsedMs={clock.ElapsedMilliseconds}; models={models.Count}",
            action, identity.Pid, identity.Car, identity.Component);
        return new(identity, requested, previous, models);
    }

    private static bool NeedsSignalModels(string car, SignalMode mode) => mode != SignalMode.Off &&
        car != "MAZ_RX7_85" && !(car == "ACU_RSXTYPES_02" && mode == SignalMode.Hazards);

    private sealed record SignalBranch(uint Output, ulong Controller, ulong Sequence,
        ulong Sentinel, ulong[] Nodes, byte[][] Original);

    private sealed class SignalSession(NativeContext context, LightIdentity identity, SignalMode mode)
    {
        internal NativeContext Context { get; } = context;
        internal LightIdentity Identity { get; } = identity;
        internal SignalMode Mode { get; } = mode;
        internal Dictionary<uint, SignalBranch> Branches { get; } = [];
        internal Dictionary<uint, uint> Gates { get; } = [];
        internal bool OutputIdTransitionAttempted { get; set; }
        internal bool Routed70 { get; set; }
        internal bool Faulted { get; set; }
        internal bool HasWrites { get; set; }
        internal Action ApplyRoute { get; set; } = () => { };
        internal Action RestoreRoute { get; set; } = () => { };
        internal Action<bool> ValidateRoute { get; set; } = _ => { };
    }

    // Heavy discovery happens only on activation, not on every timer tick.
    internal static NativeSignalStatus GetSignalStatus()
    {
        lock (SignalGate)
        {
            if (!LightingFeaturePolicy.ExperimentalSignalsEnabled && _signalSession is null)
                return new(false, false, SignalMode.Off, LightingFeaturePolicy.SignalsDeferredMessage);
            try
            {
                using var context = Locate(action: false);
                var identity = ValidateLightIdentity(context);
                var capabilities = SignalPolicy.Capabilities(context.Profile.Name, identity.Car);
                if (_signalSession is { } session)
                {
                    if (session.Identity != identity)
                        AbandonSignals("Car/scene changed. No stale signal addresses written; previous restoration was not verified. Turn signals OFF before switching cars.");
                    else if (!session.Faulted)
                    {
                        try
                        {
                            ValidateSignalSession(session, requireEngine: false);
                            session.ValidateRoute(false);
                            if (Read(session.Context.Handle, identity.Vehicle + 0x924E, 1)[0] == 0)
                                StopSignalSession();
                        }
                        catch (Exception ex) { HandleSignalFailure(ex); }
                    }
                }
                if (capabilities.Hazards && _signalSession is null)
                    _ = CaptureSignalBranches(context, identity);
                return new(capabilities.Directions, capabilities.Hazards,
                    _signalSession?.Mode ?? SignalMode.Off,
                    _signalNotice ?? capabilities.Description,
                    _signalSession?.Faulted == true);
            }
            catch (Exception ex)
            {
                if (_signalSession is not null) HandleSignalFailure(ex);
                return new(false, false, _signalSession?.Mode ?? SignalMode.Off, _signalNotice ?? ex.Message,
                    _signalSession?.Faulted == true);
            }
        }
    }

    internal static NativeLightActionResult ToggleSignals(string action, SignalPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (!LightingFeaturePolicy.ExperimentalSignalsEnabled)
            return new(false, LightingFeaturePolicy.SignalsDeferredMessage);
        var requested = SignalPolicy.Requested(action);
        lock (SignalGate)
        {
            NativeContext? context = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                context = Locate(action: true);
                var identity = ValidateLightIdentity(context);
                var currentMode = _signalSession?.Identity == identity ? _signalSession.Mode : SignalMode.Off;
                if (preparation.Identity != identity || preparation.Requested != requested ||
                    preparation.PreviousMode != currentMode)
                    return new(false, "Car/scene or signal state changed during preparation; no activation attempted.");
                var capabilities = SignalPolicy.Capabilities(context.Profile.Name, identity.Car);
                if (requested == SignalMode.Hazards ? !capabilities.Hazards : !capabilities.Directions)
                    return new(false, capabilities.Description);
                if (_signalSession is { } previous && previous.Identity != identity)
                    AbandonSignals("Previous-car signal tracking discarded; no stale addresses written.");
                var next = SignalPolicy.Toggle(_signalSession?.Mode ?? SignalMode.Off, requested);
                if (_signalSession is not null) StopSignalSession();
                if (next == SignalMode.Off)
                    return new(true, "Indicators OFF; original lamp buffers and native gates restored.");

                SuspendDrlColorForSignals();
                var session = new SignalSession(context, identity, next);
                context = null; // Session owns the handle, including partial-write recovery.
                _signalSession = session;
                foreach (var pair in CaptureSignalBranches(session.Context, identity))
                    session.Branches.Add(pair.Key, pair.Value);
                ValidateSignalSession(session, requireEngine: true);
                RequireSignalOutputsOff(session);
                // Reconstruct with fresh originals and verify every live owner,
                // descriptor and mapped buffer. Cached candidates are not proof.
                PrepareSignalRoute(session, preparation.Models);
                ValidateSignalSession(session, requireEngine: true);
                cancellationToken.ThrowIfCancellationRequested();
                session.HasWrites = true; // Routes track their own attempted DWORDs.
                var outputs = SignalPolicy.Outputs(next, identity.Car);
                var route70 = SignalPolicy.RoutesSignalTo70(next, identity.Car);
                if (route70) foreach (var output in outputs) SetSignalGate(session, output, 0);
                session.ApplyRoute();
                // Rename while gated OFF: isolated22 consumers must never see
                // an activation pulse before the native ID becomes70.
                if (route70)
                {
                    ValidateSignalSession(session, requireEngine: true);
                    session.OutputIdTransitionAttempted = true; // Track BEFORE a potentially partial write.
                    Write(session.Context.Handle, session.Branches[22].Controller, BitConverter.GetBytes(70u));
                    ValidateSignalSession(session, requireEngine: true);
                    if (!session.Routed70)
                        throw new InvalidOperationException("Owned hazard output ID did not change to 70; activation stopped.");
                }
                foreach (var output in outputs) SetSignalGate(session, output, 11);
                _signalNotice = null;
                SessionLog.Write("signals_on", $"mode={next}; guarded native sequence and tested lamp route", action,
                    identity.Pid, identity.Car, identity.Component);
                return new(true, $"{next}: ON. Click again for OFF. " + capabilities.Description +
                    " Turn OFF before changing car/scene. Development feature; Steam only.");
            }
            catch (Exception ex)
            {
                SignalModels.Clear();
                HandleSignalFailure(ex);
                return new(false, _signalNotice ?? ex.Message);
            }
            finally { context?.Dispose(); }
        }
    }

    internal static NativeLightActionResult RestoreTrackedSignals()
    {
        SignalModels.Clear();
        lock (SignalGate)
        {
            if (_signalSession is null) return new(true, _signalNotice ?? "Indicators unchanged.");
            try
            {
                StopSignalSession();
                return new(true, "Indicators OFF and original lamp routing restored.");
            }
            catch (Exception ex)
            {
                HandleSignalFailure(ex);
                return new(_signalSession is null, _signalNotice ?? ex.Message);
            }
        }
    }

    private static void AbandonSignals(string message)
    {
        _signalSession?.Context.Dispose();
        _signalSession = null;
        _signalNotice = message;
        SessionLog.Write("signals_tracking_discarded", message);
    }

    private static void HandleSignalFailure(Exception error)
    {
        var notice = "Signals stopped: " + error.GetBaseException().Message;
        _signalNotice = notice;
        if (_signalSession is not { } session) return;
        if (!session.HasWrites)
        {
            session.Context.Dispose();
            _signalSession = null;
            return;
        }
        try
        {
            if (ValidateLightIdentity(session.Context) != session.Identity)
            {
                AbandonSignals(_signalNotice + " Previous restoration not verified; no stale writes.");
                return;
            }
            StopSignalSession();
            _signalNotice = notice + " Original routing restored.";
        }
        catch (Exception restoreError)
        {
            session.Faulted = true;
            _signalNotice += " Cleanup NOT verified: " + restoreError.GetBaseException().Message +
                ". Use RESET STATE after the scene resumes; if it cannot restore, restart the game.";
        }
        SessionLog.Write("signals_safety_stop", _signalNotice, gamePid: session.Identity.Pid,
            carToken: session.Identity.Car, component: session.Identity.Component);
    }

    private static Dictionary<uint, SignalBranch> CaptureSignalBranches(NativeContext context, LightIdentity identity)
    {
        if (context.Profile.Name != "Steam 6.461.691.0")
            throw new InvalidOperationException("Signal mapping is not verified for this build.");
        var evaluator = context.Module + context.Profile.LightEvaluatorRva!.Value;
        foreach (var (offset, hex) in new (ulong, string)[]
        {
            (0x1D, "0F57C0"), (0x50, "0F11442420"),
            (0x155, "80B94E92000000"), (0x173, "F30F1144244C")
        })
            if (!Read(context.Handle, evaluator + offset, hex.Length / 2).SequenceEqual(Convert.FromHexString(hex)))
                throw new InvalidOperationException("Signal engine/zero input signature changed; no writes.");
        var live = ReadCurrentLightControllers(context, identity);
        if (live.ContainsKey(70)) throw new InvalidOperationException("Unexpected native output 70; signal routing disabled.");
        var result = new Dictionary<uint, SignalBranch>();
        foreach (var output in new uint[] { 20, 21, 22 })
        {
            if (!live.TryGetValue(output, out var controller)) throw new InvalidOperationException("Native signal controller missing.");
            var original = SignalPolicy.OriginalExpressions(output);
            var sentinel = ReadUInt64(context.Handle, controller + 0x28);
            var sequence = ReadUInt64(context.Handle, controller + 0x18);
            if (sentinel < 0x10000 || sequence < 0x10000 ||
                ReadUInt64(context.Handle, controller + 0x20) != 1 ||
                ReadUInt64(context.Handle, controller + 0x30) != (ulong)original.Length)
                throw new InvalidOperationException("Native signal sequence/layout is not verified.");
            var nodes = new ulong[original.Length];
            var node = ReadUInt64(context.Handle, sentinel);
            for (var i = 0; i < nodes.Length; i++)
            {
                if (node < 0x10000 || node == sentinel || nodes.Contains(node) ||
                    ((node + 0x10) & 3) != 0 || !Read(context.Handle, node + 0x10, 24).SequenceEqual(original[i]))
                    throw new InvalidOperationException("Native signal instructions changed; no writes.");
                nodes[i] = node;
                node = ReadUInt64(context.Handle, node);
            }
            if (node != sentinel) throw new InvalidOperationException("Native signal instruction list bounds changed.");
            result.Add(output, new(output, controller, sequence, sentinel, nodes, original));
        }
        return result;
    }

    private static void ValidateSignalSession(SignalSession session, bool requireEngine, bool restoring = false)
    {
        var context = session.Context;
        using var process = Process.GetProcessById(session.Identity.Pid);
        if (process.HasExited || !process.Responding || ValidateLightIdentity(context) != session.Identity)
            throw new InvalidOperationException("Signal car/scene/process changed; no stale writes.");
        if (requireEngine && Read(context.Handle, session.Identity.Vehicle + 0x924E, 1)[0] != 1)
            throw new InvalidOperationException("Start the engine before using signals.");
        if (!restoring && Read(context.Handle, session.Identity.Vehicle + 0x924F, 3).Any(value => value != 0))
            throw new InvalidOperationException("Native signal inputs changed outside the menu.");
        var live = ReadCurrentLightControllers(context, session.Identity);
        var ownedHazard = session.Branches[22].Controller;
        if (!SignalOutputOwnership.TryResolve(ownedHazard, session.OutputIdTransitionAttempted,
                BitConverter.ToUInt32(Read(context.Handle, ownedHazard, 4)), live, out var routed70))
            throw new InvalidOperationException("Native hazard output identity changed or collides with another output; no stale writes.");
        session.Routed70 = routed70; // Reconcile real bytes, even after a failed write or readback.
        foreach (var branch in session.Branches.Values)
        {
            var id = branch.Output == 22 && session.Routed70 ? 70u : branch.Output;
            var found = live.TryGetValue(id, out var controller);
            if (!found || controller != branch.Controller ||
                ReadUInt64(context.Handle, controller + 0x18) != branch.Sequence ||
                ReadUInt64(context.Handle, controller + 0x20) != 1 ||
                ReadUInt64(context.Handle, controller + 0x28) != branch.Sentinel ||
                ReadUInt64(context.Handle, controller + 0x30) != (ulong)branch.Nodes.Length)
                throw new InvalidOperationException("Tracked native signal owner changed.");
            var node = ReadUInt64(context.Handle, branch.Sentinel);
            for (var i = 0; i < branch.Nodes.Length; i++)
            {
                var expected = i == 0 && session.Gates.TryGetValue(branch.Output, out var source)
                    ? SignalPolicy.WithGate(branch.Original[i], branch.Output, session.Mode, source)
                    : branch.Original[i];
                var current = Read(context.Handle, node + 0x10, 24);
                if (node != branch.Nodes[i] || !SignalPolicy.IsOwnedExpression(current, expected, branch.Original[i],
                        branch.Output, session.Mode, restoring && i == 0 && session.Gates.ContainsKey(branch.Output)))
                    throw new InvalidOperationException("Tracked native signal expression changed externally.");
                node = ReadUInt64(context.Handle, node);
            }
            if (node != branch.Sentinel) throw new InvalidOperationException("Signal list links changed.");
            var value = BitConverter.ToSingle(Read(context.Handle, controller + 0x10, 4));
            if (!float.IsFinite(value) || value is < 0 or > 1.01f)
                throw new InvalidOperationException("Unexpected native blink output.");
        }
    }

    private static void SetSignalGate(SignalSession session, uint output, uint source)
    {
        ValidateSignalSession(session, requireEngine: source == 11, restoring: source == 0);
        var branch = session.Branches[output];
        var offset = output == 22 || session.Mode != SignalMode.Hazards ? 8UL : 12UL;
        session.Gates[output] = source; // Track even an attempted write.
        Write(session.Context.Handle, branch.Nodes[0] + 0x10 + offset, BitConverter.GetBytes(source));
        ValidateSignalSession(session, requireEngine: source == 11, restoring: source == 0);
    }

    private static void RequireSignalOutputsOff(SignalSession session)
    {
        if (session.Branches.Values.Any(branch => BitConverter.ToSingle(
                Read(session.Context.Handle, branch.Controller + 0x10, 4)) != 0))
            throw new InvalidOperationException("Signals must be fully OFF before routing a lamp.");
    }

    private static void StopSignalSession()
    {
        if (_signalSession is not { } session) return;
        if (!session.HasWrites)
        {
            session.Context.Dispose();
            _signalSession = null;
            return;
        }
        ValidateSignalSession(session, requireEngine: false, restoring: true);
        // Publish zero while every mapped/renamed route is still connected.
        var offOutputs = session.Gates.Count != 0 ? session.Gates.Keys.ToArray()
            : SignalPolicy.Outputs(session.Mode, session.Identity.Car);
        foreach (var output in offOutputs) SetSignalGate(session, output, 0);
        if (offOutputs.Length != 0)
        {
            var clock = Stopwatch.StartNew();
            long? zeroSince = null;
            while (clock.ElapsedMilliseconds < 1500)
            {
                ValidateSignalSession(session, requireEngine: false, restoring: true);
                if (offOutputs.All(output => BitConverter.ToSingle(Read(session.Context.Handle,
                        session.Branches[output].Controller + 0x10, 4)) == 0)) zeroSince ??= clock.ElapsedMilliseconds;
                else zeroSince = null;
                if (zeroSince is not null && clock.ElapsedMilliseconds - zeroSince >= 500) break;
                Thread.Sleep(25);
            }
            if (zeroSince is null || clock.ElapsedMilliseconds - zeroSince < 500)
                throw new InvalidOperationException("Blink outputs have not stayed OFF. Resume normal Free Roam, then RESET STATE.");
        }
        // Keep the zero gate in place until output70 is removed. An external
        // native-input change during cleanup must not republish ON to70 after
        // its sustained OFF period. Reconcile failures from the actual ID.
        if (session.Routed70)
        {
            ValidateSignalSession(session, requireEngine: false, restoring: true);
            Write(session.Context.Handle, session.Branches[22].Controller, BitConverter.GetBytes(22u));
            ValidateSignalSession(session, requireEngine: false, restoring: true);
            if (session.Routed70)
                throw new InvalidOperationException("Owned hazard output ID did not return to 22; cleanup stopped.");
        }
        // Hold zero while mapped triggers restore. Native-input changes during
        // cleanup must not relight21/20 before their mapped routes disconnect.
        Exception? routeError = null;
        try
        {
            ValidateSignalSession(session, requireEngine: false, restoring: true);
            if (offOutputs.Any(output => BitConverter.ToSingle(Read(session.Context.Handle,
                    session.Branches[output].Controller + 0x10, 4)) != 0))
                throw new InvalidOperationException("Native OFF changed before mapped restoration.");
            session.ValidateRoute(true);
            session.RestoreRoute();
        }
        catch (Exception ex) { routeError = ex; }
        foreach (var output in session.Gates.Keys.ToArray().Reverse())
        {
            ValidateSignalSession(session, requireEngine: false, restoring: true);
            var branch = session.Branches[output];
            var offset = output == 22 || session.Mode != SignalMode.Hazards ? 8UL : 12UL;
            Write(session.Context.Handle, branch.Nodes[0] + 0x10 + offset, branch.Original[0].AsSpan((int)offset, 4).ToArray());
            session.Gates.Remove(output);
            ValidateSignalSession(session, requireEngine: false, restoring: true);
        }
        // Native cleanup must not depend on mapped-buffer ownership. A route
        // conflict can stop its restoration, but cannot strand a renamed ID or
        // active native gate. A faulted session retains the route for RESET.
        ValidateSignalSession(session, requireEngine: false, restoring: true);
        if (routeError is not null)
            throw new InvalidOperationException("Native gates/output IDs restored; mapped route cleanup not verified.", routeError);
        SessionLog.Write("signals_restored", $"mode={session.Mode}; OFF published and all owned buffers/gates restored",
            gamePid: session.Identity.Pid, carToken: session.Identity.Car, component: session.Identity.Component);
        session.Context.Dispose();
        _signalSession = null;
        _signalNotice = null;
    }

    private static void PrepareSignalRoute(SignalSession session,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models)
    {
        var context = session.Context;
        var identity = session.Identity;
        if (NativeHazardProfileCatalog.ForCar(identity.Car) is not null && session.Mode == SignalMode.Hazards)
        {
            var proofs = CaptureNativeHazardProofs(context, identity, models);
            session.ValidateRoute = _ => { foreach (var proof in proofs) proof.Validate(); };
            return; // No material writes or output-ID rerouting; native21/20 drive authored bulbs.
        }
        if (identity.Car == "MAZ_RX7_85" || identity.Car == "ACU_RSXTYPES_02" && session.Mode == SignalMode.Hazards)
            return; // Visually verified native-only routes.
        byte[] ReadBytes(ulong address, int length) => Read(context.Handle, address, length);
        void WriteBytes(ulong address, byte[] bytes) => Write(context.Handle, address, bytes);
        bool SameCar() => ValidateLightIdentity(context) == identity;
        bool Writable(ulong address, int length) => MappedRenderMemory.IsWritable(context.Handle, address, length);
        var output = session.Mode == SignalMode.Left ? 21u : session.Mode == SignalMode.Right ? 20u : 22u;
        if (identity.Car == "ACU_RSXTYPES_02")
        {
            var route = new RsxSideMaterialRoute(ReadBytes, WriteBytes, SameCar, identity.Module, models,
                output, useMapped: true, writable: Writable, vehicle: identity.Vehicle);
            session.ApplyRoute = route.Apply; session.RestoreRoute = route.Restore; session.ValidateRoute = route.Validate;
        }
        else if (identity.Car == "DOD_CHARGERSRTHELLCAT_15")
        {
            var route = new ChargerDrlRoute(ReadBytes, WriteBytes, SameCar, Writable, identity.Module,
                identity.Vehicle, identity.Component, models, output, amber: true, includeFrontAndRear: true);
            session.ApplyRoute = route.Apply; session.RestoreRoute = route.Restore; session.ValidateRoute = route.Validate;
        }
        else if (identity.Car == "ALF_GIULIAGTAM_21" && session.Mode == SignalMode.Hazards)
        {
            var route = new GiuliaMergedDrlRoute(ReadBytes, WriteBytes, SameCar, Writable, identity.Module,
                identity.Vehicle, identity.Component, models, brighter: true);
            session.ApplyRoute = route.Apply; session.RestoreRoute = route.Restore; session.ValidateRoute = route.Validate;
        }
        else if (identity.Car == "AUD_RS6AVANT_21")
        {
            var route = new Rs6SignalRoute(ReadBytes, WriteBytes, SameCar, Writable, identity.Module,
                identity.Vehicle, identity.Component, models, output);
            session.ApplyRoute = route.Apply; session.RestoreRoute = route.Restore; session.ValidateRoute = route.Validate;
        }
        else throw new InvalidOperationException("No verified signal material route for this car.");
    }

    private static DrlColorRoute[] CaptureNativeHazardProofs(NativeContext context, LightIdentity identity,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models)
    {
        var profile = NativeHazardProfileCatalog.ForCar(identity.Car)
            ?? throw new InvalidOperationException("Native hazard materials are not mapped for this car.");
        return new[] { (Rows: profile.Left, Trigger: 21u), (Rows: profile.Right, Trigger: 20u) }
            .Select(side => new DrlColorRoute((address, length) => Read(context.Handle, address, length),
                (_, _) => throw new InvalidOperationException("Native hazard material proof cannot write."),
                () => ValidateLightIdentity(context) == identity,
                (address, length) => MappedRenderMemory.IsWritable(context.Handle, address, length),
                identity.Module, identity.Vehicle, identity.Component, identity.Car, models, side.Rows, side.Trigger)).ToArray();
    }

    internal static string InspectNativeHazardRoute(CancellationToken cancellation)
    {
        var prepared = PrepareSignals("signalhazards", cancellation);
        using var context = Locate(action: false);
        var identity = ValidateLightIdentity(context);
        if (identity != prepared.Identity || prepared.PreviousMode != SignalMode.Off)
            throw new InvalidOperationException("Car/scene or hazard state changed; no inspection writes.");
        _ = CaptureSignalBranches(context, identity);
        var proofs = CaptureNativeHazardProofs(context, identity, prepared.Models);
        foreach (var proof in proofs) proof.Validate();
        cancellation.ThrowIfCancellationRequested();
        return $"READ ONLY: {identity.Car}; {proofs.Sum(p => p.Count)} exact rooted native21/20 indicator materials and native gates qualified. No writes.";
    }

    private static IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> DiscoverSignalModels(
        NativeContext context, LightIdentity identity, bool inspectOwners = false,
        CancellationToken cancellationToken = default, IReadOnlySet<string>? drlColorPaths = null)
    {
        var profile = context.Profile;
        var wrapperVtable = identity.Module + (profile.CarRenderModelWrapperVtableRva ??
            throw new InvalidOperationException("Signal model wrapper is unmapped for this build."));
        var models = new List<(ulong Wrapper, ulong Instance, ulong Resource, string Path)>();
        // RS6's fixed combined route requires four main lamps and _b mirrors,
        // all from the current player's rooted Scene Pose.
        var chargerOwner = drlColorPaths is not null || identity.Car is "DOD_CHARGERSRTHELLCAT_15" or "AUD_RS6AVANT_21"
            ? ChargerRenderOwner.Capture((address, length) => Read(context.Handle, address, length),
                () => ValidateLightIdentity(context) == identity, identity.Module, identity.Vehicle, identity.Component, DrlBuildMapping.ForProfile(profile))
            : null;
        const ulong radius = 0x8000000;
        // Rooted DRL models can sit beyond the component-centered arena (M2
        // Comp, for example). Center color discovery on the validated scene
        // pose while preserving the same bounded extent and ownership checks.
        var scanAnchor = drlColorPaths is not null ? chargerOwner!.Pose : identity.Component;
        if (scanAnchor < radius + 0x10000 || scanAnchor > ulong.MaxValue - radius)
            throw new InvalidOperationException("Lamp arena bounds invalid.");
        var start = (scanAnchor & ~0xFFFFUL) - radius;
        var clock = Stopwatch.StartNew();
        var chunk = new byte[0x10000]; // Reuse one read-only buffer; no 256 MB allocation burst.
        var stop = start + radius * 2;
        var scanSteps = 0;
        IEnumerable<(ulong Start, ulong Stop)> ranges = drlColorPaths is null
            ? [(start, stop)] : SignalScanPolicy.DrlSearchRanges(chargerOwner!.Pose, identity.Component);
        foreach (var (rangeStart, rangeStop) in ranges)
        {
            // Run the fallback only when a requested model path was missed.
            if (drlColorPaths is not null && drlColorPaths.All(path => models.Any(model =>
                    string.Equals(model.Path, path, StringComparison.OrdinalIgnoreCase)))) break;
            for (ulong cursor = rangeStart; cursor < rangeStop;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= 15000) throw new InvalidOperationException("Lamp discovery timed out; no writes.");
                if ((scanSteps++ & 63) == 0 && ValidateLightIdentity(context) != identity)
                    throw new InvalidOperationException("Player changed during lamp discovery; no writes.");
                if (QueryWindowMemory(context.Handle, (nuint)cursor, out var region,
                        (nuint)Marshal.SizeOf<WindowMemoryRegion>()) != (nuint)Marshal.SizeOf<WindowMemoryRegion>() ||
                    region.Size == 0 || region.Base > cursor || region.Size > ulong.MaxValue - region.Base)
                    throw new InvalidOperationException("Lamp memory regions could not be inspected; no writes.");
                var regionEnd = Math.Min(rangeStop, region.Base + region.Size);
                if (regionEnd <= cursor) throw new InvalidOperationException("Lamp memory regions changed; no writes.");
                var readable = region.State == 0x1000 && (region.Protection & 0x100) == 0 &&
                    (region.Protection & 0xFF) is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
                if (!readable) { cursor = regionEnd; continue; }
                var chunkAddress = cursor;
                var length = (int)Math.Min((ulong)chunk.Length, regionEnd - cursor);
                cursor += (ulong)length;
                // Read only the committed range. A 64K read crossing an unreadable
                // boundary previously skipped valid M2 lamp wrappers on that page.
                if (!ReadProcessMemory(context.Handle, (nuint)chunkAddress, chunk, (nuint)length,
                        out var read) || read != (nuint)length) continue;
                foreach (var i in SignalScanPolicy.AlignedMatches(chunk, wrapperVtable, length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (models.Count >= 128) throw new InvalidOperationException("Lamp candidate budget exceeded; no writes.");
                    var wrapper = chunkAddress + (ulong)i;
                    try
                    {
                        var instance = ReadUInt64(context.Handle, wrapper + 0x60);
                        if (instance < 0x10000 || ReadUInt64(context.Handle, wrapper + 0x68) != instance - 0x10 ||
                            ReadUInt64(context.Handle, instance) != identity.Module + profile.CarModelInstanceVtableRva) continue;
                        var resource = ReadUInt64(context.Handle, instance + 0x20);
                        if (resource < 0x10000 || ReadUInt64(context.Handle, resource) != identity.Module + profile.CarModelResourceVtableRva) continue;
                        var pathBytes = Read(context.Handle, ReadUInt64(context.Handle, resource + 0x80), 512);
                        var end = Array.IndexOf(pathBytes, (byte)0);
                        if (end is <= 0 or >= 512) continue;
                        var path = Encoding.ASCII.GetString(pathBytes, 0, end).Replace('/', '\\');
                        var colorCandidate = drlColorPaths?.Contains(path) == true;
                        var exactLamp = Regex.IsMatch(path, @"\\(?:headlight|taillight)[LR](?:Bulbs)?_a\.modelbin$", RegexOptions.IgnoreCase);
                        var rs6Mirror = identity.Car == "AUD_RS6AVANT_21" &&
                            Regex.IsMatch(path, @"\\wingMirror[LR]_[ab]\.modelbin$", RegexOptions.IgnoreCase);
                        var rs6Isolation = identity.Car == "AUD_RS6AVANT_21" &&
                            (path.EndsWith(@"\BumperR\bumperrsidemarkers_b.modelbin", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(@"\Trunk\trunkLiner_a__SLOD.modelbin", StringComparison.OrdinalIgnoreCase));
                        var rs6OtherInspection = inspectOwners && identity.Car == "AUD_RS6AVANT_21" && !(exactLamp || rs6Mirror || rs6Isolation);
                        if (!path.Contains("\\" + identity.Car + "\\", StringComparison.OrdinalIgnoreCase) ||
                            (drlColorPaths is not null ? !colorCandidate :
                                !(exactLamp || rs6Mirror || rs6Isolation || rs6OtherInspection))) continue;
                        if (rs6OtherInspection && !chargerOwner!.MatchesInstance(instance)) continue;
                        // Charger uses a direct player scene -> lamp pose chain.
                        // Historical guards for other experimental routes are separate.
                        var owner = ReadUInt64(context.Handle, instance + 0x38);
                        var presentation = ReadUInt64(context.Handle, identity.Vehicle + 0x7918);
                        if (inspectOwners)
                        {
                            if (chargerOwner is not null)
                            {
                                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "charger-rooted-owner-comparison",
                                    File = System.IO.Path.GetFileName(path), Wrapper = wrapper, Instance = instance,
                                    Pose = owner, CurrentPlayerScenePose = chargerOwner.Pose,
                                    MatchesCurrentPlayerScene = chargerOwner.MatchesInstance(instance) }));
                                models.Add((wrapper, instance, resource, path)); // Read-only comparison, not a write route.
                                continue;
                            }
                            var ownerContext = ReadUInt64(context.Handle, owner + 0x18);
                            var bindings = ReadUInt64(context.Handle, presentation + 0x18);
                            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "signal-owner-comparison",
                                File = System.IO.Path.GetFileName(path), Wrapper = wrapper, Instance = instance,
                                Owner = owner, OwnerContext = ownerContext, CurrentComponent = identity.Component,
                                OwnerVtable = ReadUInt64(context.Handle, owner) - identity.Module,
                                ContextComponentControl = ReadUInt64(context.Handle, ownerContext + 0x50),
                                ActiveBindingOwner = ReadUInt64(context.Handle, bindings + 0x90),
                                ActiveBindingControl = ReadUInt64(context.Handle, bindings + 0x98),
                                ContextVtable = ReadUInt64(context.Handle, ownerContext) - identity.Module }));
                            models.Add((wrapper, instance, resource, path)); // Diagnostic candidates only, never a production route.
                            continue;
                        }
                        if (ReadUInt64(context.Handle, owner) != identity.Module + (DrlBuildMapping.ForProfile(profile) ?? DrlBuildMapping.Steam).PoseVtableRva) continue;
                        if (chargerOwner is not null)
                        {
                            if (!chargerOwner.MatchesInstance(instance)) continue;
                        }
                        else if (identity.Car == "ACU_RSXTYPES_02")
                        {
                            var bindings = ReadUInt64(context.Handle, presentation + 0x18);
                            if (ReadUInt64(context.Handle, bindings + 0x60) != owner + 0x20 ||
                                ReadUInt64(context.Handle, owner + 0x20) != bindings + 0x60) continue;
                        }
                        else
                        {
                            var ownerContext = ReadUInt64(context.Handle, owner + 0x18);
                            if (ReadUInt64(context.Handle, ownerContext + 0x50) != identity.Component - 0x10) continue;
                        }
                        models.Add((wrapper, instance, resource, path));
                    }
                    catch { /* Read-only candidate rejected; never used for a write. */ }
                    if (models.Count > 128) throw new InvalidOperationException("Lamp candidate budget exceeded; no writes.");
                }
            }
        }
        if (ValidateLightIdentity(context) != identity) throw new InvalidOperationException("Player changed during lamp discovery.");
        chargerOwner?.Validate();
        return models;
    }

    // Explicit test/diagnostic entry point. Constructs every route and executes
    // all guards with a READ-ONLY process handle; Apply/Restore are never called.
    internal static string InspectSignalRoute(string action)
    {
        lock (SignalGate)
        {
            if (_signalSession is not null) throw new InvalidOperationException("Stop active signals before read-only preflight.");
            using var context = Locate(action: false);
            var identity = ValidateLightIdentity(context);
            var mode = SignalPolicy.Requested(action);
            var capabilities = SignalPolicy.Capabilities(context.Profile.Name, identity.Car);
            if (mode == SignalMode.Hazards ? !capabilities.Hazards : !capabilities.Directions)
                throw new InvalidOperationException(capabilities.Description);
            var session = new SignalSession(context, identity, mode);
            foreach (var branch in CaptureSignalBranches(context, identity)) session.Branches.Add(branch.Key, branch.Value);
            ValidateSignalSession(session, requireEngine: true);
            RequireSignalOutputsOff(session);
            var models = NeedsSignalModels(identity.Car, mode) ? DiscoverSignalModels(context, identity) : [];
            PrepareSignalRoute(session, models);
            session.ValidateRoute(false);
            ValidateSignalSession(session, requireEngine: true);
            return $"READ ONLY: {context.Profile.Name}; {identity.Car}; {mode}; all native/material guards passed; no game writes.";
        }
    }

    internal static void InspectSignalOwnership()
    {
        using var context = Locate(action: false);
        var identity = ValidateLightIdentity(context);
        if (context.Profile.Name != "Steam 6.461.691.0") throw new InvalidOperationException("Unmapped signal build.");
        _ = DiscoverSignalModels(context, identity, inspectOwners: true);
        Console.WriteLine("READ ONLY: lamp candidates compared; Charger uses its rooted player scene pose; no game writes.");
    }
}
