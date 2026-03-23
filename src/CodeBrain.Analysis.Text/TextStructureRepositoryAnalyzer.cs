using System.Text.RegularExpressions;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Analysis.Text;

/// <summary>
/// Provides a lightweight second analyzer backend for non-C# repositories. It
/// indexes files, extracts simple declarations/imports, and emits a structural
/// graph that is sufficient for retrieval and impact-style navigation.
/// </summary>
public sealed partial class TextStructureRepositoryAnalyzer : IRepositoryAnalyzer
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rs", ".md", ".json"
    };

    public string Id => "text-structure";

    public bool CanHandle(RegisteredRepository repository)
    {
        return !string.Equals(repository.PrimaryLanguage, "csharp", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RepositoryAnalysisResult> AnalyzeAsync(
        RepositoryAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var files = await RepositoryFileScanner.ScanAsync(request.Repository.RootPath, cancellationToken);
        var selectedFiles = files
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file.RelativePath)))
            .ToList();

        var map = new RepositoryMap
        {
            Projects = selectedFiles.Select(file => file.RelativePath).ToList(),
            Namespaces = new List<string>(),
            EntryTypes = new List<string>()
        };

        var graph = new RepositoryKnowledgeGraph
        {
            RepositoryRoot = request.Repository.RootPath,
            SourcePath = request.Repository.RootPath
        };
        var documents = new List<RepositoryIndexDocument>();
        var knownSymbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in selectedFiles)
        {
            var fullPath = Path.Combine(request.Repository.RootPath, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
            var fileNodeId = $"file::{file.RelativePath}";
            graph.Nodes.Add(new KnowledgeNode
            {
                Id = fileNodeId,
                Kind = "file",
                Label = Path.GetFileName(file.RelativePath),
                FilePath = fullPath,
                Project = file.RelativePath,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["language"] = file.Language
                }
            });

            var content = string.Join(Environment.NewLine, lines.Take(80));
            documents.Add(new RepositoryIndexDocument
            {
                RepositoryId = request.Repository.Id,
                Kind = "file",
                Title = Path.GetFileName(file.RelativePath),
                FilePath = fullPath,
                Language = request.Repository.PrimaryLanguage,
                Content = $"File: {file.RelativePath}{Environment.NewLine}{content}",
                SearchText = $"{file.RelativePath} {content}"
            });

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var symbol = TryExtractSymbol(line);
                if (symbol is null)
                {
                    continue;
                }

                var qualifiedSymbol = $"{file.RelativePath}::{symbol}";
                knownSymbols[symbol] = qualifiedSymbol;
                map.CallGraph.Callees.TryAdd(qualifiedSymbol, new HashSet<string>(StringComparer.Ordinal));
                map.CallGraph.Callers.TryAdd(qualifiedSymbol, new HashSet<string>(StringComparer.Ordinal));
                map.CallGraph.Locations[qualifiedSymbol] = new SymbolLocation
                {
                    FilePath = fullPath,
                    StartLine = index + 1,
                    EndLine = index + 1
                };

                graph.Nodes.Add(new KnowledgeNode
                {
                    Id = $"symbol::{qualifiedSymbol}",
                    Kind = "symbol",
                    Label = symbol,
                    Symbol = qualifiedSymbol,
                    FilePath = fullPath,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    Namespace = Path.GetDirectoryName(file.RelativePath)
                });

                graph.Edges.Add(new KnowledgeEdge
                {
                    From = fileNodeId,
                    To = $"symbol::{qualifiedSymbol}",
                    Kind = "contains"
                });

                documents.Add(new RepositoryIndexDocument
                {
                    RepositoryId = request.Repository.Id,
                    Kind = "symbol",
                    Title = symbol,
                    Symbol = qualifiedSymbol,
                    FilePath = fullPath,
                    Language = request.Repository.PrimaryLanguage,
                    Content = $"Symbol: {qualifiedSymbol}{Environment.NewLine}File: {file.RelativePath}{Environment.NewLine}Declaration: {line.Trim()}",
                    SearchText = $"{symbol} {qualifiedSymbol} {line.Trim()} {file.RelativePath}"
                });
            }
        }

        foreach (var file in selectedFiles)
        {
            var fullPath = Path.Combine(request.Repository.RootPath, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var fileNodeId = $"file::{file.RelativePath}";
            foreach (var line in await File.ReadAllLinesAsync(fullPath, cancellationToken))
            {
                var importTarget = TryExtractImport(line);
                if (importTarget is null)
                {
                    continue;
                }

                if (knownSymbols.TryGetValue(importTarget, out var symbolTarget))
                {
                    graph.Edges.Add(new KnowledgeEdge
                    {
                        From = fileNodeId,
                        To = $"symbol::{symbolTarget}",
                        Kind = "references",
                        IsUncertain = true,
                        Reason = "matched from text import/use statement"
                    });
                }
            }
        }

        graph.Summary = new GraphSummary
        {
            NodeCount = graph.Nodes.Count,
            EdgeCount = graph.Edges.Count,
            ProjectCount = selectedFiles.Count,
            SymbolCount = graph.Nodes.Count(node => node.Kind == "symbol")
        };

        var manifest = new RepositoryIndexManifest
        {
            RepositoryId = request.Repository.Id,
            RepositoryRoot = request.Repository.RootPath,
            AnalyzerId = Id,
            PrimaryLanguage = request.Repository.PrimaryLanguage,
            IndexedAt = DateTimeOffset.UtcNow,
            HeadCommit = request.ChangeSet.HeadCommit,
            ChangeDetectionMode = request.ChangeSet.DetectionMode,
            Files = files
        };

        return new RepositoryAnalysisResult
        {
            Map = map,
            Graph = graph,
            Documents = documents,
            Manifest = manifest
        };
    }

    private static string? TryExtractImport(string line)
    {
        var importMatch = ImportRegex().Match(line);
        if (importMatch.Success)
        {
            return importMatch.Groups["name"].Value;
        }

        var useMatch = UseRegex().Match(line);
        return useMatch.Success ? useMatch.Groups["name"].Value : null;
    }

    private static string? TryExtractSymbol(string line)
    {
        var match = SymbolRegex().Match(line);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:class|interface|function|def|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"^\s*(?:import|from)\s+.*?(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"^\s*(?:use|using)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex UseRegex();
}
