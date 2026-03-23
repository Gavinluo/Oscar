namespace CodeBrain.Core.Models;

/// <summary>
/// Describes the focused repository context assembled from hybrid retrieval so
/// question answering and downstream edit planning operate on the same evidence.
/// </summary>
public sealed class RepositoryContextBundle
{
    public string Summary { get; set; } = string.Empty;
    public List<RepositoryQueryHit> Evidence { get; set; } = new();
    public List<string> Symbols { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public List<string> GraphNeighbors { get; set; } = new();
    public List<string> VerificationTargets { get; set; } = new();
}

/// <summary>
/// Represents the code area that should be inspected or modified for the
/// current task. The scope is intentionally conservative to keep edits bounded.
/// </summary>
public sealed class RepositoryChangeScope
{
    public string TargetSymbol { get; set; } = string.Empty;
    public List<string> RelatedSymbols { get; set; } = new();
    public List<string> ImpactedFiles { get; set; } = new();
    public List<string> VerificationTargets { get; set; } = new();
    public string ScopeReason { get; set; } = string.Empty;
}

/// <summary>
/// Summarizes the edit actions that the system recommends before changing code.
/// The plan can be used by the workflow layer or surfaced directly in query
/// responses for operator review.
/// </summary>
public sealed class RepositoryEditPlan
{
    public QueryIntent Intent { get; set; } = QueryIntent.CodeQa;
    public string Goal { get; set; } = string.Empty;
    public List<string> FilesToInspect { get; set; } = new();
    public List<string> SymbolsToEdit { get; set; } = new();
    public List<string> VerificationSteps { get; set; } = new();
    public string RiskNotes { get; set; } = string.Empty;
}
