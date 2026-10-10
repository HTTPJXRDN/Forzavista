namespace ForzavistaFreeRoam;

internal enum SignalMode { Off, Left, Right, Hazards }

internal sealed record NativeSignalStatus(bool DirectionalAvailable, bool HazardsAvailable,
    SignalMode Mode, string Message, bool NeedsReset = false)
{
    internal bool Available(string action) => LightingFeaturePolicy.ExperimentalSignalsEnabled && !NeedsReset &&
        (action == "signalhazards" ? HazardsAvailable : DirectionalAvailable);
}

// Pure policy: explicit tested-car allowlist, not a guess from an L/R filename.
internal static class SignalPolicy
{
    internal static (bool Directions, bool Hazards, string Description) Capabilities(string build, string car)
        => LightingFeaturePolicy.ExperimentalSignalsEnabled ? MappedCapabilities(build, car)
            : (false, false, LightingFeaturePolicy.SignalsDeferredMessage);

    // Retain the research allowlist independently of the shipped feature gate.
    internal static (bool Directions, bool Hazards, string Description) MappedCapabilities(string build, string car)
        => build != "Steam 6.461.691.0" ? (false, false, "Signals are Steam-only for this development build.")
        : car.ToUpperInvariant() switch
        {
            "ACU_RSXTYPES_02" => (true, true, "RSX: separate front/rear signals."),
            "MAZ_RX7_85" => (true, true, "1985 RX-7: stock native front/rear signals."),
            "DOD_CHARGERSRTHELLCAT_15" => (true, true, "Charger: amber fronts and red rear brake/turn lamps."),
            "ALF_GIULIAGTAM_21" => (false, true, "Giulia: FRONT hazards only; merged front emitter, independent sides/rear unverified."),
            "AUD_RS6AVANT_21" => (true, true, "RS6: amber fronts/mirrors and red rear strips; combined development trial, upgraded _b mirrors; right-mirror/selective-hazard recheck pending."),
            _ => NativeHazardProfileCatalog.ForCar(car) is not null
                ? (false, true, "Factory amber hazards: native21/20 front/rear candidate; fitted material checks required, visual verification pending.")
                : (false, false, "This car's indicator routing is not verified yet.")
        };

    internal static SignalMode Requested(string action) => action switch
    {
        "signalleft" => SignalMode.Left,
        "signalright" => SignalMode.Right,
        "signalhazards" => SignalMode.Hazards,
        _ => throw new ArgumentException("Unknown signal action.", nameof(action))
    };

    internal static SignalMode Toggle(SignalMode current, SignalMode requested)
        => current == requested ? SignalMode.Off : requested;

    internal static uint[] Outputs(SignalMode mode, bool giulia = false) => giulia ? [22u] : mode switch
    {
        SignalMode.Left => [21u], SignalMode.Right => [20u],
        SignalMode.Hazards => [20u, 21u, 22u], _ => []
    };

    internal static bool RoutesHazardTo70(SignalMode mode, string car) => mode == SignalMode.Hazards &&
        car.Equals("ACU_RSXTYPES_02", StringComparison.OrdinalIgnoreCase);
    internal static bool RoutesSignalTo70(SignalMode mode, string car) => mode != SignalMode.Off &&
        (car.Equals("AUD_RS6AVANT_21", StringComparison.OrdinalIgnoreCase) || RoutesHazardTo70(mode, car));

    internal static uint[] Outputs(SignalMode mode, string car) => mode == SignalMode.Off ? []
        : car.Equals("AUD_RS6AVANT_21", StringComparison.OrdinalIgnoreCase) ? [22u]
        : RoutesHazardTo70(mode, car) ? [22u]
        : Outputs(mode, car.Equals("ALF_GIULIAGTAM_21", StringComparison.OrdinalIgnoreCase));

    internal static byte[][] OriginalExpressions(uint output) => output switch
    {
        20 => [Convert.FromHexString("0E000000130000000E0000000C0000000000000000000000"),
               Convert.FromHexString("080000001B000000130000000F0000000000000000000000")],
        21 => [Convert.FromHexString("0E000000130000000D0000000C0000000000000000000000"),
               Convert.FromHexString("080000001B000000130000000F0000000000000000000000")],
        22 => [Convert.FromHexString("080000001B0000000C0000000F0000000000000000000000")],
        _ => throw new ArgumentOutOfRangeException(nameof(output))
    };

    internal static byte[] WithGate(byte[] original, uint output, SignalMode mode, uint source)
    {
        if (source is not (0 or 11)) throw new ArgumentOutOfRangeException(nameof(source));
        var changed = original.ToArray();
        var offset = output == 22 || mode != SignalMode.Hazards ? 8 : 12;
        BitConverter.GetBytes(source).CopyTo(changed, offset);
        return changed;
    }

    internal static string Label(string action, NativeSignalStatus status)
    {
        var requested = Requested(action);
        var label = requested == SignalMode.Left ? "LEFT INDICATOR" : requested == SignalMode.Right ? "RIGHT INDICATOR" : "HAZARDS";
        return label + ": " + (!status.Available(action) ? "—" : status.Mode == requested ? "ON" : "OFF");
    }

    internal static bool IsOwnedExpression(byte[] current, byte[] expected, byte[] original,
        uint output, SignalMode mode, bool restoring)
        => current.SequenceEqual(expected) || restoring &&
            (current.SequenceEqual(original) || current.SequenceEqual(WithGate(original, output, mode, 0)) ||
             current.SequenceEqual(WithGate(original, output, mode, 11)));
}
