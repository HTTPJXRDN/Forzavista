namespace ForzavistaFreeRoam;

// Pure selection logic for keyboard/controller groups; no game access here.
internal static class BindingActionPlanner
{
    internal static string[] Plan(
        IReadOnlyList<string> actions,
        IReadOnlyList<(string OpenAction, string CloseAction)> parts,
        IReadOnlyCollection<string> openPanels,
        IReadOnlyCollection<string> openWindows,
        IReadOnlyCollection<string>? availableWindows = null)
    {
        var distinct = actions.Select(action =>
                action.Equals("explode", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("implode", StringComparison.OrdinalIgnoreCase)
                    ? "toggleall" : action)
            .Where(action => LightingFeaturePolicy.ExperimentalSignalsEnabled ||
                !LightingFeaturePolicy.IsSignalAction(action))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allToggle = distinct.Contains("toggleall", StringComparer.OrdinalIgnoreCase);
        var lightingActions = distinct.Where(action => action.Equals("lightsall", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("lightsdrl", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("lightsrear", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("lightsheadlights", StringComparison.OrdinalIgnoreCase)).Select(action => action.ToLowerInvariant()).ToArray();
        var lightGroup = lightingActions.Length > 1;
        var emittedLights = false;
        var signalActions = distinct.Where(action => action.Equals("signalleft", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("signalright", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("signalhazards", StringComparison.OrdinalIgnoreCase)).ToArray();
        var emittedSignals = false;
        bool IsWindow((string OpenAction, string CloseAction) part) =>
            part.OpenAction.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase);
        bool IsAvailable((string OpenAction, string CloseAction) part) =>
            !IsWindow(part) || availableWindows is null ||
            availableWindows.Contains(part.OpenAction["openwindow".Length..],
                StringComparer.OrdinalIgnoreCase);
        bool Matches((string OpenAction, string CloseAction) part, string action) =>
            action.Equals("toggle" + part.OpenAction[4..], StringComparison.OrdinalIgnoreCase) ||
            action.Equals(part.OpenAction, StringComparison.OrdinalIgnoreCase) ||
            action.Equals(part.CloseAction, StringComparison.OrdinalIgnoreCase);
        var groupedParts = parts.Where(part =>
            (!allToggle || IsWindow(part)) && IsAvailable(part) &&
            (distinct.Contains("toggle" + part.OpenAction[4..], StringComparer.OrdinalIgnoreCase) ||
             (distinct.Contains(part.OpenAction, StringComparer.OrdinalIgnoreCase) &&
              distinct.Contains(part.CloseAction, StringComparer.OrdinalIgnoreCase)))).ToArray();

        bool IsOpen((string OpenAction, string CloseAction) part) =>
            part.OpenAction.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase)
                ? openWindows.Contains(part.OpenAction["openwindow".Length..],
                    StringComparer.OrdinalIgnoreCase)
                : openPanels.Contains(part.OpenAction[4..], StringComparer.OrdinalIgnoreCase);

        // For a shared gesture, a partly open group becomes fully open first;
        // only a fully open group closes on the next press.
        var openGroup = groupedParts.Any(part => !IsOpen(part));
        var emittedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        foreach (var action in distinct)
        {
            if (signalActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                // One gesture cannot activate opposing exclusive modes twice.
                // Left+right or an explicit hazard binding becomes one toggle.
                if (!emittedSignals) selected.Add(signalActions.Length > 1 ? "signalhazards" : action.ToLowerInvariant());
                emittedSignals = true;
                continue;
            }
            if (lightGroup && lightingActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                // ALL LIGHTS dominates if bound explicitly. Otherwise align
                // only the selected circuits; never turn on unbound rear lamps.
                if (!emittedLights) selected.Add(lightingActions.Contains("lightsall") ? "lightsall" :
                    "lightsgroup:" + string.Join(",", lightingActions));
                emittedLights = true;
                continue;
            }
            if (parts.Any(part => IsWindow(part) && !IsAvailable(part) && Matches(part, action)))
                continue; // Some cars have no rear (or other) side-window meshes.
            if (allToggle && parts.Any(part => !IsWindow(part) && Matches(part, action)))
                continue; // The all-panel command owns those parts for this gesture.
            var pair = groupedParts.FirstOrDefault(part => Matches(part, action));
            if (pair.OpenAction is null) selected.Add(action);
            else if (emittedPairs.Add(pair.OpenAction))
                selected.Add(openGroup ? pair.OpenAction : pair.CloseAction);
        }
        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
