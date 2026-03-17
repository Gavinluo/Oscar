namespace AgenticTestLoop.Core.Models;

public sealed class RunConfiguration
{
    public string SolutionPath { get; set; } = string.Empty;
    public List<string> Targets { get; set; } = new();
    public DrilldownPolicy DrilldownPolicy { get; set; } = new();
    public CoverageThresholds CoverageThresholds { get; set; } = new();
    public int Iterations { get; set; } = 10;
    public string LlmProvider { get; set; } = "qwen";
    public string? Model { get; set; } = "qwen3-max-2026-01-23";
    public string? BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
}
