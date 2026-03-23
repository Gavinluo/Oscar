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
            HeadCommit = request.ChangeSet.HeadCommit,
            ChangeDetectionMode = request.ChangeSet.DetectionMode,
            Files = await RepositoryFileScanner.ScanAsync(repository.RootPath, cancellationToken)
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
                Language = repository.PrimaryLanguage,
                SearchText = $"{Path.GetFileNameWithoutExtension(project)} {project}"
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
                Content = $"Symbol: {symbolNode.Symbol}\nNamespace: {containedBy}\nCalls: {relatedCalls}\nFile: {symbolNode.FilePath}",
                SearchText = $"{symbolNode.Label} {symbolNode.Symbol} {containedBy} {symbolNode.FilePath}"
            });
        }

        foreach (var group in graph.Nodes
                     .Where(node => node.Kind == "symbol" && !string.IsNullOrWhiteSpace(node.FilePath))
                     .GroupBy(node => node.FilePath!, StringComparer.OrdinalIgnoreCase))
        {
            var filePath = group.Key;
            var symbolLabels = group.Select(node => node.Label).Distinct(StringComparer.Ordinal).Take(20).ToList();
            documents.Add(new RepositoryIndexDocument
            {
                RepositoryId = repository.Id,
                Kind = "file",
                Title = Path.GetFileName(filePath),
                FilePath = filePath,
                Language = repository.PrimaryLanguage,
                Content = $"File summary: {filePath}\nSymbols: {string.Join(", ", symbolLabels)}",
                SearchText = $"{filePath} {string.Join(' ', symbolLabels)}"
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
                Content = $"Namespace: {namespaceName}",
                SearchText = namespaceName
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
}
