using CodeBrain.Analysis.CSharp;
using CodeBrain.Analysis.Text;
using CodeBrain.Core.Models;
using CodeBrain.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

var workspaceRoot = ResolveCodeBrainWorkspaceRoot();
var catalog = new SqliteRepositoryCatalog(workspaceRoot);
var planner = new LocalFileIncrementalIndexPlanner();
var indexStore = new SqliteRepositoryIndexStore(workspaceRoot);
var artifactStore = new FileArtifactStore(workspaceRoot);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/repos", async (CancellationToken cancellationToken) =>
{
    return Results.Ok(await catalog.ListAsync(cancellationToken));
});

app.MapPost("/api/repos", async (RegisterRepositoryRequest request, CancellationToken cancellationToken) =>
{
    var repository = await catalog.RegisterAsync(request.Path, cancellationToken);
    return Results.Ok(repository);
});

app.MapPost("/api/index/{repositoryId}", async (string repositoryId, CancellationToken cancellationToken) =>
{
    var repository = await catalog.GetAsync(repositoryId, cancellationToken);
    if (repository is null)
    {
        return Results.NotFound();
    }

    var analyzer = ResolveAnalyzer(repository);
    var manifest = await indexStore.LoadManifestAsync(repositoryId, cancellationToken);
    var changeSet = await planner.PlanAsync(repository, manifest, cancellationToken);
    var analysis = await analyzer.AnalyzeAsync(new RepositoryAnalysisRequest
    {
        Repository = repository,
        ChangeSet = changeSet
    }, cancellationToken);

    repository.LastIndexedAt = analysis.Manifest.IndexedAt;
    await indexStore.SaveAsync(new RepositoryIndex
    {
        Repository = repository,
        Manifest = analysis.Manifest,
        Graph = analysis.Graph,
        Documents = analysis.Documents
    }, cancellationToken);
    await catalog.UpdateLastIndexedAsync(repository.Id, analysis.Manifest.IndexedAt, cancellationToken);

    return Results.Ok(new
    {
        repository = repository.Id,
        indexedAt = analysis.Manifest.IndexedAt,
        analyzer = analyzer.Id,
        detectionMode = changeSet.DetectionMode,
        headCommit = changeSet.HeadCommit,
        files = analysis.Manifest.Files.Count,
        documents = analysis.Documents.Count,
        graph = analysis.Graph.Summary
    });
});

