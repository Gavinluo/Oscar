namespace CodeBrain.Core.Abstractions;

public interface ILLMProvider
{
    string Name { get; }
    Task<string> ChatCompletionAsync(string prompt, string? context, CancellationToken cancellationToken);
    Task<string> StructuredOutputAsync(string prompt, string schema, string? context, CancellationToken cancellationToken);
}
