using System.Text.Json;
using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;
using AgenticTestLoop.Roslyn;
using AgenticTestLoop.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

var repoRoot = ResolveRepositoryRoot();
var config = LoadConfig(repoRoot);
var solutionPath = Path.GetFullPath(config.Solution);
var repoMapService = new RoslynRepoMapService();
var artifactStore = new FileArtifactStore(repoRoot);
var graphStore = (IKnowledgeGraphStore)artifactStore;

await EnsureGraphAsync(repoMapService, graphStore, solutionPath, repoRoot, app.Lifetime.ApplicationStopping);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/graph/summary", async (CancellationToken cancellationToken) =>
{
    return Results.Ok(await graphStore.GetSummaryAsync(cancellationToken));
});

app.MapGet("/api/graph/nodes", async (string? term, int? limit, CancellationToken cancellationToken) =>
{
    var nodes = await graphStore.SearchNodesAsync(term, Math.Clamp(limit ?? 50, 1, 500), cancellationToken);
    return Results.Ok(nodes);
});

app.MapGet("/api/graph/context", async (string symbol, CancellationToken cancellationToken) =>
{
    var context = await graphStore.GetSymbolContextAsync(symbol, cancellationToken);
    return context is null ? Results.NotFound() : Results.Ok(context);
});

app.MapGet("/api/graph/impact", async (string symbol, int? depth, int? limit, CancellationToken cancellationToken) =>
{
    var result = await graphStore.GetImpactAsync(
        symbol,
        new GraphQueryOptions
        {
            Depth = Math.Clamp(depth ?? 2, 1, 5),
            Limit = Math.Clamp(limit ?? 50, 1, 500)
        },
        cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/graph/rebuild", async (CancellationToken cancellationToken) =>
{
    var graph = await repoMapService.BuildKnowledgeGraphAsync(solutionPath, cancellationToken);
    graph.RepositoryRoot = repoRoot;
    await graphStore.SaveGraphAsync(graph, cancellationToken);
    return Results.Accepted("/api/graph/summary", graph.Summary);
});

app.MapGet("/api/cards", async (string? symbol, string? level, CancellationToken cancellationToken) =>
{
    var cards = new List<UnderstandingCard>();
    if (!string.IsNullOrWhiteSpace(symbol))
    {
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
    }

    return Results.Ok(cards);
});

app.Run();

static string ResolveRepositoryRoot()
{
    var current = AppContext.BaseDirectory;
    var directory = new DirectoryInfo(current);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "agentic_test_loop.config.json")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static AppConfig LoadConfig(string repoRoot)
{
    var path = Path.Combine(repoRoot, "agentic_test_loop.config.json");
    if (!File.Exists(path))
    {
        throw new InvalidOperationException($"Missing config file: {path}");
    }

    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });
    return config ?? throw new InvalidOperationException("Unable to read configuration.");
}

static async Task EnsureGraphAsync(
    IRepoMapService repoMapService,
    IKnowledgeGraphStore graphStore,
    string solutionPath,
    string repoRoot,
    CancellationToken cancellationToken)
{
    var existing = await graphStore.LoadLatestGraphAsync(cancellationToken);
    if (existing is not null)
    {
        return;
    }

    var graph = await repoMapService.BuildKnowledgeGraphAsync(solutionPath, cancellationToken);
    graph.RepositoryRoot = repoRoot;
    await graphStore.SaveGraphAsync(graph, cancellationToken);
}

internal sealed class AppConfig
{
    public string Solution { get; set; } = string.Empty;
}
