using System.Security.Cryptography;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Analysis.CSharp;

/// <summary>
/// Adapts the existing Roslyn repository mapper into the new analyzer abstraction.
/// The analyzer produces a graph plus a lightweight set of retrieval documents
/// so query features can reuse the same repository understanding that powers
/// the closed-loop test workflow.
/// </summary>
public sealed class CSharpRepositoryAnalyzer : IRepositoryAnalyzer
{
    private readonly RoslynRepoMapService _mapService = new();

    public string Id => "csharp-roslyn";

    public bool CanHandle(RegisteredRepository repository)
    {
        return string.Equals(repository.PrimaryLanguage, "csharp", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RepositoryAnalysisResult> AnalyzeAsync(
        RepositoryAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var repository = request.Repository;
        var solutionOrProjectPath = ResolveSolutionOrProject(repository.RootPath);

        var map = await _mapService.BuildMapAsync(solutionOrProjectPath, cancellationToken);
        var graph = await _mapService.BuildKnowledgeGraphAsync(solutionOrProjectPath, cancellationToken);

        var manifest = new RepositoryIndexManifest
        {
            RepositoryId = repository.Id,
            RepositoryRoot = repository.RootPath,
            AnalyzerId = Id,
            PrimaryLanguage = repository.PrimaryLanguage,
            IndexedAt = DateTimeOffset.UtcNow,
            Files = await ScanFilesAsync(repository.RootPath, cancellationToken)
        };

        return new RepositoryAnalysisResult
        {
            Map = map,
            Graph = graph,
            Manifest = manifest,
            Documents = BuildDocuments(repository, map, graph)
        };
    }

    private static List<RepositoryIndexDocument> BuildDocuments(
        RegisteredRepository repository,
        RepositoryMap map,
        RepositoryKnowledgeGraph graph)
    {
        var documents = new List<RepositoryIndexDocument>();

        foreach (var project in map.Projects.Distinct(StringComparer.Ordinal))
        {
            documents.Add(new RepositoryIndexDocument
            {
                RepositoryId = repository.Id,
                Kind = "project",
                Title = Path.GetFileNameWithoutExtension(project),
                Content = $"Project file: {project}",
                FilePath = project,
                Language = repository.PrimaryLanguage
            });
        }

        foreach (var symbolNode in graph.Nodes.Where(node => node.Kind == "symbol"))
        {
            var relatedCalls = graph.Edges.Count(edge => edge.Kind == "calls" && edge.From == symbolNode.Id);
            var containedBy = symbolNode.Namespace ?? "global";

            documents.Add(new RepositoryIndexDocument
            {
                RepositoryId = repository.Id,
                Kind = "symbol",
                Title = symbolNode.Label,
                Symbol = symbolNode.Symbol,
                FilePath = symbolNode.FilePath,
                Language = repository.PrimaryLanguage,
                Content = $"Symbol: {symbolNode.Symbol}\nNamespace: {containedBy}\nCalls: {relatedCalls}\nFile: {symbolNode.FilePath}"
            });
        }

        foreach (var namespaceName in map.Namespaces)
        {
            documents.Add(new RepositoryIndexDocument
            {
                RepositoryId = repository.Id,
                Kind = "namespace",
                Title = namespaceName,
                Language = repository.PrimaryLanguage,
                Content = $"Namespace: {namespaceName}"
            });
        }

        return documents;
    }

    private static string ResolveSolutionOrProject(string rootPath)
    {
        var solution = Directory.GetFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (solution is not null)
        {
            return solution;
        }

        var project = Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(path => !path.Contains("test", StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return project;
        }

        throw new InvalidOperationException($"No C# solution or project found under {rootPath}.");
    }

    private static async Task<List<RepositoryFileFingerprint>> ScanFilesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var files = new List<RepositoryFileFingerprint>();

        foreach (var path in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);

            files.Add(new RepositoryFileFingerprint
            {
                RelativePath = Path.GetRelativePath(rootPath, path),
                Size = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                ContentHash = Convert.ToHexString(hash),
                Language = "csharp"
            });
        }

        return files;
    }
}
