namespace RobotStudioOffline.Core;

/// <summary>
/// Converts the SDK's compact active-joint vector to the six RAPID robot-axis fields.
/// Indices are the SDK GetActiveJoints() values: one-based robot-axis numbers in compact-vector order.
/// A robot with fewer active joints is mapped by these indices, never by its model name.
/// </summary>
public static class JointMapping
{
    private const double InactiveAxisToleranceDegrees = 1e-9;

    /// <summary>Expand active SDK radians into six RAPID degrees; unused robot-axis fields are zero.</summary>
    public static double[] ToRapidAxes(double[] activeRadians, int[] activeAxes)
    {
        ValidateMap(activeAxes, activeRadians.Length);
        var result = new double[6];
        for (var index = 0; index < activeAxes.Length; index++)
        {
            var degrees = FrameMath.RadiansToDegrees(activeRadians[index]);
            if (!double.IsFinite(degrees))
                throw new BridgeException("invalid_sdk_solution", "The station returned a nonfinite robot-axis value.", 409);
            result[activeAxes[index] - 1] = degrees;
        }
        return result;
    }

    /// <summary>
    /// Select active SDK radians from six RAPID degrees. Unused robot-axis fields must be zero
    /// (absolute tolerance 1e-9 degrees); they are not silently reassigned or treated as external axes.
    /// </summary>
    public static double[] FromRapidAxes(double[] rapidDegrees, int[] activeAxes)
    {
        ValidateMap(activeAxes, activeAxes.Length);
        if (rapidDegrees.Length != 6 || rapidDegrees.Any(value => !double.IsFinite(value)))
            throw new BridgeException("invalid_request", "robotAxes must contain six finite values in degrees.");
        for (var index = 0; index < rapidDegrees.Length; index++)
        {
            if (!activeAxes.Contains(index + 1) && Math.Abs(rapidDegrees[index]) > InactiveAxisToleranceDegrees)
                throw new BridgeException("inactive_joint", $"Robot axis {index + 1} is inactive in this station mechanism; its robotAxes value must be zero.");
        }
        return activeAxes.Select(axis => FrameMath.DegreesToRadians(rapidDegrees[axis - 1])).ToArray();
    }

    /// <summary>Reject missing, repeated, out-of-range or inconsistent SDK axis mappings.</summary>
    public static void ValidateMap(int[] activeAxes, int expectedActiveCount)
    {
        if (expectedActiveCount is < 1 or > 6 || activeAxes.Length != expectedActiveCount ||
            activeAxes.Any(axis => axis is < 1 or > 6) || activeAxes.Distinct().Count() != activeAxes.Length)
            throw new BridgeException("unsupported_mechanism",
                "The station mechanism must expose one to six unique active robot-axis indices in the range 1..6, matching its active-joint count.", 409);
    }
}
