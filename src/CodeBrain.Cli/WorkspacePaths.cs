namespace CodeBrain.Cli;

/// <summary>
/// Resolves two different roots used by the CLI:
/// 1. the CodeBrain workspace, where the catalog and persisted indexes live
/// 2. the target repository workspace, where closed-loop artifacts and config live
/// </summary>
internal static class WorkspacePaths
{
    public static string ResolveCodeBrainRoot()
    {
        var fromBaseDirectory = FindByMarker(AppContext.BaseDirectory, "CodeBrain.sln");
        if (fromBaseDirectory is not null)
        {
            return fromBaseDirectory;
        }

        var fromCurrentDirectory = FindByMarker(Directory.GetCurrentDirectory(), "CodeBrain.sln");
        if (fromCurrentDirectory is not null)
        {
            return fromCurrentDirectory;
        }

        throw new InvalidOperationException("Unable to locate the CodeBrain workspace root.");
    }

    public static string ResolveTargetWorkspaceRoot(string? solutionOrProjectPath)
    {
        if (!string.IsNullOrWhiteSpace(solutionOrProjectPath))
        {
            var fullPath = Path.GetFullPath(solutionOrProjectPath);
            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? FindByMarker(string startPath, string markerFileName)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, markerFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
