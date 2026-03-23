namespace CodeBrain.Core.Models;

/// <summary>
/// Applies lightweight heuristics to choose a primary language and default
/// analyzer from the local repository contents without requiring a full scan.
/// </summary>
public static class RepositoryLanguageDetector
{
    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".py"] = "python",
        [".java"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".md"] = "markdown",
        [".json"] = "json"
    };

    public static (string Language, string AnalyzerId) Detect(string rootPath)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            if (RepositoryFileScanner.ShouldSkipPath(file))
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (!ExtensionToLanguage.TryGetValue(extension, out var language))
            {
                continue;
            }

            counts[language] = counts.TryGetValue(language, out var current) ? current + 1 : 1;
        }

        if (Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).Any())
        {
            return ("csharp", "csharp-roslyn");
        }

        if (counts.Count == 0)
        {
            return ("text", "text-structure");
        }

        var languageWinner = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .Key;

        return languageWinner switch
        {
            "csharp" => ("csharp", "csharp-roslyn"),
            _ => (languageWinner, "text-structure")
        };
    }
}
