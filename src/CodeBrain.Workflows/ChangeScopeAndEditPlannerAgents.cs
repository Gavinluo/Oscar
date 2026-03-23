using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

/// <summary>
/// Narrows the candidate modification surface before the test planner runs so
/// the closed-loop pipeline can reason about a bounded edit area.
/// </summary>
public sealed class ChangeScopeAgent : IAgent
{
    private readonly IArtifactStore _artifactStore;

    public ChangeScopeAgent(IArtifactStore artifactStore)
    {
        _artifactStore = artifactStore;
    }

    public string Name => nameof(ChangeScopeAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var symbol = context.Items[PipelineKeys.CurrentSymbol] as string
                     ?? throw new InvalidOperationException("Current symbol not set.");
        var drilldown = context.Items[PipelineKeys.CurrentDrilldown] as DrilldownNode
                        ?? throw new InvalidOperationException("Current drilldown not set.");

        var scope = new RepositoryChangeScope
        {
            TargetSymbol = symbol,
            RelatedSymbols = drilldown.SelectedDependencies.Select(dep => dep.Symbol).Distinct(StringComparer.Ordinal).Take(8).ToList(),
            ImpactedFiles = drilldown.SelectedDependencies
                .Select(dep => ExtractFileHint(dep.Symbol))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            VerificationTargets = drilldown.SelectedDependencies
                .Select(dep => $"symbol:{dep.Symbol}")
                .Take(8)
                .ToList(),
            ScopeReason = $"Derived from drilldown for {symbol} using {drilldown.SelectedDependencies.Count} selected dependencies."
        };

        context.Items[PipelineKeys.CurrentChangeScope] = scope;
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.changescope.json",
            scope,
            cancellationToken);
    }

    private static string? ExtractFileHint(string symbol)
    {
        var lastNamespaceDot = symbol.LastIndexOf('.', symbol.LastIndexOf('.') - 1);
        if (lastNamespaceDot <= 0)
        {
            return null;
        }

        return symbol[(lastNamespaceDot + 1)..].Split('(')[0];
    }
}

/// <summary>
/// Converts the change scope and understanding card into an explicit edit plan
/// so the modification loop remains inspectable even before automated edits are
/// introduced.
/// </summary>
public sealed class EditPlannerAgent : IAgent
{
    private readonly IArtifactStore _artifactStore;

    public EditPlannerAgent(IArtifactStore artifactStore)
    {
        _artifactStore = artifactStore;
    }

    public string Name => nameof(EditPlannerAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var symbol = context.Items[PipelineKeys.CurrentSymbol] as string
                     ?? throw new InvalidOperationException("Current symbol not set.");
        var scope = context.Items[PipelineKeys.CurrentChangeScope] as RepositoryChangeScope
                    ?? throw new InvalidOperationException("Current change scope not set.");
        var card = context.Items[PipelineKeys.CurrentCard] as UnderstandingCard;

        var plan = new RepositoryEditPlan
        {
            Intent = QueryIntent.TestGeneration,
            Goal = $"Modify or validate behavior for {symbol} within the retrieved repository scope.",
            FilesToInspect = scope.ImpactedFiles.Count == 0 ? new List<string> { "inspect target file from symbol location" } : scope.ImpactedFiles,
            SymbolsToEdit = new List<string> { symbol }.Concat(scope.RelatedSymbols).Distinct(StringComparer.Ordinal).Take(8).ToList(),
            VerificationSteps = scope.VerificationTargets.Count == 0
                ? new List<string> { $"Run tests covering {symbol}" }
                : scope.VerificationTargets,
            RiskNotes = card is null
                ? scope.ScopeReason
                : $"{scope.ScopeReason} Purpose summary: {card.Purpose}"
        };

        context.Items[PipelineKeys.CurrentEditPlan] = plan;
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.editplan.json",
            plan,
            cancellationToken);
    }
}
