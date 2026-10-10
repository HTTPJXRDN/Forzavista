using System.Diagnostics;

namespace ForzavistaFreeRoam;

internal sealed record NativeDrlColorStatus(DrlColorMode Mode, bool Suspended, bool NeedsReset, string Message,
    string? Hex = null, bool Strobe = false)
{
    // Stable choice snapshot; frame time is excluded so the UI need not repaint each frame.
    internal DrlColorSelection? Selection { get; init; }
}

internal static partial class NativeCarControl
{
    // All cross-feature operations acquire SignalGate before DrlColorGate.
    // Signal routes receive the authored buffers; only one tint owner writes.
    private static readonly object DrlColorGate = new();
    private static DrlColorSession? _drlColorSession;
    private static long _drlColorGeneration;
    private static string? _drlColorNotice;

    internal sealed record DrlColorPreparation(LightIdentity Identity, long Generation,
        DrlColorSelection Selection, bool Reuse,
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> Models)
    {
        internal string? UnavailableReason { get; init; }
    }

    private sealed class DrlColorSession(NativeContext context, LightIdentity identity, DrlColorRoute route,
        DrlColorSelection selection)
    {
        internal NativeContext Context { get; } = context;
        internal LightIdentity Identity { get; } = identity;
        internal DrlColorRoute Route { get; } = route;
        internal DrlColorSelection Selection { get; set; } = selection;
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal double RainbowPhaseOffsetSeconds { get; set; }
        internal double StrobeStartedAtSeconds { get; set; }
        internal bool Suspended { get; set; }
        internal bool Faulted { get; set; }
        internal bool FrameDirty { get; set; }
        internal NativeDrlStrobeSource? StrobeSource { get; set; }
    }

    internal static bool HasDrlColorProfile(string? car) => !string.IsNullOrWhiteSpace(car) &&
        DrlColorProfileCatalog.ForCar(car).Count != 0;

    internal static void InvalidateDrlColorPreparation()
    {
        lock (DrlColorGate) _drlColorGeneration++;
    }

