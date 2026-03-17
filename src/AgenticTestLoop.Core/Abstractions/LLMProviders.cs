using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgenticTestLoop.Core.Abstractions;

public sealed class DummyLLMProvider : ILLMProvider
{
    public string Name => "dummy";

    public Task<string> ChatCompletionAsync(string prompt, string? context, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            $"[DummyLLM] PromptLength={prompt.Length}; ContextLength={context?.Length ?? 0}. This is placeholder output.");
    }

    public Task<string> StructuredOutputAsync(string prompt, string schema, string? context, CancellationToken cancellationToken)
    {
        var json = $$"""
                     {
                       "provider": "dummy",
                       "note": "No external LLM configured. Generated deterministic placeholder.",
                       "promptLength": {{prompt.Length}},
                       "contextLength": {{context?.Length ?? 0}},
                       "schemaDigest": "{{schema.GetHashCode()}}"
                     }
                     """;
        return Task.FromResult(json);
    }
}

public sealed class OpenAIProvider : ILLMProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;

    public OpenAIProvider(HttpClient httpClient, string? model = null, string? baseUrl = null, string? apiKey = null)
    {
        _httpClient = httpClient;
        _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        _baseUrl = baseUrl ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    }

    public string Name => "openai";

    public Task<string> StructuredOutputAsync(string prompt, string schema, string? context, CancellationToken cancellationToken)
    {
        var mergedPrompt = $"Return JSON matching schema: {schema}\n\n{prompt}";
        return ChatCompletionAsync(mergedPrompt, context, cancellationToken);
    }

    public async Task<string> ChatCompletionAsync(string prompt, string? context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY not configured.");
        }

        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = "You are a senior C# and testing assistant." },
                new { role = "user", content = $"{prompt}\n\nContext:\n{context}" }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? string.Empty;
    }
}

public sealed class QwenProvider : ILLMProvider
{
    private readonly OpenAIProvider _innerProvider;

    public QwenProvider(HttpClient httpClient, string? model = null, string? baseUrl = null, string? apiKey = null)
    {
        var resolvedModel = model
                            ?? Environment.GetEnvironmentVariable("QWEN_MODEL")
                            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                            ?? "qwen3-max-2026-01-23";
        var resolvedBaseUrl = baseUrl
                              ?? Environment.GetEnvironmentVariable("QWEN_BASE_URL")
                              ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                              ?? "https://dashscope.aliyuncs.com/compatible-mode/v1";
        var resolvedApiKey = apiKey
                             ?? Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY")
                             ?? Environment.GetEnvironmentVariable("QWEN_API_KEY")
                             ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        _innerProvider = new OpenAIProvider(httpClient, resolvedModel, resolvedBaseUrl, resolvedApiKey);
    }

    public string Name => "qwen";

    public Task<string> ChatCompletionAsync(string prompt, string? context, CancellationToken cancellationToken)
    {
        return _innerProvider.ChatCompletionAsync(prompt, context, cancellationToken);
    }

    public Task<string> StructuredOutputAsync(string prompt, string schema, string? context, CancellationToken cancellationToken)
    {
        return _innerProvider.StructuredOutputAsync(prompt, schema, context, cancellationToken);
    }
}
