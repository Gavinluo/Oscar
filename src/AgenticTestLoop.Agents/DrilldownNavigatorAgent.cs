using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;

namespace AgenticTestLoop.Agents;

public sealed class DrilldownNavigatorAgent : IAgent
{
    private readonly IRepoMapService _repoMapService;
    private readonly IArtifactStore _artifactStore;
    private readonly IUnderstandingStorage _storage;

    public DrilldownNavigatorAgent(IRepoMapService repoMapService, IArtifactStore artifactStore, IUnderstandingStorage storage)
    {
        _repoMapService = repoMapService;
        _artifactStore = artifactStore;
        _storage = storage;
    }

    public string Name => nameof(DrilldownNavigatorAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var symbol = context.Items[PipelineKeys.CurrentSymbol] as string
                     ?? throw new InvalidOperationException("Current symbol not set.");
        var node = await _repoMapService.BuildDrilldownAsync(symbol, context.DrilldownPolicy, cancellationToken);
        context.Items[PipelineKeys.CurrentDrilldown] = node;
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.drilldown.json",
            node,
            cancellationToken);

        if (context.Items[PipelineKeys.CurrentCard] is UnderstandingCard card)
        {
            AppendEvidence(card, node);
            await _storage.SaveDraftAsync(card, cancellationToken);
        }
    }

    private static void AppendEvidence(UnderstandingCard card, DrilldownNode node)
    {
        foreach (var dep in node.SelectedDependencies)
        {
            card.Evidence.Add(new UnderstandingEvidence
            {
                FilePath = "drilldown",
                LineRange = $"Depth={node.Depth}",
                Summary = $"Dependency {dep.Symbol} scored T:{dep.TestNeed} U:{dep.Uncertainty} R:{dep.Risk} from {card.Symbol}",
                IsUncertain = dep.IsDynamicEdge,
                UncertaintyReason = dep.Reason
            });
        }

        foreach (var child in node.Children)
        {
            AppendEvidence(card, child);
        }
    }
}
