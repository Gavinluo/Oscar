namespace AgenticTestLoop.Core.Models;

public sealed class TestPlanCase
{
    public string Name { get; set; } = string.Empty;
    public string Arrange { get; set; } = string.Empty;
    public string Act { get; set; } = string.Empty;
    public string Assert { get; set; } = string.Empty;
    public List<string> Mocks { get; set; } = new();
    public string BranchCovered { get; set; } = string.Empty;
}

public sealed class TestPlan
{
    public string TargetSymbol { get; set; } = string.Empty;
    public List<TestPlanCase> Cases { get; set; } = new();
}

public sealed class TestRunResult
{
    public bool Success { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<TestFailure> Failures { get; set; } = new();
    public string RawOutputPath { get; set; } = string.Empty;
}

public sealed class TestFailure
{
    public string TestName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}
