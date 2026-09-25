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
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allToggle = distinct.Contains("toggleall", StringComparer.OrdinalIgnoreCase);
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
