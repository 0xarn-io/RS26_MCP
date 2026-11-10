namespace RobotStudioOffline.Core;

/// <summary>Draft-zone checks from 3HAC098577-001 §10.5.3.6. Coordinates are in metres.</summary>
public static class SafeMovePolygon
{
    public static void Check(double[][] points)
    {
        if (points.Length is < 4 or > 24 ||
            points.Any(p => p is not { Length: 2 } || p.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1000)))
            throw Invalid("Use 4–24 [x,y] vertices in controller world coordinates, within ±1000 metres.");

        const double minEdge = 0.1;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            var c = points[(i + 2) % points.Length];
            var ab = double.Hypot(a[0] - b[0], a[1] - b[1]);
            var cb = double.Hypot(c[0] - b[0], c[1] - b[1]);
            if (ab < minEdge - 1e-12 || cb < minEdge - 1e-12)
                throw Invalid("Each zone edge, including the closing edge, must be at least 0.1 m.");
            var dot = (a[0] - b[0]) * (c[0] - b[0]) + (a[1] - b[1]) * (c[1] - b[1]);
            if (dot / (ab * cb) > Math.Cos(Math.PI / 6) + 1e-12)
                throw Invalid("Each interior corner angle must be between 30 and 330 degrees.");

            for (var j = i + 2; j < points.Length; j++)
            {
                if (i == 0 && j == points.Length - 1) continue; // The closing edge shares the first vertex.
                var end = points[(j + 1) % points.Length];
                if (Intersect(a, b, points[j], end)) throw Invalid("Zone edges may not cross or touch nonadjacent edges.");
            }
        }
        // The minimum enclosing strip for a polygon is parallel to an edge of its convex hull.
        // Each hull edge joins two input vertices, including across a concavity.
        for (var i = 0; i < points.Length; i++)
        for (var j = i + 1; j < points.Length; j++)
        {
            var dx = points[j][0] - points[i][0];
            var dy = points[j][1] - points[i][1];
            var projections = points.Select(p => dx * (p[1] - points[i][1]) - dy * (p[0] - points[i][0])).ToArray();
            if (projections.Max() - projections.Min() <= minEdge * double.Hypot(dx, dy))
                throw Invalid("The polygon's overall width must exceed 0.1 m.");
        }
    }

    private static bool Intersect(double[] a, double[] b, double[] c, double[] d)
    {
        static double Cross(double[] p, double[] q, double[] r) =>
            (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]);
        static bool Within(double[] p, double[] q, double[] r) =>
            r[0] >= Math.Min(p[0], q[0]) && r[0] <= Math.Max(p[0], q[0]) &&
            r[1] >= Math.Min(p[1], q[1]) && r[1] <= Math.Max(p[1], q[1]);
        var abc = Math.Sign(Cross(a, b, c));
        var abd = Math.Sign(Cross(a, b, d));
        var cda = Math.Sign(Cross(c, d, a));
        var cdb = Math.Sign(Cross(c, d, b));
        return (abc * abd < 0 && cda * cdb < 0) ||
            (abc == 0 && Within(a, b, c)) || (abd == 0 && Within(a, b, d)) ||
            (cda == 0 && Within(c, d, a)) || (cdb == 0 && Within(c, d, b));
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
