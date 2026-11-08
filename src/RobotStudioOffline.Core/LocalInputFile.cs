namespace RobotStudioOffline.Core;

/// <summary>Validates owner-selected local input files before a native SDK operation.</summary>
public static class LocalInputFile
{
    public static string CanonicalPath(string path)
    {
        if (path.Length is < 4 or > 32767 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/'))
            throw Invalid("Select a fully qualified local drive-letter path.");

        var normalized = char.ToUpperInvariant(path[0]) + path[1..].Replace('/', '\\');
        var components = normalized[3..].Split('\\');
        foreach (var component in components)
        {
            if (component.Length == 0 || component[^1] is '.' or ' ' ||
                component.Any(c => char.IsControl(c) || c is ':' or '"' or '<' or '>' or '|' or '?' or '*'))
                throw Invalid("The file path contains an invalid Windows path component.");
            if (IsDeviceName(component)) throw Invalid("Windows device names are forbidden in local paths.");
        }

        return normalized;
    }

    private static bool IsDeviceName(string component)
    {
        // Win32 resolves device names even with extensions, e.g. NUL.part.stl or COM¹.stp.
        var dot = component.IndexOf('.');
        var stem = (dot < 0 ? component : component[..dot]).TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "NUL" or "PRN" or "AUX" or "CONIN$" or "CONOUT$") return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
            stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]);
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);

    public static void RequireAvailable(string canonicalPath, string kind)
    {
        if (!OperatingSystem.IsWindows() || canonicalPath.Length < 4 ||
            !char.IsAsciiLetter(canonicalPath[0]) || canonicalPath[1] != ':' || canonicalPath[2] != '\\')
            throw new BridgeException($"{kind}_file_not_local", "Select a file on a local Windows drive.");

        try
        {
            var root = canonicalPath[..3];
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
                throw new BridgeException($"{kind}_file_not_local", "Copy the file to a local drive before using it.");

            // An ordinary-looking drive path can cross a junction onto a share, or
            // refer to an online-only placeholder. Check each ancestor before descending.
            var current = root;
            foreach (var segment in canonicalPath[3..].Split('\\'))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new BridgeException($"{kind}_file_not_local",
                        "Use a locally stored file in a regular directory. The selected path contains a link or an offline file.");
                if (current == canonicalPath && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                    throw new BridgeException($"{kind}_file_not_regular", "The selected path must name a regular file.");
            }
            if (new FileInfo(canonicalPath).Length == 0)
                throw new BridgeException($"{kind}_file_empty", "The selected file is empty.");
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BridgeException($"{kind}_file_missing", "The selected file could not be found.", 404);
        }
        catch (UnauthorizedAccessException)
        {
            throw new BridgeException($"{kind}_file_unreadable", "RobotStudio does not have permission to read the selected file.", 403);
        }
        catch (IOException)
        {
            throw new BridgeException($"{kind}_file_unreadable", "The selected file or its local drive could not be read.", 409);
        }
    }
}
