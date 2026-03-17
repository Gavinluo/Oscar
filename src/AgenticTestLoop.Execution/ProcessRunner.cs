using System.Diagnostics;
using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;

namespace AgenticTestLoop.Execution;

public sealed class DotnetTestExecutionService : ITestExecutionService
{
    public async Task<TestRunResult> RunTestsAsync(string testProjectOrSolution, string resultsDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(resultsDirectory);
        var outputPath = Path.Combine(resultsDirectory, $"dotnet-test-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");
        var args =
            $"test \"{testProjectOrSolution}\" --logger \"trx;LogFileName=test-results.trx\" --results-directory \"{resultsDirectory}\"";
        var (exitCode, text) = await RunProcessAsync("dotnet", args, cancellationToken);
        await File.WriteAllTextAsync(outputPath, text, cancellationToken);

        var result = ParseSummary(text);
        result.Success = exitCode == 0 && result.Failed == 0;
        result.RawOutputPath = outputPath;
        return result;
    }

    private static TestRunResult ParseSummary(string output)
    {
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var result = new TestRunResult();
        foreach (var line in lines)
        {
            if (line.Contains("Passed!", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed!", StringComparison.OrdinalIgnoreCase))
            {
                // Example: Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 123 ms
                result.Failed = ExtractInt(line, "Failed:");
                result.Passed = ExtractInt(line, "Passed:");
                result.Total = ExtractInt(line, "Total:");
            }

            if (line.Contains("Error Message:", StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add(new TestFailure
                {
                    TestName = "UnknownTest",
                    Message = line.Trim()
                });
            }
        }

        return result;
    }

    private static int ExtractInt(string line, string key)
    {
        var index = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        var fragment = line[(index + key.Length)..].TrimStart();
        var digits = new string(fragment.TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");
    }
}
