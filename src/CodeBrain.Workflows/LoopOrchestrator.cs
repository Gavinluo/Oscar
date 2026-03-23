using System.Text;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

public sealed class LoopOrchestrator : IOrchestrator
{
    private readonly RepoMapperAgent _repoMapper;
    private readonly UnderstanderAgent _understander;
    private readonly DrilldownNavigatorAgent _drilldown;
    private readonly ChangeScopeAgent _changeScope;
    private readonly EditPlannerAgent _editPlanner;
    private readonly TestPlannerAgent _planner;
    private readonly TestWriterAgent _writer;
    private readonly RunnerAgent _runner;
    private readonly CoverageAgent _coverage;
    private readonly MemoryAgent _memory;
    private readonly IArtifactStore _artifactStore;

    public LoopOrchestrator(
        RepoMapperAgent repoMapper,
        UnderstanderAgent understander,
        DrilldownNavigatorAgent drilldown,
        ChangeScopeAgent changeScope,
        EditPlannerAgent editPlanner,
        TestPlannerAgent planner,
        TestWriterAgent writer,
        RunnerAgent runner,
        CoverageAgent coverage,
        MemoryAgent memory,
        IArtifactStore artifactStore)
    {
        _repoMapper = repoMapper;
        _understander = understander;
        _drilldown = drilldown;
        _changeScope = changeScope;
        _editPlanner = editPlanner;
        _planner = planner;
        _writer = writer;
        _runner = runner;
        _coverage = coverage;
        _memory = memory;
        _artifactStore = artifactStore;
    }

    public async Task<OrchestrationResult> RunAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        await _repoMapper.ExecuteAsync(context, cancellationToken);

        while (context.State.TargetSymbolsQueue.Count > 0)
        {
            var symbol = context.State.TargetSymbolsQueue.Dequeue();
            context.Items[PipelineKeys.CurrentSymbol] = symbol;
            context.State.CurrentDepth = 0;

            for (var iteration = 1; iteration <= context.State.IterationBudget; iteration++)
            {
                context.State.IterationCount = iteration;
                await _understander.ExecuteAsync(context, cancellationToken);
                await _drilldown.ExecuteAsync(context, cancellationToken);
                await _changeScope.ExecuteAsync(context, cancellationToken);
                await _editPlanner.ExecuteAsync(context, cancellationToken);
                await _planner.ExecuteAsync(context, cancellationToken);
                await _writer.ExecuteAsync(context, cancellationToken);
                await _runner.ExecuteAsync(context, cancellationToken);

                var runResult = context.Items[PipelineKeys.LatestTestRun] as TestRunResult;
                if (runResult is null || !runResult.Success)
                {
                    var reason = AnalyzeFailure(runResult);
                    context.Items[PipelineKeys.LastBlocker] = reason;
                    await _artifactStore.WriteLogAsync(
                        $"logs/iteration_{_artifactStore.GetSafeArtifactName(symbol)}_{iteration}.md",
                        $"Iteration failed during test execution.\n\nReason: {reason}",
                        cancellationToken);

                    if (iteration == context.State.IterationBudget)
                    {
                        blockers.Add($"Symbol={symbol}; {reason}");
                        break;
                    }

                    continue;
                }

                await _coverage.ExecuteAsync(context, cancellationToken);
                var coverage = context.Items[PipelineKeys.LatestCoverage] as CoverageSummary;
                if (coverage is null)
                {
                    blockers.Add($"Symbol={symbol}; coverage summary missing.");
                    break;
                }

                if (coverage.Line < context.State.CoverageThresholds.Line ||
                    coverage.Branch < context.State.CoverageThresholds.Branch)
                {
                    await _artifactStore.WriteLogAsync(
                        $"logs/iteration_{_artifactStore.GetSafeArtifactName(symbol)}_{iteration}.md",
                        $"Coverage below threshold: line={coverage.Line:F2}, branch={coverage.Branch:F2}.",
                        cancellationToken);
                    if (iteration == context.State.IterationBudget)
                    {
                        blockers.Add(
                            $"Symbol={symbol}; coverage below threshold (line={coverage.Line:F2}, branch={coverage.Branch:F2}).");
                        break;
                    }

                    continue;
                }

                await _memory.ExecuteAsync(context, cancellationToken);
                context.State.CompletedSymbols.Add(symbol);
                break;
            }
        }

        var success = blockers.Count == 0;
        var summary = BuildSummary(context, success, blockers);
        await _artifactStore.WriteLogAsync("reports/test_runs/latest.md", summary, cancellationToken);
        return new OrchestrationResult
        {
            Success = success,
            Summary = summary,
            Blockers = blockers
        };
    }

    private static string AnalyzeFailure(TestRunResult? runResult)
    {
        if (runResult is null)
        {
            return "No test result available.";
        }

        if (runResult.Failures.Count == 0)
        {
            return "dotnet test reported failure but no parsed failure details.";
        }

        return runResult.Failures[0].Message;
    }

    private static string BuildSummary(PipelineContext context, bool success, IReadOnlyCollection<string> blockers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CodeBrain Closed-loop Summary");
        sb.AppendLine();
        sb.AppendLine($"- Success: {success}");
        sb.AppendLine($"- Completed symbols: {context.State.CompletedSymbols.Count}");
        sb.AppendLine($"- Iteration budget: {context.State.IterationBudget}");
        sb.AppendLine($"- Coverage threshold (line/branch): {context.State.CoverageThresholds.Line:F2}/{context.State.CoverageThresholds.Branch:F2}");
        if (blockers.Count > 0)
        {
            sb.AppendLine("- Blockers:");
            foreach (var blocker in blockers)
            {
                sb.AppendLine($"  - {blocker}");
            }
        }

        if (context.Items[PipelineKeys.LatestCoverage] is CoverageSummary coverage)
        {
            sb.AppendLine($"- Latest coverage: line={coverage.Line:F2}, branch={coverage.Branch:F2}");
            foreach (var hotspot in coverage.Hotspots.Take(5))
            {
                sb.AppendLine($"  - Hotspot: {hotspot.FileOrType} (line={hotspot.LineCoverage:F2}, branch={hotspot.BranchCoverage:F2})");
            }
        }

        if (context.Items[PipelineKeys.CurrentEditPlan] is RepositoryEditPlan editPlan)
        {
            sb.AppendLine($"- Latest edit goal: {editPlan.Goal}");
            foreach (var file in editPlan.FilesToInspect.Take(3))
            {
                sb.AppendLine($"  - Inspect: {file}");
            }
        }

        return sb.ToString();
    }
}
