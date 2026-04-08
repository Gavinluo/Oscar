using System.Diagnostics;
using CodeBrain.Core.Models;
using CodeBrain.Storage;

namespace CodeBrain.Tests;

[TestFixture]
public class IncrementalIndexPlannerTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codebrain-planner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            TryDeleteDirectory(_root);
        }
    }

    [Test]
    public async Task PlanAsync_WorkingTreeStagedFilter_OnlyReturnsStagedChanges()
    {
        SkipIfGitUnavailable();

        var repository = CreateRepository();
        var manifest = await CaptureManifestAsync(repository);

        WriteFile("tracked-staged.txt", "staged change");
        RunGit("add tracked-staged.txt");
        WriteFile("tracked-unstaged.txt", "unstaged change");

        var planner = new LocalFileIncrementalIndexPlanner(new RepositoryIndexingOptions
        {
            GitDiffTarget = GitDiffTarget.WorkingTree,
            GitChangeFilter = GitChangeFilter.Staged
        });

        var changeSet = await planner.PlanAsync(repository, manifest, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(changeSet.Modified, Is.EqualTo(new[] { "tracked-staged.txt" }));
            Assert.That(changeSet.GitStaged, Contains.Item("tracked-staged.txt"));
            Assert.That(changeSet.GitUnstaged, Contains.Item("tracked-unstaged.txt"));
            Assert.That(changeSet.GitChangeFilter, Is.EqualTo(nameof(GitChangeFilter.Staged)));
            Assert.That(changeSet.GitDiffTarget, Is.EqualTo(nameof(GitDiffTarget.WorkingTree)));
        });
    }

    [Test]
    public async Task PlanAsync_WorkingTreeUnstagedFilter_IncludesUntrackedFiles()
    {
        SkipIfGitUnavailable();

        var repository = CreateRepository();
        var manifest = await CaptureManifestAsync(repository);

        WriteFile("tracked-staged.txt", "staged change");
        RunGit("add tracked-staged.txt");
        WriteFile("tracked-unstaged.txt", "unstaged change");
        WriteFile("new-untracked.txt", "brand new");

        var planner = new LocalFileIncrementalIndexPlanner(new RepositoryIndexingOptions
        {
            GitDiffTarget = GitDiffTarget.WorkingTree,
            GitChangeFilter = GitChangeFilter.Unstaged
        });

        var changeSet = await planner.PlanAsync(repository, manifest, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(changeSet.Modified, Is.EqualTo(new[] { "tracked-unstaged.txt" }));
            Assert.That(changeSet.Added, Is.EqualTo(new[] { "new-untracked.txt" }));
            Assert.That(changeSet.GitUntracked, Contains.Item("new-untracked.txt"));
            Assert.That(changeSet.Modified, Does.Not.Contain("tracked-staged.txt"));
        });
    }

    [Test]
    public async Task PlanAsync_HeadAllFilter_ReturnsStagedAndUnstagedChanges()
    {
        SkipIfGitUnavailable();

        var repository = CreateRepository();
        var manifest = await CaptureManifestAsync(repository);

        WriteFile("tracked-staged.txt", "staged change");
        RunGit("add tracked-staged.txt");
        WriteFile("tracked-unstaged.txt", "unstaged change");

        var planner = new LocalFileIncrementalIndexPlanner(new RepositoryIndexingOptions
        {
            GitDiffTarget = GitDiffTarget.Head,
            GitChangeFilter = GitChangeFilter.All
        });

        var changeSet = await planner.PlanAsync(repository, manifest, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(changeSet.Modified, Is.EquivalentTo(new[] { "tracked-staged.txt", "tracked-unstaged.txt" }));
            Assert.That(changeSet.HeadCommit, Is.Not.Null.And.Not.Empty);
            Assert.That(changeSet.DetectionMode, Is.EqualTo("git"));
        });
    }

    private RegisteredRepository CreateRepository()
    {
        WriteFile("tracked-staged.txt", "base staged");
        WriteFile("tracked-unstaged.txt", "base unstaged");

        RunGit("init");
        RunGit("config user.email tests@example.com");
        RunGit("config user.name CodeBrain Tests");
        RunGit("add .");
        RunGit("commit -m \"initial\"");

        return new RegisteredRepository
        {
            Id = "planner-test",
            DisplayName = "planner-test",
            RootPath = _root,
            AnalyzerId = "text-structure",
            PrimaryLanguage = "text"
        };
    }

    private async Task<RepositoryIndexManifest> CaptureManifestAsync(RegisteredRepository repository)
    {
        var files = await RepositoryFileScanner.ScanAsync(repository.RootPath, CancellationToken.None);
        var headCommit = RunGit("rev-parse HEAD").Trim();

        return new RepositoryIndexManifest
        {
            RepositoryId = repository.Id,
            RepositoryRoot = repository.RootPath,
            AnalyzerId = repository.AnalyzerId,
            PrimaryLanguage = repository.PrimaryLanguage,
            HeadCommit = headCommit,
            ChangeDetectionMode = "git",
            Files = files
        };
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void SkipIfGitUnavailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Assert.Ignore("git is not available in this environment.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Assert.Ignore("git is not available in this environment.");
            }
        }
        catch
        {
            Assert.Ignore("git is not available in this environment.");
        }
    }

    private string RunGit(string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Unable to start git {arguments}.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        }

        return stdout;
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 7)
            {
                Thread.Sleep(150);
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(150);
            }
        }

        ClearReadOnlyAttributes(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
