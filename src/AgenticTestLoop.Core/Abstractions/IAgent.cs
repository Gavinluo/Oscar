using AgenticTestLoop.Core.Models;

namespace AgenticTestLoop.Core.Abstractions;

public interface IAgent
{
    string Name { get; }
    Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken);
}

public interface IOrchestrator
{
    Task<OrchestrationResult> RunAsync(PipelineContext context, CancellationToken cancellationToken);
}

public sealed class OrchestrationResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Blockers { get; set; } = new();
}
