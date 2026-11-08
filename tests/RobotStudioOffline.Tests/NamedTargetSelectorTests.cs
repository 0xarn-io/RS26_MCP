using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddNamedTargetSelectorTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("pDetached declaration without a graphical target resolves only with an explicit frame", () =>
        {
            NamedTargetCandidate[] declarations = [new("pReference", "Main/pReference"), new("pDetached", "Main/pDetached")];
            var selected = NamedTargetSelector.Select("pDetached", "wobjBench", [], declarations);
            Equal(TargetSource.Declaration, selected.Source);
            Equal(1, selected.Index);
            RejectSelection("pDetached", null, [], declarations, "missing_frame");
            RejectSelection("pDetached", " ", [], declarations, "missing_frame");
        }));
        tests.Add(("existing graphical binding takes priority over duplicate declarations without requiring a frame", () =>
        {
            NamedTargetCandidate[] graphical = [new("pReference", "Main/pReference", "wobj0"), new("pDetached", "Main/pDetached", "wobjBench")];
            NamedTargetCandidate[] declarations = [new("pDetached", "Main/pDetached"), new("pDetached", "Other/pDetached")];
            var selected = NamedTargetSelector.Select("pDetached", null, graphical, declarations);
            Equal(TargetSource.Graphical, selected.Source);
            Equal(1, selected.Index);
        }));
        tests.Add(("graphical and declaration aliases preserve existing named target behavior", () =>
        {
            NamedTargetCandidate[] graphical = [new("pDetached", "Main/pDetached", "wobjBench", "Frames/wobjBench", "Reference marker")];
            foreach (var name in new[] { "pDetached", "Main/pDetached", "Reference marker" })
            foreach (var frame in new[] { "wobjBench", "Frames/wobjBench" })
                Equal(new NamedTargetSelection(TargetSource.Graphical, 0), NamedTargetSelector.Select(name, frame, graphical, []));
        }));
        tests.Add(("requested frame selects among graphical bindings and retains original indexes", () =>
        {
            NamedTargetCandidate[] graphical = [new("pOther", "Main/pOther", "wobjBench"),
                new("pDetached", "Main/pDetached", "wobj0", "Frames/wobj0"),
                new("pDetached", "Main/pDetached", "wobjBench", "Frames/wobjBench")];
            Equal(new NamedTargetSelection(TargetSource.Graphical, 2),
                NamedTargetSelector.Select("pDetached", "Frames/wobjBench", graphical, []));
            RejectSelection("pDetached", null, graphical, [], "target_selection");
        }));
        tests.Add(("wrong graphical work object never falls back to declaration reinterpretation", () =>
        {
            NamedTargetCandidate[] graphical = [new("pDetached", "Main/pDetached", "wobjFixture", "Frames/wobjFixture", "Reference marker")];
            NamedTargetCandidate[] declarations = [new("pDetached", "Main/pDetached")];
            foreach (var name in new[] { "pDetached", "Main/pDetached", "Reference marker" })
            {
                var error = RejectSelection(name, "wobj0", graphical, declarations, "target_selection");
                True(error.Message.Contains("not be reinterpreted", StringComparison.Ordinal));
            }
        }));
        tests.Add(("ambiguous graphical bindings cannot be bypassed by a unique declaration", () =>
        {
            NamedTargetCandidate[] graphical = [new("pDetached", "Main/pDetached", "wobj0"), new("pDetached", "Main/pDetached", "wobj0")];
            RejectSelection("pDetached", "wobj0", graphical, [new("pDetached", "Main/pDetached")], "target_selection");
        }));
        tests.Add(("qualified declaration names disambiguate modules without inferring a frame", () =>
        {
            NamedTargetCandidate[] declarations = [new("pDetached", "Main/pDetached"), new("pDetached", "Maintenance/pDetached")];
            RejectSelection("pDetached", "wobj0", [], declarations, "target_selection");
            Equal(new NamedTargetSelection(TargetSource.Declaration, 1),
                NamedTargetSelector.Select("Maintenance/pDetached", "wobjBench", [], declarations));
            RejectSelection("Maintenance/pDetached", null, [], declarations, "missing_frame");
        }));
        tests.Add(("missing and case-mismatched targets never choose an unrelated declaration", () =>
        {
            NamedTargetCandidate[] graphical = [new("pReference", "Main/pReference", "wobj0")];
            NamedTargetCandidate[] declarations = [new("pDetached", "Main/pDetached")];
            RejectSelection("pMissing", "wobj0", graphical, declarations, "target_selection");
            RejectSelection("PDetached", "wobj0", graphical, declarations, "target_selection");
            RejectSelection("pMissing", null, graphical, declarations, "target_selection");
        }));
    }

    private static BridgeException RejectSelection(string name, string? workObject,
        NamedTargetCandidate[] graphical, NamedTargetCandidate[] declarations, string code)
    {
        try { NamedTargetSelector.Select(name, workObject, graphical, declarations); }
        catch (BridgeException error)
        {
            Equal(code, error.Code);
            Equal(409, error.StatusCode);
            return error;
        }
        throw new Exception("Expected named target selection to fail.");
    }
}
