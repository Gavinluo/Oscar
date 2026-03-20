using CodeBrain.Core.Models;

namespace CodeBrain.Core.Abstractions;

public interface IRepoMapService
{
    Task<RepositoryMap> BuildMapAsync(string solutionOrProjectPath, CancellationToken cancellationToken);
    Task<RepositoryKnowledgeGraph> BuildKnowledgeGraphAsync(string solutionOrProjectPath, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> GetDependenciesAsync(string symbol, CancellationToken cancellationToken);
    Task<DrilldownNode> BuildDrilldownAsync(string symbol, DrilldownPolicy policy, CancellationToken cancellationToken);
    Task<ResolvedSymbol?> ResolveSymbolAsync(string query, CancellationToken cancellationToken);
}

public interface IUnderstandingStorage
{
    Task SaveDraftAsync(UnderstandingCard card, CancellationToken cancellationToken);
    Task PromoteToStableAsync(UnderstandingCard card, VerificationInfo verification, CancellationToken cancellationToken);
    Task<UnderstandingCard?> LoadLatestAsync(string symbol, CardStatus status, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    string EnsureDirectory(params string[] segments);
    string GetSafeArtifactName(string value);
    Task WriteLogAsync(string relativePath, string content, CancellationToken cancellationToken);
    Task WriteJsonAsync<T>(string relativePath, T model, CancellationToken cancellationToken);
}

public interface IKnowledgeGraphStore
{
    Task SaveGraphAsync(RepositoryKnowledgeGraph graph, CancellationToken cancellationToken);
    Task<RepositoryKnowledgeGraph?> LoadLatestGraphAsync(CancellationToken cancellationToken);
    Task<GraphSummary> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<KnowledgeNode>> SearchNodesAsync(string? term, int limit, CancellationToken cancellationToken);
    Task<SymbolContextResult?> GetSymbolContextAsync(string symbol, CancellationToken cancellationToken);
    Task<ImpactAnalysisResult> GetImpactAsync(string symbol, GraphQueryOptions options, CancellationToken cancellationToken);
}

public interface ITestExecutionService
{
    Task<TestRunResult> RunTestsAsync(string testProjectOrSolution, string resultsDirectory, CancellationToken cancellationToken);
}

public interface ICoverageService
{
    Task<CoverageSummary> CollectAsync(string testProjectOrSolution, string resultsDirectory, CancellationToken cancellationToken);
}
