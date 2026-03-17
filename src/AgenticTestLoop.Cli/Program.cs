using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticTestLoop.Agents;
using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;
using AgenticTestLoop.Execution;
using AgenticTestLoop.Roslyn;
using AgenticTestLoop.Storage;

var root = new RootCommand("Agentic Test Loop CLI");
root.Name = "atl";

var initCmd = new Command("init", "Initialize agentic test loop for current repository.");
var initSlnOption = new Option<string?>("--sln", "Path to solution.");
var initProjectOption = new Option<string?>("--project", "Target production project path for test reference.");
initCmd.AddOption(initSlnOption);
initCmd.AddOption(initProjectOption);
initCmd.SetHandler(async (sln, project) =>
{
    var repoRoot = Directory.GetCurrentDirectory();
    await RunInitAsync(repoRoot, sln, project);
}, initSlnOption, initProjectOption);

var runCmd = new Command("run", "Run full closed-loop pipeline.");
var slnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
var targetOption = new Option<string[]>("--target", "Target symbol/namespace/project.") { Arity = ArgumentArity.ZeroOrMore };
var topKOption = new Option<int>("--topk", () => 5);
var depthOption = new Option<int>("--depth", () => 3);
var coverageLineOption = new Option<double>("--coverage-line", () => 0.6);
var coverageBranchOption = new Option<double>("--coverage-branch", () => 0.4);
var iterationsOption = new Option<int>("--iterations", () => 10);
var llmOption = new Option<string>("--llm", () => "qwen");
runCmd.AddOption(slnOption);
runCmd.AddOption(targetOption);
runCmd.AddOption(topKOption);
runCmd.AddOption(depthOption);
runCmd.AddOption(coverageLineOption);
runCmd.AddOption(coverageBranchOption);
runCmd.AddOption(iterationsOption);
runCmd.AddOption(llmOption);
runCmd.SetHandler(async (sln, target, topk, depth, line, branch, iterations, llm) =>
{
    var repoRoot = Directory.GetCurrentDirectory();
    await RunLoopAsync(repoRoot, sln, target, topk, depth, line, branch, iterations, llm);
}, slnOption, targetOption, topKOption, depthOption, coverageLineOption, coverageBranchOption, iterationsOption, llmOption);

