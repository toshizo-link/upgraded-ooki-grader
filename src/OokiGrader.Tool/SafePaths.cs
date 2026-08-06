namespace OokiGrader.Tool;

internal static class SafePaths
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string RequireAbsoluteNonRoot(
        string value,
        string optionName,
        bool requireExistingDirectory = false,
        bool requireExistingFile = false)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Path.IsPathFullyQualified(value)
            || value.IndexOfAny(['*', '?']) >= 0)
        {
            throw new ToolUsageException(
                "path_invalid",
                $"{optionName} must be an absolute, non-wildcard path.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var root = Path.GetPathRoot(fullPath);
        if (root is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(root),
                fullPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                "path_root_rejected",
                $"{optionName} may not be a filesystem root.");
        }

        if (requireExistingDirectory && !Directory.Exists(fullPath))
        {
            throw new ToolUsageException(
                "directory_missing",
                $"{optionName} must identify an existing directory.");
        }

        if (requireExistingFile && !File.Exists(fullPath))
        {
            throw new ToolUsageException(
                "file_missing",
                $"{optionName} must identify an existing file.");
        }

        if (requireExistingDirectory || requireExistingFile)
        {
            EnsureExistingPathChainHasNoReparsePoints(fullPath, optionName);
        }

        return fullPath;
    }

    public static void EnsureExistingPathChainHasNoReparsePoints(
        string path,
        string optionName)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ToolUsageException(
                "path_invalid",
                $"{optionName} must be an absolute path.");
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if (IsReparsePoint(current))
            {
                throw new ToolUsageException(
                    "path_reparse_point_rejected",
                    $"{optionName} may not traverse a reparse point or symbolic link.");
            }
        }
    }

    public static string RequireCanonicalBackupRelativePath(
        string value,
        string backupId)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                "backup_path_invalid",
                "The backup relative path is not canonical.");
        }

        var segments = value.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !string.Equals(
                value,
                string.Join('/', segments),
                StringComparison.Ordinal)
            || !string.Equals(segments[0], "sets", StringComparison.Ordinal)
            || segments[1].Length != 4
            || !segments[1].All(char.IsAsciiDigit)
            || segments[2].Length != 2
            || !int.TryParse(
                segments[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var month)
            || month is < 1 or > 12
            || !string.Equals(
                segments[3],
                backupId,
                StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                "backup_path_invalid",
                "The backup relative path is not canonical.");
        }

        return value;
    }

    public static string ResolveUnderRoot(
        string root,
        string relativePath,
        string optionName,
        bool requireExisting = false)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new ToolUsageException(
                "relative_path_invalid",
                $"{optionName} is not a safe relative path.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var resolved = Path.GetFullPath(
            Path.Combine(normalizedRoot, relativePath));
        if (!resolved.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new ToolUsageException(
                "path_escape_rejected",
                $"{optionName} escaped its allowed root.");
        }

        if (requireExisting
            && !File.Exists(resolved)
            && !Directory.Exists(resolved))
        {
            throw new ToolUsageException(
                "path_missing",
                $"{optionName} identifies a missing path.");
        }

        EnsureExistingPathChainHasNoReparsePoints(resolved, optionName);
        return resolved;
    }

    public static bool IsSameOrNested(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent));
        return string.Equals(
                normalizedCandidate,
                normalizedParent,
                PathComparison)
            || normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                PathComparison);
    }

    public static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2_048
            || Path.IsPathFullyQualified(value)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment =>
                segment is not "." and not ".."
                && segment.Length <= 255);
    }

    public static bool IsReadableDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)
                || IsReparsePoint(path))
            {
                return false;
            }

            using var enumerator = Directory
                .EnumerateFileSystemEntries(
                    path,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static bool Equal(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);
}
