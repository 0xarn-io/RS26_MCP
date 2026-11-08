namespace RobotStudioOffline.Core;

public static class LocalGeometryFile
{
    public static void RequireAvailable(string canonicalPath) => LocalInputFile.RequireAvailable(canonicalPath, "geometry");
}
