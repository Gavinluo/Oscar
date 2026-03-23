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
    private readonly GitRepositoryInspector _gitInspector = new();

    public async Task<RepositoryChangeSet> PlanAsync(
        RegisteredRepository repository,
        RepositoryIndexManifest? existingManifest,
        CancellationToken cancellationToken)
    {
        var currentFiles = await RepositoryFileScanner.ScanAsync(repository.RootPath, cancellationToken);
        var previousFiles = existingManifest?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, RepositoryFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var gitState = await _gitInspector.TryInspectAsync(repository.RootPath, cancellationToken);

        var changeSet = new RepositoryChangeSet
        {
            DetectionMode = gitState is null ? "filesystem" : "git",
            HeadCommit = gitState?.HeadCommit
        };

        if (gitState is not null)
        {
            changeSet.GitModified.AddRange(gitState.ModifiedPaths);
            changeSet.GitUntracked.AddRange(gitState.UntrackedPaths);

            if (existingManifest is not null &&
                string.Equals(existingManifest.HeadCommit, gitState.HeadCommit, StringComparison.Ordinal) &&
                gitState.ModifiedPaths.Count == 0 &&
                gitState.UntrackedPaths.Count == 0 &&
                previousFiles.Count == currentFiles.Count)
            {
                changeSet.Unchanged.AddRange(currentFiles.Select(file => file.RelativePath));
                return changeSet;
            }
        }

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
}
