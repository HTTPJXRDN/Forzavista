namespace ForzavistaFreeRoam;

// Release builds omit experimental signals until front/rear coverage is ready.
// Research code and restoration remain available for a deliberate future build.
internal static class LightingFeaturePolicy
{
    internal static bool ExperimentalSignalsEnabled =>
#if EXPERIMENTAL_SIGNALS
        true;
#else
        false;
#endif

    internal const string SignalsDeferredMessage = "Hazards and turn signals are deferred until front/rear coverage is complete.";

    internal static bool IsSignalAction(string action) =>
        action.Equals("signalleft", StringComparison.OrdinalIgnoreCase) ||
        action.Equals("signalright", StringComparison.OrdinalIgnoreCase) ||
        action.Equals("signalhazards", StringComparison.OrdinalIgnoreCase);
}
