namespace CodeBrain.Core.Models;

public enum CardStatus
{
    Draft,
    Stable
}

public sealed class UnderstandingEvidence
{
    public string FilePath { get; set; } = string.Empty;
    public string LineRange { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsUncertain { get; set; }
    public string? UncertaintyReason { get; set; }
}

public sealed class VerificationInfo
{
    public List<string> Tests { get; set; } = new();
    public CoverageSummary? Coverage { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Commit { get; set; }
}

public sealed class UnderstandingCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public List<string> InputsPreconditions { get; set; } = new();
    public List<string> OutputsPostconditions { get; set; } = new();
    public List<string> SideEffects { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    public List<string> Branching { get; set; } = new();
    public List<string> TestSeams { get; set; } = new();
    public List<string> Observability { get; set; } = new();
    public List<UnderstandingEvidence> Evidence { get; set; } = new();
    public CardStatus Status { get; set; } = CardStatus.Draft;
    public VerificationInfo? VerifiedBy { get; set; }
}

public sealed class CoverageSummary
{
    public double Line { get; set; }
    public double Branch { get; set; }
    public List<CoverageHotspot> Hotspots { get; set; } = new();
}

public sealed class CoverageHotspot
{
    public string FileOrType { get; set; } = string.Empty;
    public double LineCoverage { get; set; }
    public double BranchCoverage { get; set; }
    public string Reason { get; set; } = string.Empty;
}
