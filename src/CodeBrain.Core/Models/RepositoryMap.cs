namespace CodeBrain.Core.Models;

public sealed class SymbolCallGraph
{
    public Dictionary<string, HashSet<string>> Callees { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Callers { get; set; } = new(StringComparer.Ordinal);
    public List<DynamicEdge> DynamicEdges { get; set; } = new();
    public Dictionary<string, SymbolLocation> Locations { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DynamicEdge
{
    public string Caller { get; set; } = string.Empty;
    public string DeclaredTarget { get; set; } = string.Empty;
    public List<string> CandidateImplementations { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public sealed class RepositoryMap
{
    public List<string> Projects { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
    public List<string> EntryTypes { get; set; } = new();
    public SymbolCallGraph CallGraph { get; set; } = new();
}

public sealed class SymbolLocation
{
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

public sealed class ResolvedSymbol
{
    public string Query { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public SymbolLocation? Location { get; set; }
    public List<ResolvedDependency> Dependencies { get; set; } = new();
}

public sealed class ResolvedDependency
{
    public string Symbol { get; set; } = string.Empty;
    public SymbolLocation? Location { get; set; }
    public bool IsDynamicEdge { get; set; }
    public string? UncertaintyReason { get; set; }
}
