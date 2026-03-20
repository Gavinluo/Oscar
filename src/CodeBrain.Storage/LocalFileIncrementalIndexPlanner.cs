using System.Security.Cryptography;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Storage;

/// <summary>
/// Computes file-level deltas for a local repository. This keeps the first
/// incremental implementation simple and deterministic: unchanged files are
/// skipped, while added/modified/removed files trigger a rebuild of the
/// repository-level documents and graph.
/// </summary>
public sealed class LocalFileIncrementalIndexPlanner : IIncrementalIndexPlanner
{
    public async Task<RepositoryChangeSet> PlanAsync(
        RegisteredRepository repository,
        RepositoryIndexManifest? existingManifest,
        CancellationToken cancellationToken)
    {
        var currentFiles = await ScanFilesAsync(repository.RootPath, cancellationToken);
        var previousFiles = existingManifest?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, RepositoryFileFingerprint>(StringComparer.OrdinalIgnoreCase);

        var changeSet = new RepositoryChangeSet();

        foreach (var current in currentFiles)
        {
            if (!previousFiles.TryGetValue(current.RelativePath, out var previous))
            {
                changeSet.Added.Add(current.RelativePath);
                continue;
            }

            if (!string.Equals(current.ContentHash, previous.ContentHash, StringComparison.Ordinal))
            {
                changeSet.Modified.Add(current.RelativePath);
            }
            else
            {
                changeSet.Unchanged.Add(current.RelativePath);
            }
        }

        var currentLookup = currentFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var previous in previousFiles.Keys)
        {
            if (!currentLookup.ContainsKey(previous))
            {
                changeSet.Removed.Add(previous);
            }
        }

        return changeSet;
    }

    public static async Task<List<RepositoryFileFingerprint>> ScanFilesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var fingerprints = new List<RepositoryFileFingerprint>();
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".json", ".md"
        };

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!allowedExtensions.Contains(Path.GetExtension(file)))
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

    private static string GuessLanguage(string path)
    {
        return string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
            ? "csharp"
            : "text";
    }
}
