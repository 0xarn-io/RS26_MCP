namespace RobotStudioOffline.Core;

/// <summary>Name and frame aliases observed in one robot's station task.</summary>
public sealed record NamedTargetCandidate(string Name, string QualifiedName, string? WorkObject = null,
    string? QualifiedWorkObject = null, string? GraphicalName = null);

public enum TargetSource { Graphical, Declaration }

/// <summary>Index into the original candidate list for the selected source.</summary>
public sealed record NamedTargetSelection(TargetSource Source, int Index);

/// <summary>Selects existing graphical targets before considering unattached robtarget declarations.</summary>
public static class NamedTargetSelector
{
    public static NamedTargetSelection Select(string requestedName, string? requestedWorkObject,
        IReadOnlyList<NamedTargetCandidate> graphical, IReadOnlyList<NamedTargetCandidate> declarations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        ArgumentNullException.ThrowIfNull(graphical);
        ArgumentNullException.ThrowIfNull(declarations);

        var graphicalMatches = Enumerable.Range(0, graphical.Count)
            .Where(index => MatchesName(graphical[index], requestedName)).ToArray();
        if (graphicalMatches.Length != 0)
        {
            var matches = graphicalMatches.Where(index => requestedWorkObject is null ||
                graphical[index].WorkObject == requestedWorkObject ||
                graphical[index].QualifiedWorkObject == requestedWorkObject).ToArray();
            if (matches.Length == 0)
                throw new BridgeException("target_selection",
                    "A graphical station target matches the name, but not the requested workObject. " +
                    "Its robtarget will not be reinterpreted in another frame.", 409);
            if (matches.Length != 1)
                throw new BridgeException("target_selection",
                    "Target is ambiguous; specify its Module/name and workObject.", 409);
            return new(TargetSource.Graphical, matches[0]);
        }

        var declarationMatches = Enumerable.Range(0, declarations.Count)
            .Where(index => MatchesName(declarations[index], requestedName)).ToArray();
        if (declarationMatches.Length == 0)
            throw new BridgeException("target_selection",
                "No matching graphical target or robtarget declaration exists in the robot's station task.", 409);
        if (declarationMatches.Length != 1)
            throw new BridgeException("target_selection",
                "Robtarget declaration is ambiguous; specify its Module/name.", 409);
        if (string.IsNullOrWhiteSpace(requestedWorkObject))
            throw new BridgeException("missing_frame",
                "The named robtarget declaration has no graphical target binding. Specify workObject explicitly; no frame is assumed.", 409);
        return new(TargetSource.Declaration, declarationMatches[0]);
    }

    private static bool MatchesName(NamedTargetCandidate candidate, string requestedName) =>
        candidate.Name == requestedName || candidate.QualifiedName == requestedName || candidate.GraphicalName == requestedName;
}