var mapCmd = new Command("map", "Output repository map summary.");
var mapSlnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
mapCmd.AddOption(mapSlnOption);
mapCmd.SetHandler(async sln =>
{
    var service = new RoslynRepoMapService();
    var map = await service.BuildMapAsync(Path.GetFullPath(sln), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
}, mapSlnOption);

var drillCmd = new Command("drill", "Drill dependencies for a symbol.");
var drillSlnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
var drillSymbolOption = new Option<string>("--symbol", "Full symbol name.") { IsRequired = true };
var drillTopKOption = new Option<int>("--topk", () => 5);
var drillDepthOption = new Option<int>("--depth", () => 3);
drillCmd.AddOption(drillSlnOption);
drillCmd.AddOption(drillSymbolOption);
drillCmd.AddOption(drillTopKOption);
drillCmd.AddOption(drillDepthOption);
drillCmd.SetHandler(async (sln, symbol, topK, depth) =>
{
    var service = new RoslynRepoMapService();
    await service.BuildMapAsync(Path.GetFullPath(sln), CancellationToken.None);
    var node = await service.BuildDrilldownAsync(symbol, new DrilldownPolicy { TopK = topK, MaxDepth = depth }, CancellationToken.None);
    var deps = await service.GetDependenciesAsync(symbol, CancellationToken.None);
    var payload = new { symbol, deps, drilldown = node };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
}, drillSlnOption, drillSymbolOption, drillTopKOption, drillDepthOption);

var serveCmd = new Command("serve", "Start local query API and visualization UI.");
var portOption = new Option<int>("--port", () => 5088);
serveCmd.AddOption(portOption);
serveCmd.SetHandler(async port =>
{
    var repoRoot = Directory.GetCurrentDirectory();
    await RunProcessAsync(
        "dotnet",
        $"run --project \"{Path.Combine(repoRoot, "agentic_test_loop", "src", "AgenticTestLoop.Api", "AgenticTestLoop.Api.csproj")}\" --urls http://localhost:{port}",
        repoRoot);
}, portOption);

root.AddCommand(initCmd);
root.AddCommand(runCmd);
root.AddCommand(mapCmd);
root.AddCommand(drillCmd);
root.AddCommand(serveCmd);

return await root.InvokeAsync(args);

static async Task RunInitAsync(string repoRoot, string? slnInput, string? projectInput)
{
    var sln = slnInput is null ? FindFirst(repoRoot, "*.sln") : Path.GetFullPath(slnInput);
    if (sln is null)
    {
        throw new InvalidOperationException("No solution file found. Pass --sln.");
    }

    var existingTestProjects = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(p => p.Contains("test", StringComparison.OrdinalIgnoreCase))
        .ToList();
    var nunitTestProject = existingTestProjects.FirstOrDefault(IsNUnitProject);

    if (nunitTestProject is null)
    {
        var testProjectDir = Path.Combine(repoRoot, "agentic_test_loop", "tests", "RepoGeneratedTests");
        Directory.CreateDirectory(testProjectDir);
        await RunProcessAsync("dotnet", $"new nunit -n RepoGeneratedTests -o \"{testProjectDir}\"", repoRoot);
        nunitTestProject = Path.Combine(testProjectDir, "RepoGeneratedTests.csproj");
        if (projectInput is not null)
        {
            await RunProcessAsync("dotnet", $"add \"{nunitTestProject}\" reference \"{Path.GetFullPath(projectInput)}\"", repoRoot);
        }
        else
        {
            var firstProdProject = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("test", StringComparison.OrdinalIgnoreCase));
            if (firstProdProject is not null)
            {
                await RunProcessAsync("dotnet", $"add \"{nunitTestProject}\" reference \"{firstProdProject}\"", repoRoot);
            }
        }
    }

    await EnsurePackageAsync(nunitTestProject, "NUnit", "4.2.2", repoRoot);
    await EnsurePackageAsync(nunitTestProject, "NUnit3TestAdapter", "4.6.0", repoRoot);
    await EnsurePackageAsync(nunitTestProject, "Microsoft.NET.Test.Sdk", "17.11.1", repoRoot);
    await EnsurePackageAsync(nunitTestProject, "coverlet.collector", "6.0.2", repoRoot);
    await EnsureToolAsync(repoRoot, "dotnet-reportgenerator-globaltool", "5.4.3");

    var config = new
    {
        solution = sln,
        testProject = nunitTestProject,
        defaultTargets = Array.Empty<string>(),
        drilldown = new { topK = 5, maxDepth = 3 },
        coverage = new { line = 0.6, branch = 0.4 },
        llm = "qwen",
        model = "qwen3-max-2026-01-23",
        baseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"
    };

    var configPath = Path.Combine(repoRoot, "agentic_test_loop.config.json");
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "understanding", "draft"));
    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "understanding", "stable"));
    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "reports", "coverage"));
    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "reports", "test_runs"));
    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "logs"));
    Directory.CreateDirectory(Path.Combine(repoRoot, "agent_artifacts", "plans"));

    Console.WriteLine($"Initialized. Solution={sln}");
    Console.WriteLine($"TestProject={nunitTestProject}");
    Console.WriteLine($"Config={configPath}");
}

