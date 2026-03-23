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
    public async Task<GitRepositoryState?> TryInspectAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")))
        {
            return null;
        }

        var headCommit = await TryRunGitAsync("rev-parse HEAD", repositoryRoot, cancellationToken);
        if (string.IsNullOrWhiteSpace(headCommit))
        {
            return null;
        }

        var statusOutput = await TryRunGitAsync("status --porcelain", repositoryRoot, cancellationToken);
        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in SplitLines(statusOutput))
        {
            if (line.Length < 4)
            {
                continue;
            }

            var path = NormalizeGitPath(line[3..]);
            if (line.StartsWith("??", StringComparison.Ordinal))
            {
                untracked.Add(path);
            }
            else
            {
                modified.Add(path);
            }
        }

        var diffOutput = await TryRunGitAsync("diff --name-only HEAD --", repositoryRoot, cancellationToken);
        foreach (var line in SplitLines(diffOutput))
        {
            modified.Add(NormalizeGitPath(line));
        }

        return new GitRepositoryState
        {
            HeadCommit = headCommit.Trim(),
            ModifiedPaths = modified.ToList(),
            UntrackedPaths = untracked.ToList()
        };
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

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
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
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeGitPath(string value)
    {
        return value.Replace('/', Path.DirectorySeparatorChar).Trim();
    }
}

internal sealed class GitRepositoryState
{
    public string HeadCommit { get; set; } = string.Empty;
    public List<string> ModifiedPaths { get; set; } = new();
    public List<string> UntrackedPaths { get; set; } = new();
}