app.MapPost("/api/query", async (RepositoryQueryRequest request, CancellationToken cancellationToken) =>
{
    var result = await indexStore.QueryAsync(new RepositoryQuery
    {
        Text = request.Query,
        Intent = ParseIntent(request.Intent),
        RepositoryIds = request.Repositories,
        Limit = request.Limit,
        GraphDepth = request.GraphDepth
    }, cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/api/graph/summary", async (string repositoryId, CancellationToken cancellationToken) =>
{
    var index = await indexStore.LoadAsync(repositoryId, cancellationToken);
    return index is null ? Results.NotFound() : Results.Ok(index.Graph.Summary);
});

app.MapGet("/api/graph/nodes", async (string repositoryId, string? term, int? limit, CancellationToken cancellationToken) =>
{
    var index = await indexStore.LoadAsync(repositoryId, cancellationToken);
    if (index is null)
    {
        return Results.NotFound();
    }

    var nodes = index.Graph.Nodes
        .Where(node => string.IsNullOrWhiteSpace(term) ||
                       node.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                       (node.Symbol?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (node.Namespace?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
        .Take(Math.Clamp(limit ?? 50, 1, 500))
        .ToList();
    return Results.Ok(nodes);
});

app.MapGet("/api/graph/context", async (string repositoryId, string symbol, CancellationToken cancellationToken) =>
{
    var index = await indexStore.LoadAsync(repositoryId, cancellationToken);
    if (index is null)
    {
        return Results.NotFound();
    }

    var node = index.Graph.Nodes.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.Ordinal));
    if (node is null)
    {
        return Results.NotFound();
    }

    var outgoing = index.Graph.Edges.Where(edge => edge.From == node.Id).ToList();
    var incoming = index.Graph.Edges.Where(edge => edge.To == node.Id).ToList();
    var relatedIds = outgoing.Select(edge => edge.To).Concat(incoming.Select(edge => edge.From)).Distinct(StringComparer.Ordinal);
    var relatedNodes = index.Graph.Nodes.Where(item => relatedIds.Contains(item.Id, StringComparer.Ordinal)).ToList();

    return Results.Ok(new SymbolContextResult
    {
        Symbol = symbol,
        Node = node,
        OutgoingEdges = outgoing,
        IncomingEdges = incoming,
        RelatedNodes = relatedNodes,
        DraftCard = await artifactStore.LoadLatestAsync(symbol, CardStatus.Draft, cancellationToken),
        StableCard = await artifactStore.LoadLatestAsync(symbol, CardStatus.Stable, cancellationToken)
    });
});

app.MapGet("/api/graph/impact", async (string repositoryId, string symbol, int? depth, int? limit, CancellationToken cancellationToken) =>
{
    var index = await indexStore.LoadAsync(repositoryId, cancellationToken);
    if (index is null)
    {
        return Results.NotFound();
    }

    var start = index.Graph.Nodes.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.Ordinal));
    if (start is null)
    {
        return Results.NotFound();
    }

    var maxDepth = Math.Clamp(depth ?? 2, 1, 5);
    var maxResults = Math.Clamp(limit ?? 50, 1, 500);
    var queue = new Queue<(string NodeId, int Depth)>();
    var visited = new HashSet<string>(StringComparer.Ordinal) { start.Id };
    var impactedNodes = new List<KnowledgeNode>();
    var traversedEdges = new List<KnowledgeEdge>();

    queue.Enqueue((start.Id, 0));
    while (queue.Count > 0)
    {
        var (nodeId, currentDepth) = queue.Dequeue();
        if (currentDepth >= maxDepth)
        {
            continue;
        }

        foreach (var edge in index.Graph.Edges.Where(item => item.From == nodeId || item.To == nodeId))
        {
            traversedEdges.Add(edge);
            var nextId = edge.From == nodeId ? edge.To : edge.From;
            if (!visited.Add(nextId))
            {
                continue;
            }

            var nextNode = index.Graph.Nodes.FirstOrDefault(item => item.Id == nextId);
            if (nextNode is null)
            {
                continue;
            }

            impactedNodes.Add(nextNode);
            if (impactedNodes.Count >= maxResults)
            {
                break;
            }

            queue.Enqueue((nextId, currentDepth + 1));
        }
    }

    return Results.Ok(new ImpactAnalysisResult
    {
        Symbol = symbol,
        ImpactedNodes = impactedNodes.Take(maxResults).ToList(),
        TraversedEdges = traversedEdges
    });
});

app.MapGet("/api/cards", async (string symbol, string? level, CancellationToken cancellationToken) =>
{
    var cards = new List<UnderstandingCard>();
    if (!string.Equals(level, "stable", StringComparison.OrdinalIgnoreCase))
    {
        var draft = await artifactStore.LoadLatestAsync(symbol, CardStatus.Draft, cancellationToken);
        if (draft is not null)
        {
            cards.Add(draft);
        }
    }

    if (!string.Equals(level, "draft", StringComparison.OrdinalIgnoreCase))
    {
        var stable = await artifactStore.LoadLatestAsync(symbol, CardStatus.Stable, cancellationToken);
        if (stable is not null)
        {
            cards.Add(stable);
        }
    }

    return Results.Ok(cards);
});

app.MapGet("/api/source/snippet", async (string repositoryId, string? symbol, string? filePath, CancellationToken cancellationToken) =>
{
    var index = await indexStore.LoadAsync(repositoryId, cancellationToken);
    if (index is null)
    {
        return Results.NotFound();
    }

    KnowledgeNode? node = null;
    if (!string.IsNullOrWhiteSpace(symbol))
    {
        node = index.Graph.Nodes.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.Ordinal));
    }
    else if (!string.IsNullOrWhiteSpace(filePath))
    {
        node = index.Graph.Nodes
            .Where(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.StartLine ?? int.MaxValue)
            .FirstOrDefault();
    }

    if (node is null || string.IsNullOrWhiteSpace(node.FilePath) || !File.Exists(node.FilePath))
    {
        return Results.NotFound();
    }

    var allLines = await File.ReadAllLinesAsync(node.FilePath, cancellationToken);
    var startLine = Math.Max(1, (node.StartLine ?? 1) - 4);
    var endLine = Math.Min(allLines.Length, (node.EndLine ?? Math.Min(allLines.Length, startLine + 24)) + 4);
    var snippet = allLines[(startLine - 1)..endLine];

    return Results.Ok(new SourceSnippetResult(
        node.FilePath,
        startLine,
        endLine,
        string.Join(Environment.NewLine, snippet)));
});

app.Run();

static string ResolveCodeBrainWorkspaceRoot()
{
    var fromBaseDirectory = FindByMarker(AppContext.BaseDirectory, "CodeBrain.sln");
    if (fromBaseDirectory is not null)
    {
        return fromBaseDirectory;
    }

    var fromCurrentDirectory = FindByMarker(Directory.GetCurrentDirectory(), "CodeBrain.sln");
    if (fromCurrentDirectory is not null)
    {
        return fromCurrentDirectory;
    }

    throw new InvalidOperationException("Unable to locate the CodeBrain workspace root.");
}

static string? FindByMarker(string startPath, string markerFileName)
{
    var directory = new DirectoryInfo(startPath);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, markerFileName)))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}

static QueryIntent ParseIntent(string? raw)
{
    return raw?.Trim().ToLowerInvariant() switch
    {
        "symbol" or "symbollookup" => QueryIntent.SymbolLookup,
        "impact" or "impactanalysis" => QueryIntent.ImpactAnalysis,
        "test" or "testgeneration" => QueryIntent.TestGeneration,
        "bug" or "buglocalization" => QueryIntent.BugLocalization,
        _ => QueryIntent.CodeQa
    };
}

static CodeBrain.Core.Abstractions.IRepositoryAnalyzer ResolveAnalyzer(RegisteredRepository repository)
{
    CodeBrain.Core.Abstractions.IRepositoryAnalyzer[] analyzers =
    [
        new CSharpRepositoryAnalyzer(),
        new TextStructureRepositoryAnalyzer()
    ];

    return analyzers.FirstOrDefault(analyzer =>
               string.Equals(analyzer.Id, repository.AnalyzerId, StringComparison.OrdinalIgnoreCase) &&
               analyzer.CanHandle(repository))
           ?? analyzers.First(analyzer => analyzer.CanHandle(repository));
}

internal sealed record RegisterRepositoryRequest(string Path);

internal sealed record RepositoryQueryRequest(string Query, List<string> Repositories, string? Intent, int Limit = 10, int GraphDepth = 2);

internal sealed record SourceSnippetResult(string FilePath, int StartLine, int EndLine, string Content);
