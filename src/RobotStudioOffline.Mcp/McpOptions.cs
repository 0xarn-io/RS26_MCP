using System.Globalization;

namespace RobotStudioOffline.Mcp;

public sealed record McpOptions(int Port, string TokenFile)
{
    public static McpOptions Parse(string[] args)
    {
        var port = 18426;
        var tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotStudioOfflineBridge", "token");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--port" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                        || port is < 1024 or > 65535)
                        throw new ArgumentException("--port must be an integer from 1024 through 65535.");
                    break;
                case "--token-file" when index + 1 < args.Length:
                    tokenFile = args[++index];
                    break;
                default:
                    throw new ArgumentException("Only --port and --token-file are accepted. The bridge is fixed to 127.0.0.1. Real-controller access requires separate explicit owner permission and is not exposed here.");
            }
        }
        return new McpOptions(port, ValidateTokenFile(tokenFile));
    }

    public static string ValidateTokenFile(string tokenFile)
    {
        // File reads must not introduce SMB or another network connection. Reject
        // UNC/device syntax before asking the filesystem anything about the path.
        if (string.IsNullOrWhiteSpace(tokenFile) || tokenFile.StartsWith("\\\\", StringComparison.Ordinal) ||
            tokenFile.StartsWith("//", StringComparison.Ordinal) || tokenFile.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("The token file must be on a local drive. UNC, device and network paths are forbidden.");
        var fullPath = Path.GetFullPath(tokenFile);
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || fullPath[2] != '\\')
                throw new ArgumentException("The token file must resolve to a local Windows drive path.");
            var root = Path.GetPathRoot(fullPath)!;
            if (new DriveInfo(root).DriveType == DriveType.Network)
                throw new ArgumentException("The token file cannot be read from a mapped network drive.");
        }
        return fullPath;
    }
}
