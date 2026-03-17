namespace AgenticTestLoop.Core.Models;

public sealed class RepositoryKnowledgeGraph
{
    public string RepositoryRoot { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<KnowledgeNode> Nodes { get; set; } = new();
    public List<KnowledgeEdge> Edges { get; set; } = new();
    public GraphSummary Summary { get; set; } = new();
}

public sealed class KnowledgeNode
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Project { get; set; }
    public string? Namespace { get; set; }
    public string? Symbol { get; set; }
    public string? FilePath { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
}

public sealed class KnowledgeEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool IsUncertain { get; set; }
    public string? Reason { get; set; }
}

public sealed class GraphSummary
{
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int ProjectCount { get; set; }
    public int SymbolCount { get; set; }
    public int UnderstandingCardCount { get; set; }
}

public sealed class SymbolContextResult
{
    public string Symbol { get; set; } = string.Empty;
    public KnowledgeNode? Node { get; set; }
    public List<KnowledgeEdge> OutgoingEdges { get; set; } = new();
    public List<KnowledgeEdge> IncomingEdges { get; set; } = new();
    public List<KnowledgeNode> RelatedNodes { get; set; } = new();
    public UnderstandingCard? DraftCard { get; set; }
    public UnderstandingCard? StableCard { get; set; }
}

public sealed class ImpactAnalysisResult
{
    public string Symbol { get; set; } = string.Empty;
    public List<KnowledgeNode> ImpactedNodes { get; set; } = new();
    public List<KnowledgeEdge> TraversedEdges { get; set; } = new();
}

public sealed class GraphQueryOptions
{
    public int Depth { get; set; } = 2;
    public int Limit { get; set; } = 50;
}
