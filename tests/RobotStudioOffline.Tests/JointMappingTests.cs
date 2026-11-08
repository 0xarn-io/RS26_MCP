using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddJointMappingTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("four-axis input expands to six RAPID fields", () =>
        {
            var expanded = JointMapping.ToRapidAxes([Math.PI / 5, 0, 0, 0], [1, 2, 3, 6]);
            SequenceNear([36, 0, 0, 0, 0, 0], expanded);
        }));
        tests.Add(("fourth compact joint maps to requested physical axis six", () =>
        {
            var expanded = JointMapping.ToRapidAxes([Math.PI / 2, Math.PI / 12, -Math.PI / 9, Math.PI / 4], [1, 2, 3, 6]);
            SequenceNear([90, 15, -20, 0, 0, 45], expanded);
            SequenceNear([Math.PI / 2, Math.PI / 12, -Math.PI / 9, Math.PI / 4],
                JointMapping.FromRapidAxes(expanded, [1, 2, 3, 6]));
        }));
        tests.Add(("joint mapping preserves supplied order rather than sorting or assuming a model", () =>
        {
            var source = new[] { -Math.PI / 4, Math.PI / 2, Math.PI / 6 };
            var mapping = new[] { 6, 1, 3 };
            SequenceNear([90, 0, 30, 0, 0, -45], JointMapping.ToRapidAxes(source, mapping));
            SequenceNear(source, JointMapping.FromRapidAxes(JointMapping.ToRapidAxes(source, mapping), mapping));
        }));
        tests.Add(("inactive RAPID axes reject a requested movement", () =>
        {
            var error = Throws<BridgeException>(() => JointMapping.FromRapidAxes([36, 0, 0, 12, 0, 0], [1, 2, 3, 6]));
            Equal("inactive_joint", error.Code);
            True(error.Message.Contains("axis 4"));
            SequenceNear([Math.PI / 5, 0, 0, 0], JointMapping.FromRapidAxes([36, 0, 0, 1e-10, 0, 0], [1, 2, 3, 6]));
        }));
        tests.Add(("joint mapping refuses invalid maps and inconsistent SDK vectors", () =>
        {
            foreach (var mapping in new int[][] { [], [0], [7], [1, 1], [1, 2, 3, 4, 5, 6, 7] })
                Equal("unsupported_mechanism", Throws<BridgeException>(() => JointMapping.ValidateMap(mapping, mapping.Length)).Code);
            Equal("unsupported_mechanism", Throws<BridgeException>(() => JointMapping.ToRapidAxes([0, 0, 0], [1, 2, 3, 6])).Code);
            Equal("unsupported_mechanism", Throws<BridgeException>(() => JointMapping.ValidateMap([1, 2, 3, 6], 6)).Code);
            Equal("invalid_sdk_solution", Throws<BridgeException>(() => JointMapping.ToRapidAxes([double.NaN], [1])).Code);
            Equal("invalid_sdk_solution", Throws<BridgeException>(() => JointMapping.ToRapidAxes([double.MaxValue], [1])).Code);
            Equal("invalid_request", Throws<BridgeException>(() => JointMapping.FromRapidAxes([0, 0, 0, 0], [1, 2, 3, 6])).Code);
            Equal("invalid_request", Throws<BridgeException>(() => JointMapping.FromRapidAxes([0, 0, 0, 0, 0, double.PositiveInfinity], [1, 2, 3, 6])).Code);
        }));
        tests.Add(("six-axis mapping preserves all values and does not mutate inputs", () =>
        {
            var source = new[] { 90.0, -30, 45, 180, -180, 360 };
            var original = source.ToArray();
            var mapping = new[] { 1, 2, 3, 4, 5, 6 };
            var radians = JointMapping.FromRapidAxes(source, mapping);
            SequenceNear(original, JointMapping.ToRapidAxes(radians, mapping));
            SequenceNear(original, source);
            True(mapping.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 }));
            True(!ReferenceEquals(source, radians));
        }));
        tests.Add(("parsed six-field jointtarget maps to four SDK joints", () =>
        {
            var request = RequestParser.ParseJson("jump", "{\"jointtarget\":{\"robotAxes\":[90,10,-20,0,0,30]}}");
            var active = JointMapping.FromRapidAxes(request.JointTarget!.RobotAxes, [1, 2, 3, 6]);
            Equal(4, active.Length);
            SequenceNear([90, 10, -20, 0, 0, 30], JointMapping.ToRapidAxes(active, [1, 2, 3, 6]));
        }));
    }

    private static void SequenceNear(double[] expected, double[] actual)
    {
        Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Near(expected[index], actual[index]);
    }
}
