namespace AgenticTestLoop.Core.Models;

public sealed class CoverageThresholds
{
    public double Line { get; set; } = 0.6;
    public double Branch { get; set; } = 0.4;
}

public sealed class PipelineState
{
    public Queue<string> TargetSymbolsQueue { get; set; } = new();
    public HashSet<string> CompletedSymbols { get; set; } = new(StringComparer.Ordinal);
    public int CurrentDepth { get; set; }
    public CoverageThresholds CoverageThresholds { get; set; } = new();
    public int IterationBudget { get; set; } = 10;
    public int IterationCount { get; set; }
}

public sealed class PipelineContext
{
    public required string RepositoryRoot { get; init; }
    public required string SolutionOrProjectPath { get; init; }
    public required PipelineState State { get; init; }
    public required DrilldownPolicy DrilldownPolicy { get; init; }
    public required string ProviderName { get; init; }
    public string ArtifactsRoot { get; init; } = "agent_artifacts";
    public Dictionary<string, object> Items { get; } = new(StringComparer.Ordinal);
}
