using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

public sealed class RunnerAgent : IAgent
{
    private readonly ITestExecutionService _testExecutionService;
    private readonly IArtifactStore _artifactStore;

    public RunnerAgent(ITestExecutionService testExecutionService, IArtifactStore artifactStore)
    {
        _testExecutionService = testExecutionService;
        _artifactStore = artifactStore;
    }

    public string Name => nameof(RunnerAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var testProjectPath = context.Items[PipelineKeys.TestProjectPath] as string
                              ?? throw new InvalidOperationException("Test project path missing.");
        var resultsDir = _artifactStore.EnsureDirectory("reports", "test_runs", "raw");
        var result = await _testExecutionService.RunTestsAsync(testProjectPath, resultsDir, cancellationToken);
        context.Items[PipelineKeys.LatestTestRun] = result;
        await _artifactStore.WriteJsonAsync("reports/test_runs/latest.json", result, cancellationToken);
    }
}

public sealed class CoverageAgent : IAgent
{
    private readonly ICoverageService _coverageService;
    private readonly IArtifactStore _artifactStore;

    public CoverageAgent(ICoverageService coverageService, IArtifactStore artifactStore)
    {
        _coverageService = coverageService;
        _artifactStore = artifactStore;
    }

    public string Name => nameof(CoverageAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var testProjectPath = context.Items[PipelineKeys.TestProjectPath] as string
                              ?? throw new InvalidOperationException("Test project path missing.");
        var coverageRawDir = _artifactStore.EnsureDirectory("reports", "coverage", "raw");
        var coverage = await _coverageService.CollectAsync(testProjectPath, coverageRawDir, cancellationToken);
        context.Items[PipelineKeys.LatestCoverage] = coverage;
        await _artifactStore.WriteJsonAsync("reports/coverage/summary.json", coverage, cancellationToken);
    }
}

public sealed class MemoryAgent : IAgent
{
    private readonly IUnderstandingStorage _storage;
    private readonly IArtifactStore _artifactStore;

    public MemoryAgent(IUnderstandingStorage storage, IArtifactStore artifactStore)
    {
        _storage = storage;
        _artifactStore = artifactStore;
    }

    public string Name => nameof(MemoryAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        if (context.Items[PipelineKeys.CurrentCard] is not UnderstandingCard card)
        {
            return;
        }

        await _storage.SaveDraftAsync(card, cancellationToken);

        var run = context.Items[PipelineKeys.LatestTestRun] as TestRunResult;
        var coverage = context.Items[PipelineKeys.LatestCoverage] as CoverageSummary;
        if (run is null || coverage is null)
        {
            return;
        }

        if (run.Success &&
            context.State.CoverageThresholds.Line > 0 &&
            context.State.CoverageThresholds.Branch > 0 &&
            coverage.Line >= context.State.CoverageThresholds.Line &&
            coverage.Branch >= context.State.CoverageThresholds.Branch)
        {
            var verification = new VerificationInfo
            {
                Tests = run.Failures.Count == 0
                    ? new List<string> { "dotnet test (all green)" }
                    : run.Failures.Select(f => f.TestName).Distinct(StringComparer.Ordinal).ToList(),
                Coverage = coverage,
                Timestamp = DateTimeOffset.UtcNow,
                Commit = Environment.GetEnvironmentVariable("GIT_COMMIT")
            };

            await _storage.PromoteToStableAsync(card, verification, cancellationToken);
            await _artifactStore.WriteLogAsync(
                $"logs/memory_upgrade.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.md",
                $"Promoted symbol {card.Symbol} to stable.",
                cancellationToken);
        }
    }
}
