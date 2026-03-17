namespace AgenticTestLoop.Core.Models;

public sealed class DrilldownScoreWeights
{
    public double TestNeed { get; set; } = 1.0;
    public double Uncertainty { get; set; } = 1.0;
    public double Risk { get; set; } = 1.0;
}

public sealed class DrilldownPolicy
{
    public int TopK { get; set; } = 5;
    public int MaxDepth { get; set; } = 3;
    public DrilldownScoreWeights ScoringWeights { get; set; } = new();
    public List<string> IgnoreNamespaces { get; set; } = new() { "System.", "Microsoft." };
}

public sealed class DependencyScore
{
    public string Symbol { get; set; } = string.Empty;
    public int TestNeed { get; set; }
    public int Uncertainty { get; set; }
    public int Risk { get; set; }
    public double Total { get; set; }
    public bool IsDynamicEdge { get; set; }
    public string? Reason { get; set; }
}

public sealed class DrilldownNode
{
    public string Symbol { get; set; } = string.Empty;
    public int Depth { get; set; }
    public List<DependencyScore> SelectedDependencies { get; set; } = new();
    public List<DrilldownNode> Children { get; set; } = new();
}
