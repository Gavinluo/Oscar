namespace CodeBrain.Core.Models;

public enum RepositoryKind
{
    LocalPath
}

public enum QueryIntent
{
    CodeQa,
    SymbolLookup,
    ImpactAnalysis,
    TestGeneration,
    BugLocalization
}

public sealed class RegisteredRepository
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public RepositoryKind Kind { get; set; } = RepositoryKind.LocalPath;
    public string AnalyzerId { get; set; } = string.Empty;
    public string PrimaryLanguage { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastIndexedAt { get; set; }
}

public sealed class RepositoryFileFingerprint
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}

public sealed class RepositoryIndexManifest
{
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public string AnalyzerId { get; set; } = string.Empty;
    public string PrimaryLanguage { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? HeadCommit { get; set; }
    public string ChangeDetectionMode { get; set; } = "filesystem";
    public List<RepositoryFileFingerprint> Files { get; set; } = new();
}

public sealed class RepositoryChangeSet
{
    public List<string> Added { get; set; } = new();
    public List<string> Modified { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> Unchanged { get; set; } = new();
    public string DetectionMode { get; set; } = "filesystem";
    public string? HeadCommit { get; set; }
    public List<string> GitModified { get; set; } = new();
    public List<string> GitUntracked { get; set; } = new();

    public bool HasChanges =>
        Added.Count > 0 ||
        Modified.Count > 0 ||
        Removed.Count > 0;
}

public sealed class RepositoryIndexDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RepositoryId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string? FilePath { get; set; }
    public string? Language { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RepositoryAnalysisRequest
{
    public RegisteredRepository Repository { get; set; } = new();
    public RepositoryChangeSet ChangeSet { get; set; } = new();
}

public sealed class RepositoryAnalysisResult
{
    public RepositoryMap Map { get; set; } = new();
    public RepositoryKnowledgeGraph Graph { get; set; } = new();
    public List<RepositoryIndexDocument> Documents { get; set; } = new();
    public RepositoryIndexManifest Manifest { get; set; } = new();
}

public sealed class RepositoryIndex
{
    public RegisteredRepository Repository { get; set; } = new();
    public RepositoryIndexManifest Manifest { get; set; } = new();
    public RepositoryKnowledgeGraph Graph { get; set; } = new();
    public List<RepositoryIndexDocument> Documents { get; set; } = new();
}

public sealed class RepositoryQuery
{
    public string Text { get; set; } = string.Empty;
    public QueryIntent Intent { get; set; } = QueryIntent.CodeQa;
    public List<string> RepositoryIds { get; set; } = new();
    public int Limit { get; set; } = 10;
    public int GraphDepth { get; set; } = 2;
}

public sealed class RepositoryQueryHit
{
    public string RepositoryId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string? FilePath { get; set; }
    public double Score { get; set; }
    public double Bm25Score { get; set; }
    public double VectorScore { get; set; }
    public double GraphScore { get; set; }
    public string RankReason { get; set; } = string.Empty;
}

public sealed class RepositoryQueryResult
{
    public string QueryText { get; set; } = string.Empty;
    public QueryIntent Intent { get; set; } = QueryIntent.CodeQa;
    public List<RepositoryQueryHit> Hits { get; set; } = new();
    public List<string> RelatedSymbols { get; set; } = new();
    public string RetrievalStrategy { get; set; } = "hybrid";
    public RepositoryContextBundle Context { get; set; } = new();
    public RepositoryEditPlan? SuggestedEditPlan { get; set; }
}
