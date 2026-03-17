using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;

namespace AgenticTestLoop.Agents;

public sealed class UnderstanderAgent : IAgent
{
    private readonly IRepoMapService _repoMapService;
    private readonly IUnderstandingStorage _storage;
    private readonly IArtifactStore _artifactStore;
    private readonly ILLMProvider _llmProvider;

    public UnderstanderAgent(
        IRepoMapService repoMapService,
        IUnderstandingStorage storage,
        IArtifactStore artifactStore,
        ILLMProvider llmProvider)
    {
        _repoMapService = repoMapService;
        _storage = storage;
        _artifactStore = artifactStore;
        _llmProvider = llmProvider;
    }

    public string Name => nameof(UnderstanderAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var query = context.Items[PipelineKeys.CurrentSymbol] as string
                    ?? throw new InvalidOperationException("Current symbol not set.");
        var resolved = await _repoMapService.ResolveSymbolAsync(query, cancellationToken)
                       ?? throw new InvalidOperationException($"Unable to resolve symbol '{query}'.");
        var symbol = resolved.Symbol;
        context.Items[PipelineKeys.CurrentSymbol] = symbol;
        var deps = resolved.Dependencies.Select(dep => dep.Symbol).ToList();
        var llmSummary = await BuildPurposeAsync(resolved, cancellationToken);

        var card = new UnderstandingCard
        {
            Symbol = symbol,
            Purpose = llmSummary,
            Dependencies = deps,
            InputsPreconditions = new() { "Input constraints should follow the method signature and loop bounds." },
            OutputsPostconditions = new() { "Return value should match the method body behavior observed in source." },
            SideEffects = new() { "No external side effects unless dependency evidence proves otherwise." },
            Branching = new() { "Loop and guard conditions are inferred from source evidence." },
            TestSeams = new() { "Static randomness may need assertion by shape rather than exact value." },
            Observability = new() { "Assert length, allowed character set, and exception behavior." },
            Evidence = BuildEvidence(resolved),
            Status = CardStatus.Draft
        };

        context.Items[PipelineKeys.CurrentCard] = card;
        await _storage.SaveDraftAsync(card, cancellationToken);
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.understanding.json",
            card,
            cancellationToken);
    }

    private async Task<string> BuildPurposeAsync(ResolvedSymbol resolved, CancellationToken cancellationToken)
    {
        if (!string.Equals(_llmProvider.Name, "dummy", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await _llmProvider.ChatCompletionAsync(
                    $"Summarize purpose and test seams for symbol: {resolved.Symbol}",
                    string.Join(Environment.NewLine, resolved.Dependencies.Select(dep => dep.Symbol)),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var heuristic = await BuildHeuristicPurposeAsync(resolved, cancellationToken);
                return $"{heuristic} LLM fallback reason: {ex.GetType().Name}.";
            }
        }

        return await BuildHeuristicPurposeAsync(resolved, cancellationToken);
    }

    private static async Task<string> BuildHeuristicPurposeAsync(ResolvedSymbol resolved, CancellationToken cancellationToken)
    {
        if (resolved.Location is null || !File.Exists(resolved.Location.FilePath))
        {
            return $"Heuristic summary unavailable for {resolved.Symbol}.";
        }

        var lines = await File.ReadAllLinesAsync(resolved.Location.FilePath, cancellationToken);
        var start = Math.Max(0, resolved.Location.StartLine - 1);
        var end = Math.Min(lines.Length, resolved.Location.EndLine);
        var snippet = string.Join(Environment.NewLine, lines[start..end]);
        var statements = new List<string>();

        if (snippet.Contains("RandomNumberGenerator.GetInt32", StringComparison.Ordinal))
        {
            statements.Add("uses cryptographic randomness");
        }

        if (snippet.Contains("StringBuilder", StringComparison.Ordinal))
        {
            statements.Add("builds the output incrementally");
        }

        if (snippet.Contains("for (", StringComparison.Ordinal))
        {
            statements.Add("iterates based on the input length");
        }

        if (snippet.Contains("return", StringComparison.Ordinal))
        {
            statements.Add("returns the constructed value directly");
        }

        if (statements.Count == 0)
        {
            statements.Add("matches the source implementation observed in the target method");
        }

        return $"{resolved.Symbol} {string.Join(", ", statements)}.";
    }

    private static List<UnderstandingEvidence> BuildEvidence(ResolvedSymbol resolved)
    {
        var evidence = new List<UnderstandingEvidence>();
        if (resolved.Location is not null)
        {
            evidence.Add(new UnderstandingEvidence
            {
                FilePath = resolved.Location.FilePath,
                LineRange = $"{resolved.Location.StartLine}-{resolved.Location.EndLine}",
                Summary = $"Resolved target symbol {resolved.Symbol} from source."
            });
        }

        foreach (var dependency in resolved.Dependencies)
        {
            evidence.Add(new UnderstandingEvidence
            {
                FilePath = dependency.Location?.FilePath ?? "unknown",
                LineRange = dependency.Location is null
                    ? "N/A"
                    : $"{dependency.Location.StartLine}-{dependency.Location.EndLine}",
                Summary = $"Direct dependency {dependency.Symbol}",
                IsUncertain = dependency.IsDynamicEdge,
                UncertaintyReason = dependency.UncertaintyReason
            });
        }

        return evidence;
    }
}
