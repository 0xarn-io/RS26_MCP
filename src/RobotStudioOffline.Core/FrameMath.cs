namespace RobotStudioOffline.Core;

public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a) => new(-a.X, -a.Y, -a.Z);
    public double[] ToArray() => [X, Y, Z];
}

/// <summary>Unit quaternion in ABB/RAPID order w,x,y,z; all calculations use doubles.</summary>
public readonly record struct QuaternionD(double W, double X, double Y, double Z)
{
    public static QuaternionD Identity => new(1, 0, 0, 0);
    public double[] ToArray() => [W, X, Y, Z];
}

/// <summary>A local frame expressed in its parent's coordinates. Translation keeps the caller's units.</summary>
public readonly record struct RigidFrame(Vector3D Position, QuaternionD Orientation)
{
    public static RigidFrame Identity => new(new(0, 0, 0), QuaternionD.Identity);
}

public static class FrameMath
{
    public static QuaternionD Normalize(QuaternionD q)
    {
        var scale = Math.Max(Math.Max(Math.Abs(q.W), Math.Abs(q.X)), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z)));
        if (!double.IsFinite(scale) || scale < 1e-15) throw new ArgumentException("Quaternion must be finite and nonzero.", nameof(q));
        var w = q.W / scale;
        var x = q.X / scale;
        var y = q.Y / scale;
        var z = q.Z / scale;
        var length = Math.Sqrt(w * w + x * x + y * y + z * z);
        return new(w / length, x / length, y / length, z / length);
    }

    /// <summary>Hamilton product: applying child first, then parent.</summary>
    public static QuaternionD Multiply(QuaternionD parent, QuaternionD child)
    {
        var a = Normalize(parent);
        var b = Normalize(child);
        return Normalize(new(
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W));
    }

    public static Vector3D Rotate(QuaternionD quaternion, Vector3D vector)
    {
        var q = Normalize(quaternion);
        // v' = v + 2*w*(q.xyz x v) + 2*(q.xyz x (q.xyz x v)).
        var tx = 2 * (q.Y * vector.Z - q.Z * vector.Y);
        var ty = 2 * (q.Z * vector.X - q.X * vector.Z);
        var tz = 2 * (q.X * vector.Y - q.Y * vector.X);
        return new(
            vector.X + q.W * tx + q.Y * tz - q.Z * ty,
            vector.Y + q.W * ty + q.Z * tx - q.X * tz,
            vector.Z + q.W * tz + q.X * ty - q.Y * tx);
    }

    public static RigidFrame Compose(RigidFrame parent, RigidFrame child) =>
        new(parent.Position + Rotate(parent.Orientation, child.Position), Multiply(parent.Orientation, child.Orientation));

    public static RigidFrame Inverse(RigidFrame frame)
    {
        var q = Normalize(frame.Orientation);
        var inverseRotation = new QuaternionD(q.W, -q.X, -q.Y, -q.Z);
        return new(Rotate(inverseRotation, -frame.Position), inverseRotation);
    }

    /// <summary>Express worldFromFrame relative to worldFromReference.</summary>
    public static RigidFrame RelativeTo(RigidFrame worldFromReference, RigidFrame worldFromFrame) =>
        Compose(Inverse(worldFromReference), worldFromFrame);

    /// <summary>Apply a TCP offset: worldFromFlange = worldFromTcp * inverse(flangeFromTcp).</summary>
    public static RigidFrame FlangeFromTcp(RigidFrame worldFromTcp, RigidFrame flangeFromTcp) =>
        Compose(worldFromTcp, Inverse(flangeFromTcp));

    public static RigidFrame FromRobTarget(RobTarget target) => new(
        new(target.Position[0], target.Position[1], target.Position[2]),
        Normalize(new(target.Quaternion[0], target.Quaternion[1], target.Quaternion[2], target.Quaternion[3])));

    public static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);
    public static double RadiansToDegrees(double radians) => radians * (180.0 / Math.PI);
}