static async Task RunLoopAsync(
    string repoRoot,
    string sln,
    string[] targets,
    int topK,
    int depth,
    double lineThreshold,
    double branchThreshold,
    int iterations,
    string llm)
{
    var slnPath = Path.GetFullPath(sln);
    var artifactStore = new FileArtifactStore(repoRoot);
    var mapService = new RoslynRepoMapService();
    var testExecution = new DotnetTestExecutionService();
    var coverageService = new CoverletCoverageService();
    ILLMProvider provider = CreateLlmProvider(llm);

    var testProject = ResolveTestProject(repoRoot);
    if (testProject is null)
    {
        throw new InvalidOperationException("No NUnit test project found. Run `atl init` first.");
    }

    var state = new PipelineState
    {
        CoverageThresholds = new CoverageThresholds { Line = lineThreshold, Branch = branchThreshold },
        IterationBudget = iterations
    };

    var normalizedTargets = targets.Length == 0
        ? await GuessDefaultTargetAsync(slnPath, mapService)
        : targets;

    foreach (var target in normalizedTargets)
    {
        state.TargetSymbolsQueue.Enqueue(target);
    }

    var context = new PipelineContext
    {
        RepositoryRoot = repoRoot,
        SolutionOrProjectPath = slnPath,
        ProviderName = provider.Name,
        DrilldownPolicy = new DrilldownPolicy { TopK = topK, MaxDepth = depth },
        State = state
    };
    context.Items[PipelineKeys.TestProjectPath] = testProject;

    var orchestrator = new LoopOrchestrator(
        new RepoMapperAgent(mapService, artifactStore),
        new UnderstanderAgent(mapService, artifactStore, artifactStore, provider),
        new DrilldownNavigatorAgent(mapService, artifactStore, artifactStore),
        new TestPlannerAgent(artifactStore),
        new TestWriterAgent(artifactStore),
        new RunnerAgent(testExecution, artifactStore),
        new CoverageAgent(coverageService, artifactStore),
        new MemoryAgent(artifactStore, artifactStore),
        artifactStore);

    var result = await orchestrator.RunAsync(context, CancellationToken.None);
    Console.WriteLine(result.Summary);
}

static string? ResolveTestProject(string repoRoot)
{
    var candidates = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(p => p.Contains("test", StringComparison.OrdinalIgnoreCase))
        .ToList();
    return candidates.FirstOrDefault(IsNUnitProject);
}

static async Task<string[]> GuessDefaultTargetAsync(string slnPath, RoslynRepoMapService mapService)
{
    var map = await mapService.BuildMapAsync(slnPath, CancellationToken.None);
    var first = map.CallGraph.Callees.Keys.FirstOrDefault() ?? "global::Unknown.Target";
    return new[] { first };
}

static ILLMProvider CreateLlmProvider(string llm)
{
    if (llm.Equals("qwen", StringComparison.OrdinalIgnoreCase))
    {
        return new QwenProvider(new HttpClient());
    }

    if (llm.Equals("openai", StringComparison.OrdinalIgnoreCase))
    {
        return new OpenAIProvider(new HttpClient());
    }

    return new DummyLLMProvider();
}

static bool IsNUnitProject(string csprojPath)
{
    var content = File.ReadAllText(csprojPath);
    return content.Contains("NUnit", StringComparison.OrdinalIgnoreCase);
}

static string? FindFirst(string root, string pattern)
{
    return Directory.GetFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();
}

static async Task EnsurePackageAsync(string projectPath, string packageId, string version, string workDir)
{
    var content = await File.ReadAllTextAsync(projectPath);
    if (content.Contains($"Include=\"{packageId}\"", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    await RunProcessAsync("dotnet", $"add \"{projectPath}\" package {packageId} --version {version}", workDir);
}

static async Task EnsureToolAsync(string repoRoot, string toolId, string version)
{
    var manifest = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
    if (!File.Exists(manifest))
    {
        await RunProcessAsync("dotnet", "new tool-manifest", repoRoot);
    }

    var content = await File.ReadAllTextAsync(manifest);
    if (!content.Contains(toolId, StringComparison.OrdinalIgnoreCase))
    {
        await RunProcessAsync("dotnet", $"tool install {toolId} --version {version}", repoRoot);
    }
}

static async Task RunProcessAsync(string file, string args, string workDir)
{
    var psi = new ProcessStartInfo(file, args)
    {
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var process = new Process { StartInfo = psi };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Command failed: {file} {args}\n{output}\n{error}");
    }

    if (!string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(output.Trim());
    }
}
