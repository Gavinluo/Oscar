using System.Security.Cryptography;

namespace CodeBrain.Core.Models;

/// <summary>
/// Centralizes file discovery for repository indexing so the incremental planner
/// and analyzer always operate on the same file set and fingerprint rules.
/// </summary>
public static class RepositoryFileScanner
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".sln",
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
        ".py",
        ".java",
        ".go",
        ".rs",
        ".json",
        ".md"
    };

    public static async Task<List<RepositoryFileFingerprint>> ScanAsync(string rootPath, CancellationToken cancellationToken)
    {
        var fingerprints = new List<RepositoryFileFingerprint>();

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipPath(file) || !AllowedExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            var info = new FileInfo(file);
            await using var stream = File.OpenRead(file);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);

            fingerprints.Add(new RepositoryFileFingerprint
            {
                RelativePath = Path.GetRelativePath(rootPath, file),
                Size = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                ContentHash = Convert.ToHexString(hash),
                Language = GuessLanguage(file)
            });
        }

        return fingerprints.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool ShouldSkipPath(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessLanguage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".java" => "java",
            ".go" => "go",
            ".rs" => "rust",
            ".json" => "json",
            ".md" => "markdown",
            _ => "text"
        };
    }
}
