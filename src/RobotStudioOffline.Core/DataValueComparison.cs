using System.Text.Json;

namespace RobotStudioOffline.Core;

/// <summary>Comparison of normalized station/VC data snapshots, independent of ABB assemblies.</summary>
public static class DataValueComparison
{
    public const double AbsoluteTolerance = 1e-6;
    public const double RelativeTolerance = 1e-7;
    public const double QuaternionTolerance = 1e-6;

    public static string[] DifferencePaths(JsonElement station, JsonElement controller)
    {
        var differences = new List<string>();
        Compare(station, controller, "$", differences);
        return differences.ToArray();
    }

    private static void Compare(JsonElement left, JsonElement right, string path, List<string> differences)
    {
        if (left.ValueKind != right.ValueKind) { differences.Add(path); return; }
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var rightProperties = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var name in leftProperties.Keys.Union(rightProperties.Keys, StringComparer.Ordinal))
                {
                    if (!leftProperties.TryGetValue(name, out var l) || !rightProperties.TryGetValue(name, out var r)) differences.Add(path + "." + name);
                    else Compare(l, r, path + "." + name, differences);
                }
                break;
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength()) { differences.Add(path); return; }
                if (path.EndsWith(".quaternion", StringComparison.Ordinal))
                {
                    if (!SameOrientation(left, right)) differences.Add(path);
                    return;
                }
                for (var index = 0; index < left.GetArrayLength(); index++) Compare(left[index], right[index], $"{path}[{index}]", differences);
                break;
            case JsonValueKind.Number:
                var a = left.GetDouble();
                var b = right.GetDouble();
                var tolerance = path.Contains(".configuration[", StringComparison.Ordinal)
                    ? 0 : AbsoluteTolerance + RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b));
                if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Abs(a - b) > tolerance) differences.Add(path);
                break;
            case JsonValueKind.String:
                if (!string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal)) differences.Add(path);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default: differences.Add(path); break;
        }
    }

    private static bool SameOrientation(JsonElement left, JsonElement right)
    {
        if (left.GetArrayLength() != 4 || right.GetArrayLength() != 4 ||
            left.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number) ||
            right.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number)) return false;
        var a = left.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var b = right.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var normA = Math.Sqrt(a.Sum(x => x * x));
        var normB = Math.Sqrt(b.Sum(x => x * x));
        if (!double.IsFinite(normA) || !double.IsFinite(normB) || Math.Abs(normA - 1) > QuaternionTolerance || Math.Abs(normB - 1) > QuaternionTolerance) return false;
        var same = true;
        var opposite = true;
        for (var index = 0; index < 4; index++)
        {
            same &= Math.Abs(a[index] / normA - b[index] / normB) <= QuaternionTolerance;
            opposite &= Math.Abs(a[index] / normA + b[index] / normB) <= QuaternionTolerance;
        }
        return same || opposite;
    }
}
