using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

public sealed class RepoMapperAgent : IAgent
{
    private readonly IRepoMapService _repoMapService;
    private readonly IArtifactStore _artifactStore;

    public RepoMapperAgent(IRepoMapService repoMapService, IArtifactStore artifactStore)
    {
        _repoMapService = repoMapService;
        _artifactStore = artifactStore;
    }

    public string Name => nameof(RepoMapperAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var map = await _repoMapService.BuildMapAsync(context.SolutionOrProjectPath, cancellationToken);
        var knowledgeGraph = await _repoMapService.BuildKnowledgeGraphAsync(context.SolutionOrProjectPath, cancellationToken);
        context.Items[PipelineKeys.RepoMap] = map;
        context.Items["repo.knowledgeGraph"] = knowledgeGraph;
        await _artifactStore.WriteJsonAsync("logs/repo_map.json", map, cancellationToken);
        await _artifactStore.WriteJsonAsync("index/graph.latest.json", knowledgeGraph, cancellationToken);
        if (_artifactStore is IKnowledgeGraphStore graphStore)
        {
            await graphStore.SaveGraphAsync(knowledgeGraph, cancellationToken);
        }
    }
}
