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
    private readonly GitRepositoryInspector _gitInspector;
    private readonly RepositoryIndexingOptions _options;

    public LocalFileIncrementalIndexPlanner(RepositoryIndexingOptions? options = null)
    {
        _options = options ?? new RepositoryIndexingOptions();
        _gitInspector = new GitRepositoryInspector();
    }

    public async Task<RepositoryChangeSet> PlanAsync(
        RegisteredRepository repository,
        RepositoryIndexManifest? existingManifest,
        CancellationToken cancellationToken)
    {
        var currentFiles = await RepositoryFileScanner.ScanAsync(repository.RootPath, cancellationToken);
        var previousFiles = existingManifest?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, RepositoryFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var gitState = await _gitInspector.TryInspectAsync(repository.RootPath, _options, cancellationToken);

        var changeSet = new RepositoryChangeSet
        {
            DetectionMode = gitState is null ? "filesystem" : "git",
            HeadCommit = gitState?.HeadCommit,
            GitDiffTarget = _options.GitDiffTarget.ToString(),
            GitChangeFilter = _options.GitChangeFilter.ToString()
        };

        if (gitState is not null)
        {
            changeSet.GitModified.AddRange(gitState.ModifiedPaths);
            changeSet.GitStaged.AddRange(gitState.StagedPaths);
            changeSet.GitUnstaged.AddRange(gitState.UnstagedPaths);
            changeSet.GitUntracked.AddRange(gitState.UntrackedPaths);

            if (existingManifest is not null &&
                string.Equals(existingManifest.HeadCommit, gitState.HeadCommit, StringComparison.Ordinal) &&
                gitState.SelectedPaths.Count == 0 &&
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

        if (gitState is not null)
        {
            ApplyGitSelection(changeSet, gitState.SelectedPaths);
        }

        return changeSet;
    }

    private static void ApplyGitSelection(RepositoryChangeSet changeSet, IReadOnlyCollection<string> selectedPaths)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selected.Count == 0)
        {
            changeSet.Added.Clear();
            changeSet.Modified.Clear();
            changeSet.Removed.Clear();
            return;
        }

        var filteredAdded = changeSet.Added.Where(selected.Contains).ToList();
        var filteredModified = changeSet.Modified.Where(selected.Contains).ToList();
        var filteredRemoved = changeSet.Removed.Where(selected.Contains).ToList();
        var filteredOutCurrent = changeSet.Added
            .Concat(changeSet.Modified)
            .Where(path => !selected.Contains(path));

        changeSet.Unchanged = changeSet.Unchanged
            .Concat(filteredOutCurrent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        changeSet.Added = filteredAdded;
        changeSet.Modified = filteredModified;
        changeSet.Removed = filteredRemoved;
    }
}