    // Query-only preparation. Every pointer is revalidated in the mutation phase.
    internal static DrlColorPreparation PrepareDrlColor(DrlColorSelection selection, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (selection.Mode == DrlColorMode.Hazards && !LightingFeaturePolicy.ExperimentalSignalsEnabled)
            throw new InvalidOperationException(LightingFeaturePolicy.SignalsDeferredMessage);
        using var context = Locate(action: false);
        var identity = ValidateLightIdentity(context);
        if (DrlBuildMapping.ForProfile(context.Profile) is null)
            throw new InvalidOperationException("DRL colors are not mapped for this game build.");
        long generation;
        bool reuse;
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is { } previous &&
                (previous.Identity != identity || !IsCurrentDrlColorScene(context, previous)))
                DiscardDrlColor("Car/render scene changed. Previous color addresses left untouched; restoration was not verified.");
            if (_signalSession is not null)
                throw new InvalidOperationException("Turn indicators OFF before selecting a DRL color.");
            if (_drlColorSession is { Faulted: true } fault && fault.Identity == identity)
                throw new InvalidOperationException("Use RESET STATE before retrying DRL colors.");
            generation = _drlColorGeneration;
            reuse = _drlColorSession?.Identity == identity;
        }
        var profiles = DrlColorProfileCatalog.ForCar(identity.Car);
        if (selection.Mode != DrlColorMode.Original && profiles.Count == 0)
            throw new InvalidOperationException("This car's DRL color material is not mapped yet.");
        if (selection.Strobe && selection.NativeStrobe &&
            !CaptureNativeDrlStrobeSource(context, identity).EngineRunning)
            return new(identity, generation, selection, reuse, [])
            { UnavailableReason = NativeDrlStrobeSource.EngineOffMessage };
        IReadOnlyList<(ulong Wrapper, ulong Instance, ulong Resource, string Path)> models = [];
        if (selection.Mode != DrlColorMode.Original && !reuse)
        {
            var paths = profiles.Select(p => @"Game:\Media\cars\" + identity.Car + "\\" +
                p.Path.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            models = DiscoverSignalModels(context, identity, cancellationToken: cancellation, drlColorPaths: paths);
            if (models.Count == 0)
                throw new InvalidOperationException("Current-player DRL color materials were not found.");
        }
        cancellation.ThrowIfCancellationRequested();
        if (ValidateLightIdentity(context) != identity)
            throw new InvalidOperationException("Car/scene changed during DRL color preparation.");
        return new(identity, generation, selection, reuse, models);
    }

    internal static string InspectDrlColorRoute(CancellationToken cancellation)
    {
        var prepared = PrepareDrlColor(DrlColorSelection.Fixed(255, 255, 255), cancellation);
        if (prepared.Reuse) throw new InvalidOperationException("Restore the active DRL color before inspecting a new route.");
        using var context = Locate(action: false);
        var identity = ValidateLightIdentity(context);
        if (DrlBuildMapping.ForProfile(context.Profile) is null || identity != prepared.Identity)
            throw new InvalidOperationException("Car/scene changed during DRL color inspection.");
        var route = new DrlColorRoute((address, length) => Read(context.Handle, address, length),
            (_, _) => throw new InvalidOperationException("Read-only inspection cannot write."),
            () => ValidateLightIdentity(context) == identity,
            (address, length) => MappedRenderMemory.IsWritable(context.Handle, address, length),
            identity.Module, identity.Vehicle, identity.Component, identity.Car, prepared.Models,
            DrlColorProfileCatalog.ForCar(identity.Car), build: DrlBuildMapping.ForProfile(context.Profile));
        cancellation.ThrowIfCancellationRequested();
        route.Validate();
        return $"READ ONLY: {identity.Car}; {route.Count} rooted DRL material(s), complete RGB ownership guards passed. No writes.";
    }

    internal static NativeLightActionResult ApplyDrlColor(DrlColorPreparation preparation, CancellationToken cancellation)
    {
        if (preparation.UnavailableReason is { } unavailable) return new(false, unavailable);
        if (preparation.Selection.Mode == DrlColorMode.Hazards && !LightingFeaturePolicy.ExperimentalSignalsEnabled)
            return new(false, LightingFeaturePolicy.SignalsDeferredMessage);
        lock (SignalGate)
        lock (DrlColorGate)
        {
            NativeContext? context = null;
            var mutationStarted = false;
            var createdSession = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                context = Locate(action: true);
                var identity = ValidateLightIdentity(context);
                if (DrlBuildMapping.ForProfile(context.Profile) is null || identity != preparation.Identity ||
                    preparation.Generation != _drlColorGeneration || _signalSession is not null)
                    return new(false, "Car/scene or lighting state changed during color preparation; try again.");
                if (_drlColorSession is { } previous &&
                    (previous.Identity != identity || !IsCurrentDrlColorScene(context, previous)))
                    DiscardDrlColor("Car/render scene changed. Previous color addresses left untouched; restoration was not verified.");
                if (preparation.Selection.Mode == DrlColorMode.Original)
                {
                    cancellation.ThrowIfCancellationRequested();
                    mutationStarted = true;
                    return RestoreDrlColorCore();
                }
                if (_drlColorSession is { Faulted: true })
                    return new(false, "Use RESET STATE before retrying DRL colors.");
                if (preparation.Reuse != (_drlColorSession is not null))
                    return new(false, "DRL color ownership changed during preparation; try again.");
                if (_drlColorSession is null)
                {
                    var capturedContext = context;
                    var route = new DrlColorRoute((address, length) => Read(capturedContext.Handle, address, length),
                        (address, bytes) => Write(capturedContext.Handle, address, bytes),
                        () => ValidateLightIdentity(capturedContext) == identity,
                        (address, length) => MappedRenderMemory.IsWritable(capturedContext.Handle, address, length),
                        identity.Module, identity.Vehicle, identity.Component, identity.Car, preparation.Models,
                        DrlColorProfileCatalog.ForCar(identity.Car), build: DrlBuildMapping.ForProfile(context.Profile));
                    cancellation.ThrowIfCancellationRequested();
                    _drlColorSession = new(capturedContext, identity, route, preparation.Selection);
                    context = null; // Retain the handle for guarded updates and partial-write cleanup.
                    createdSession = true;
                }
                var session = _drlColorSession;
                cancellation.ThrowIfCancellationRequested();
                if (preparation.Selection.Strobe && preparation.Selection.NativeStrobe)
                {
                    session.StrobeSource ??= CaptureNativeDrlStrobeSource(session.Context, session.Identity);
                    session.StrobeSource.Validate();
                    if (!session.StrobeSource.EngineRunning)
                    {
                        if (createdSession) DiscardDrlColor(NativeDrlStrobeSource.EngineOffMessage);
                        return new(false, NativeDrlStrobeSource.EngineOffMessage);
                    }
                }
                mutationStarted = true;
                var previousSelection = session.Selection;
                if (createdSession || !previousSelection.HasSameBaseColor(preparation.Selection))
                {
                    session.Clock.Restart();
                    session.RainbowPhaseOffsetSeconds = 0;
                    session.StrobeStartedAtSeconds = 0;
                }
                else
                {
                    // A strobe-only toggle preserves the running rainbow hue.
                    session.Clock.Start();
                    if (!previousSelection.Strobe && preparation.Selection.Strobe)
                        session.StrobeStartedAtSeconds = session.Clock.Elapsed.TotalSeconds;
                    else if (previousSelection.Strobe && preparation.Selection.Strobe &&
                        previousSelection.StrobeFlashesPerSecond != preparation.Selection.StrobeFlashesPerSecond)
                    {
                        double elapsed = session.Clock.Elapsed.TotalSeconds;
                        session.StrobeStartedAtSeconds = elapsed - previousSelection.RemapStrobeElapsed(
                            Math.Max(0, elapsed - session.StrobeStartedAtSeconds),
                            preparation.Selection.StrobeFlashesPerSecond);
                    }
                }
                session.Selection = preparation.Selection;
                session.Suspended = false;
                ApplyDrlColorFrame(session);
                _drlColorGeneration++;
                _drlColorNotice = null;
                SessionLog.Write("drl_color_on", $"mode={session.Selection.Mode}; cycleSeconds={session.Selection.RainbowPeriodSeconds}; brightnessPercent={session.Selection.BrightnessPercent}; strobe={session.Selection.Strobe}; nativeStrobe={session.Selection.NativeStrobe}; strobeFlashesPerSecond={session.Selection.StrobeFlashesPerSecond}; materials={session.Route.Count}",
                    gamePid: identity.Pid, carToken: identity.Car, component: identity.Component);
                return new(true, session.Selection.Mode == DrlColorMode.Hazards
                    ? "DRL hazards ON: synchronized amber flashes on mapped DRLs only. FRONT DRL must be ON."
                    : session.Selection.Strobe
                    && session.Selection.NativeStrobe ? "DRL native strobe ON: synchronized game timing. FRONT DRL and engine must be ON."
                    : session.Selection.Strobe
                    ? $"DRL strobe ON: {session.Selection.StrobeFlashesPerSecond:0} flashes per second. FRONT DRL must be ON."
                    : session.Selection.IsCycling
                    ? "DRL color cycle ON. Adjust DRL brightness to balance color and glow."
                    : $"DRL color applied: #{session.Selection.R:X2}{session.Selection.G:X2}{session.Selection.B:X2}.");
            }
            catch (OperationCanceledException error) when (!mutationStarted)
            {
                if (createdSession)
                {
                    if (_drlColorSession?.Route.HasWrites == true) return HandleDrlColorFailure(error);
                    DiscardDrlColor("DRL color preparation canceled; no color change applied.");
                }
                return new(false, "DRL color preparation canceled; no color change applied.");
            }
            catch (Exception error) { return HandleDrlColorFailure(error); }
            finally { context?.Dispose(); }
        }
    }

    // Configuration only: no process reads/writes, route discovery or clock restart.
    // The next normal guarded frame applies the new speed, including after signals release it.
    internal static void SetDrlRainbowPeriod(double periodSeconds)
    {
        if (!double.IsFinite(periodSeconds) || periodSeconds < 1 || periodSeconds > 120)
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is not { Faulted: false, Selection: { IsCycling: true } } session ||
                session.Selection.RainbowPeriodSeconds == periodSeconds) return;
            double elapsed = session.Clock.Elapsed.TotalSeconds;
            double oldPeriod = session.Selection.RainbowPeriodSeconds;
            double phase = Math.Max(0, elapsed + session.RainbowPhaseOffsetSeconds) % oldPeriod / oldPeriod;
            session.RainbowPhaseOffsetSeconds = phase * periodSeconds - elapsed;
            session.Selection = session.Selection.WithRainbowPeriod(periodSeconds);
            _drlColorGeneration++;
        }
    }

    // Configuration only. Fixed colors need one guarded frame after a slider
    // change; preserve rainbow/strobe clocks and wait while signals own buffers.
    internal static void SetDrlBrightness(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > DrlColorSelection.MaximumBrightnessPercent)
            throw new ArgumentOutOfRangeException(nameof(percent));
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is not { Faulted: false } session ||
                session.Selection.BrightnessPercent == percent) return;
            session.Selection = session.Selection.WithBrightness(percent);
            session.FrameDirty = true;
            _drlColorGeneration++;
        }
    }

    internal static void SetDrlStrobeSpeed(double flashesPerSecond)
    {
        DrlColorSelection.ValidateStrobeSpeed(flashesPerSecond);
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is not { Faulted: false } session ||
                session.Selection.StrobeFlashesPerSecond == flashesPerSecond) return;
            if (session.Selection.Strobe && !session.Selection.NativeStrobe)
            {
                double elapsed = session.Clock.Elapsed.TotalSeconds;
                double phaseElapsed = session.Selection.RemapStrobeElapsed(
                    Math.Max(0, elapsed - session.StrobeStartedAtSeconds), flashesPerSecond);
                session.StrobeStartedAtSeconds = elapsed - phaseElapsed;
                session.FrameDirty = true;
            }
            session.Selection = session.Selection.WithStrobeSpeed(flashesPerSecond);
            _drlColorGeneration++;
        }
    }

    private static void ApplyDrlColorFrame(DrlColorSession session, Func<bool>? shouldYield = null)
    {
        if (ValidateLightIdentity(session.Context) != session.Identity)
            throw new InvalidOperationException("DRL color car/scene changed.");
        bool native = session.Selection.Strobe && session.Selection.NativeStrobe;
        if (native)
        {
            session.StrobeSource ??= CaptureNativeDrlStrobeSource(session.Context, session.Identity);
            session.StrobeSource.Validate();
        }
        // OFF/Original cleanup does not depend on an unowned strobe definition:
        // restore our mapped trigger using the route's complete owner guards.
        if (!session.Route.SetNativeStrobe(native && session.StrobeSource!.DrlEnabled, shouldYield))
        { session.FrameDirty = true; return; }
        double elapsed = session.Clock.Elapsed.TotalSeconds;
        double rainbowElapsed = Math.Max(0, elapsed + session.RainbowPhaseOffsetSeconds);
        var (r, g, b) = session.Selection.GetFrameLinearRgb(rainbowElapsed, elapsed - session.StrobeStartedAtSeconds);
        // Native strobe49 bypasses DRL output15. Fade its owned emission colors
        // with the same front gain; other modes fade through native output15.
        if (native)
        {
            float gain = NativeStrobeFadeLevel;
            r *= gain; g *= gain; b *= gain;
        }
        session.FrameDirty = !session.Route.SetColor(r, g, b, shouldYield);
    }

    private static NativeDrlStrobeSource CaptureNativeDrlStrobeSource(NativeContext context, LightIdentity identity) =>
        new((address, length) => Read(context.Handle, address, length),
            () => ValidateLightIdentity(context) == identity, identity.Vehicle, identity.Sentinel, identity.Count);

    // Called after the light toggle has released LightGate, while the UI still
    // owns its manual slot. Reconcile the native trigger immediately; no second
    // process query is added for ordinary fixed/RGB/custom-strobe sessions.
    internal static NativeDrlColorStatus? RefreshNativeDrlStrobeAfterLights()
    {
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is not { Faulted: false, Suspended: false,
                Selection: { Strobe: true, NativeStrobe: true } }) return null;
            return UpdateDrlColor();
        }
    }

    internal static NativeDrlColorStatus GetDrlColorStatus()
    {
        lock (DrlColorGate)
        {
            var session = _drlColorSession;
            return new(session?.Selection.Mode ?? DrlColorMode.Original, session?.Suspended == true,
                session?.Faulted == true, _drlColorNotice ?? (session is null ? "Original DRL color." :
                    session.Suspended ? "Indicators take priority; selected DRL color resumes when they turn OFF." :
                    session.Selection.Mode == DrlColorMode.Hazards ? "DRL hazards: mapped DRLs only, synchronized amber at 1.5 flashes per second. FRONT DRL must be ON." :
                    session.Selection.Strobe && session.Selection.NativeStrobe ? "DRL native strobe: synchronized game timing. FRONT DRL and engine must be ON." :
                    session.Selection.Strobe ? $"DRL custom strobe: {session.Selection.StrobeFlashesPerSecond:0} flashes per second. FRONT DRL must be ON." :
                    session.Selection.IsCycling ? "DRL RGB fading." : "Custom DRL color."),
                session?.Selection.Mode == DrlColorMode.Fixed
                    ? $"#{session.Selection.R:X2}{session.Selection.G:X2}{session.Selection.B:X2}" : null,
                session?.Selection.Strobe == true) { Selection = session?.Selection };
        }
    }

    // Called with the freshly queried current-player status. Releasing tracking
    // does not dereference or restore the previous car's allocations.
    internal static void ReconcileTrackedDrlColor(int? pid, ulong? vehicle, ulong? component, string? car)
    {
        if (pid is null || vehicle is null || component is null || string.IsNullOrWhiteSpace(car)) return;
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (_drlColorSession is { } session &&
                (session.Identity.Pid != pid || session.Identity.Vehicle != vehicle ||
                 session.Identity.Component != component ||
                 !string.Equals(session.Identity.Car, car, StringComparison.OrdinalIgnoreCase)))
                DiscardDrlColor("Car changed. Choose a color for the current car; previous restoration was not verified.");
        }
    }

    private static bool IsCurrentDrlColorCar(DrlColorSession session)
    {
        // Locate through the live service root instead of following the old
        // callback/controller, which can be freed while changing cars.
        using var current = Locate(action: false);
        return ValidateLightIdentity(current) == session.Identity && IsCurrentDrlColorScene(current, session);
    }

    private static bool IsCurrentDrlColorScene(NativeContext current, DrlColorSession session) =>
        session.Route.IsCurrentScene(address =>
        {
            if (address is < 0x10000 or >= 0x800000000000) return null;
            var bytes = new byte[8];
            return ReadProcessMemory(current.Handle, (nuint)address, bytes, 8, out var length) && length == 8
                ? BitConverter.ToUInt64(bytes) : null;
        });

    // Called by the UI's nonqueued timer. There is no background worker surviving exit/reset.
    internal static NativeDrlColorStatus UpdateDrlColor() => UpdateDrlColor(null);

    internal static NativeDrlColorStatus UpdateDrlColor(Func<bool>? shouldYield)
    {
        lock (SignalGate)
        lock (DrlColorGate)
        {
            if (shouldYield?.Invoke() == true) return GetDrlColorStatus();
            if (_drlColorSession is not { } session) return GetDrlColorStatus();
            if (session.Faulted) return GetDrlColorStatus(); // Retry cleanup only on an explicit reset.
            try
            {
                if (!IsCurrentDrlColorCar(session))
                {
                    DiscardDrlColor("Car/scene changed. Choose a color for the current car; previous restoration was not verified.");
                    return GetDrlColorStatus();
                }
                if (_signalSession is not null) return GetDrlColorStatus();
                if (session.Suspended || session.FrameDirty || session.Selection.IsCycling || session.Selection.Mode == DrlColorMode.Hazards || session.Selection.Strobe)
                {
                    // Resuming can fail after a partial write; cleanup must own it.
                    session.Suspended = false;
                    session.Clock.Start(); // Continue the paused phase; do not jump over indicator time.
                    ApplyDrlColorFrame(session, shouldYield);
                }
                else session.Route.Validate();
            }
            catch (Exception error) { HandleDrlColorFailure(error); }
            return GetDrlColorStatus();
        }
    }

    // Invoked under SignalGate before a signal route captures its originals.
    private static void SuspendDrlColorForSignals()
    {
        lock (DrlColorGate)
        {
            if (_drlColorSession is not { } session || session.Suspended) return;
            if (session.Faulted) throw new InvalidOperationException("Use RESET STATE to restore DRL color first.");
            try
            {
                session.Route.Restore();
                session.Clock.Stop();
                session.Suspended = true;
                _drlColorGeneration++;
            }
            catch (Exception error)
            {
                var cleanup = HandleDrlColorFailure(error);
                throw new InvalidOperationException("DRL color could not release its lamp buffers. " + cleanup.Message, error);
            }
        }
    }

    internal static NativeLightActionResult RestoreTrackedDrlColor()
    {
        lock (SignalGate)
        lock (DrlColorGate)
        {
            _drlColorGeneration++;
            try { return RestoreDrlColorCore(); }
            catch (Exception error) { return HandleDrlColorFailure(error); }
        }
    }

    private static NativeLightActionResult RestoreDrlColorCore()
    {
        if (_drlColorSession is not { } session) return new(true, "Original DRL color unchanged.");
        if (!IsCurrentDrlColorCar(session))
        {
            DiscardDrlColor("Car/scene changed; obsolete color addresses left untouched. Previous restoration was not verified.");
            return new(false, _drlColorNotice!);
        }
        // A suspended route owns no tint edits while signal routing owns these buffers.
        if (!session.Suspended || session.Route.HasWrites) session.Route.Restore();
        DiscardDrlColor("Original DRL color restored.");
        return new(true, _drlColorNotice!);
    }

    private static NativeLightActionResult HandleDrlColorFailure(Exception error)
    {
        var notice = "DRL colors stopped: " + error.GetBaseException().Message;
        _drlColorNotice = notice;
        if (_drlColorSession is { } session)
        {
            try
            {
                if (!IsCurrentDrlColorCar(session))
                    DiscardDrlColor(notice + " Previous restoration was not verified; no stale writes.");
                else
                {
                    if (!session.Suspended || session.Route.HasWrites) session.Route.Restore();
                    DiscardDrlColor(notice + " Original color restored.");
                }
            }
            catch (Exception restoreError)
            {
                session.Faulted = true;
                _drlColorNotice += " Cleanup NOT verified: " + restoreError.GetBaseException().Message + ". Use RESET STATE.";
            }
        }
        SessionLog.Write("drl_color_stopped", _drlColorNotice);
        return new(false, _drlColorNotice ?? notice);
    }

    private static void DiscardDrlColor(string notice)
    {
        _drlColorSession?.Context.Dispose();
        _drlColorSession = null;
        _drlColorNotice = notice;
        _drlColorGeneration++;
    }
}
