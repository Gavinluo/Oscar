using System.Diagnostics;
using CodeBrain.Core.Models;

namespace CodeBrain.Storage;

/// <summary>
/// Reads lightweight Git state when the target repository is a Git checkout.
/// The planner uses this as a hint source and falls back to filesystem diffs
/// whenever Git is unavailable or the repository is dirty in unsupported ways.
/// </summary>
internal sealed class GitRepositoryInspector
{
    public async Task<GitRepositoryState?> TryInspectAsync(
        string repositoryRoot,
        RepositoryIndexingOptions options,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")))
        {
            return null;
        }

        var headCommit = await TryRunGitAsync("rev-parse HEAD", repositoryRoot, cancellationToken);
        if (options.GitDiffTarget == GitDiffTarget.Head && string.IsNullOrWhiteSpace(headCommit))
        {
            return null;
        }

        var staged = await LoadPathSetAsync("diff --name-only --cached", repositoryRoot, cancellationToken);
        var unstaged = await LoadPathSetAsync("diff --name-only", repositoryRoot, cancellationToken);
        var untracked = await LoadPathSetAsync("ls-files --others --exclude-standard", repositoryRoot, cancellationToken);

        var selectedPaths = await ResolveSelectedPathsAsync(repositoryRoot, options, staged, unstaged, untracked, cancellationToken);
        var modified = selectedPaths
            .Where(path => !untracked.Contains(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GitRepositoryState
        {
            HeadCommit = headCommit?.Trim(),
            DiffTarget = options.GitDiffTarget,
            ChangeFilter = options.GitChangeFilter,
            SelectedPaths = selectedPaths.ToList(),
            ModifiedPaths = modified.ToList(),
            StagedPaths = staged.ToList(),
            UnstagedPaths = unstaged.ToList(),
            UntrackedPaths = untracked.ToList()
        };
    }

    private static async Task<HashSet<string>> LoadPathSetAsync(
        string arguments,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var output = await TryRunGitAsync(arguments, repositoryRoot, cancellationToken);
        return SplitLines(output)
            .Select(NormalizeGitPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<HashSet<string>> ResolveSelectedPathsAsync(
        string repositoryRoot,
        RepositoryIndexingOptions options,
        HashSet<string> staged,
        HashSet<string> unstaged,
        HashSet<string> untracked,
        CancellationToken cancellationToken)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.GitDiffTarget == GitDiffTarget.Head)
        {
            var diffArguments = options.GitChangeFilter switch
            {
                GitChangeFilter.Staged => "diff --name-only --cached HEAD --",
                GitChangeFilter.Unstaged => "diff --name-only --",
                _ => "diff --name-only HEAD --"
            };

            var diffOutput = await TryRunGitAsync(diffArguments, repositoryRoot, cancellationToken);
            foreach (var line in SplitLines(diffOutput))
            {
                selected.Add(NormalizeGitPath(line));
            }
        }
        else
        {
            switch (options.GitChangeFilter)
            {
                case GitChangeFilter.Staged:
                    selected.UnionWith(staged);
                    break;
                case GitChangeFilter.Unstaged:
                    selected.UnionWith(unstaged);
                    break;
                default:
                    selected.UnionWith(staged);
                    selected.UnionWith(unstaged);
                    break;
            }
        }

        if (options.GitChangeFilter != GitChangeFilter.Staged)
        {
            selected.UnionWith(untracked);
        }

        return selected;
    }

    private static async Task<string?> TryRunGitAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitLines(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeGitPath(string value)
    {
        return value.Replace('/', Path.DirectorySeparatorChar).Trim();
    }
}

internal sealed class GitRepositoryState
{
    public string? HeadCommit { get; set; }
    public GitDiffTarget DiffTarget { get; set; } = GitDiffTarget.Head;
    public GitChangeFilter ChangeFilter { get; set; } = GitChangeFilter.All;
    public List<string> SelectedPaths { get; set; } = new();
    public List<string> ModifiedPaths { get; set; } = new();
    public List<string> StagedPaths { get; set; } = new();
    public List<string> UnstagedPaths { get; set; } = new();
    public List<string> UntrackedPaths { get; set; } = new();
}
