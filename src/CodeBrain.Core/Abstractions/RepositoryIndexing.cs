using CodeBrain.Core.Models;

namespace CodeBrain.Core.Abstractions;

public interface IRepositoryCatalog
{
    Task<RegisteredRepository> RegisterAsync(string rootPath, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RegisteredRepository>> ListAsync(CancellationToken cancellationToken);
    Task<RegisteredRepository?> GetAsync(string repositoryId, CancellationToken cancellationToken);
}

public interface IRepositoryAnalyzer
{
    string Id { get; }
    bool CanHandle(RegisteredRepository repository);
    Task<RepositoryAnalysisResult> AnalyzeAsync(RepositoryAnalysisRequest request, CancellationToken cancellationToken);
}

public interface IIncrementalIndexPlanner
{
    Task<RepositoryChangeSet> PlanAsync(RegisteredRepository repository, RepositoryIndexManifest? existingManifest, CancellationToken cancellationToken);
}

public interface IRepositoryIndexStore
{
    Task SaveAsync(RepositoryIndex index, CancellationToken cancellationToken);
    Task<RepositoryIndex?> LoadAsync(string repositoryId, CancellationToken cancellationToken);
    Task<RepositoryIndexManifest?> LoadManifestAsync(string repositoryId, CancellationToken cancellationToken);
}

public interface IRepositoryQueryService
{
    Task<RepositoryQueryResult> QueryAsync(RepositoryQuery query, CancellationToken cancellationToken);
}
