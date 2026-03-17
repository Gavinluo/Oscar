using System.Xml.Linq;
using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;

namespace AgenticTestLoop.Execution;

public sealed class CoverletCoverageService : ICoverageService
{
    public async Task<CoverageSummary> CollectAsync(string testProjectOrSolution, string resultsDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(resultsDirectory);
        var args =
            $"test \"{testProjectOrSolution}\" --collect:\"XPlat Code Coverage\" --results-directory \"{resultsDirectory}\"";
        var (exitCode, output) = await DotnetTestExecutionService.RunProcessAsync("dotnet", args, cancellationToken);
        var logPath = Path.Combine(resultsDirectory, $"coverage-run-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");
        await File.WriteAllTextAsync(logPath, output, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException("Coverage collection failed. See log: " + logPath);
        }

        var cobertura = FindLatestCoverageXml(resultsDirectory);
        if (cobertura is null)
        {
            return new CoverageSummary();
        }

        var summary = ParseCobertura(cobertura);
        await GenerateHtmlAsync(cobertura, resultsDirectory, cancellationToken);
        return summary;
    }

    private static string? FindLatestCoverageXml(string root)
    {
        return Directory.GetFiles(root, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private static CoverageSummary ParseCobertura(string coberturaPath)
    {
        var doc = XDocument.Load(coberturaPath);
        var coverage = doc.Root;
        if (coverage is null)
        {
            return new CoverageSummary();
        }

        var lineRate = ParseDouble(coverage.Attribute("line-rate")?.Value);
        var branchRate = ParseDouble(coverage.Attribute("branch-rate")?.Value);
        var hotspots = new List<CoverageHotspot>();

        var classNodes = coverage.Descendants("class");
        foreach (var classNode in classNodes)
        {
            var name = classNode.Attribute("name")?.Value ?? "Unknown";
            var clsLine = ParseDouble(classNode.Attribute("line-rate")?.Value);
            var clsBranch = ParseDouble(classNode.Attribute("branch-rate")?.Value);
            if (clsLine < 0.8 || clsBranch < 0.6)
            {
                hotspots.Add(new CoverageHotspot
                {
                    FileOrType = name,
                    LineCoverage = clsLine,
                    BranchCoverage = clsBranch,
                    Reason = "Below recommended threshold."
                });
            }
        }

        return new CoverageSummary
        {
            Line = lineRate,
            Branch = branchRate,
            Hotspots = hotspots
                .OrderBy(h => h.LineCoverage)
                .ThenBy(h => h.BranchCoverage)
                .Take(10)
                .ToList()
        };
    }

    private static async Task GenerateHtmlAsync(string coberturaPath, string resultsDirectory, CancellationToken cancellationToken)
    {
        var htmlDir = Path.Combine(Directory.GetParent(resultsDirectory)?.FullName ?? resultsDirectory, "coverage");
        Directory.CreateDirectory(htmlDir);
        var args = $"-reports:\"{coberturaPath}\" -targetdir:\"{htmlDir}\" -reporttypes:Html";
        var (exitCode, output) = await DotnetTestExecutionService.RunProcessAsync("reportgenerator", args, cancellationToken);
        if (exitCode != 0)
        {
            var fallback = await DotnetTestExecutionService.RunProcessAsync(
                "dotnet",
                $"tool run reportgenerator {args}",
                cancellationToken);
            output += Environment.NewLine + fallback.Output;
        }

        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "reportgenerator.log"), output, cancellationToken);
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, out var parsed) ? parsed : 0;
    }
}
