using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using CodeBrain.Analysis.CSharp;
using CodeBrain.Analysis.Text;
using CodeBrain.Cli;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using CodeBrain.Execution;
using CodeBrain.Storage;
using CodeBrain.Workflows;

var root = new RootCommand("CodeBrain CLI");
root.Name = "codebrain";

root.AddCommand(BuildReposCommand());
root.AddCommand(BuildIndexCommand());
root.AddCommand(BuildQueryCommand());
root.AddCommand(BuildInitCommand());
root.AddCommand(BuildRunCommand());
root.AddCommand(BuildMapCommand());
root.AddCommand(BuildDrillCommand());
root.AddCommand(BuildServeCommand());

return await root.InvokeAsync(args);

static Command BuildReposCommand()
{
    var repos = new Command("repos", "Register and inspect local repositories managed by CodeBrain.");

    var add = new Command("add", "Register a local repository path in the CodeBrain catalog.");
    var pathOption = new Option<string>("--path", "Local repository path.") { IsRequired = true };
    add.AddOption(pathOption);
    add.SetHandler(async path =>
    {
        var codeBrainRoot = WorkspacePaths.ResolveCodeBrainRoot();
        var catalog = new SqliteRepositoryCatalog(codeBrainRoot);
        var repository = await catalog.RegisterAsync(path, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(repository, JsonOptions()));
    }, pathOption);

    var list = new Command("list", "List repositories known to the local CodeBrain catalog.");
    list.SetHandler(async () =>
    {
        var codeBrainRoot = WorkspacePaths.ResolveCodeBrainRoot();
        var catalog = new SqliteRepositoryCatalog(codeBrainRoot);
        var repositories = await catalog.ListAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(repositories, JsonOptions()));
    });

    repos.AddCommand(add);
    repos.AddCommand(list);
    return repos;
}

static Command BuildIndexCommand()
{
    var index = new Command("index", "Build or refresh a persisted repository index.");
    var repoOption = new Option<string>("--repo", "Registered repository id.") { IsRequired = true };
    index.AddOption(repoOption);
    index.SetHandler(async repoId =>
    {
        var codeBrainRoot = WorkspacePaths.ResolveCodeBrainRoot();
        var catalog = new SqliteRepositoryCatalog(codeBrainRoot);
        var planner = new LocalFileIncrementalIndexPlanner();
        var store = new SqliteRepositoryIndexStore(codeBrainRoot);
        var repository = await catalog.GetAsync(repoId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Unknown repository '{repoId}'. Run `codebrain repos add --path <path>` first.");
        var analyzer = ResolveAnalyzer(repository);
        if (!analyzer.CanHandle(repository))
        {
            throw new InvalidOperationException($"Analyzer '{analyzer.Id}' cannot handle repository '{repoId}'.");
        }

        var existingManifest = await store.LoadManifestAsync(repoId, CancellationToken.None);
        var changeSet = await planner.PlanAsync(repository, existingManifest, CancellationToken.None);
        var analysis = await analyzer.AnalyzeAsync(new RepositoryAnalysisRequest
        {
            Repository = repository,
            ChangeSet = changeSet
        }, CancellationToken.None);

        repository.AnalyzerId = analyzer.Id;
        repository.LastIndexedAt = analysis.Manifest.IndexedAt;

        await store.SaveAsync(new RepositoryIndex
        {
            Repository = repository,
            Manifest = analysis.Manifest,
            Graph = analysis.Graph,
            Documents = analysis.Documents
        }, CancellationToken.None);

        await catalog.UpdateLastIndexedAsync(repository.Id, analysis.Manifest.IndexedAt, CancellationToken.None);

        var payload = new
        {
            repository = repository.Id,
            indexedAt = analysis.Manifest.IndexedAt,
            analyzer = analyzer.Id,
            detectionMode = changeSet.DetectionMode,
            headCommit = changeSet.HeadCommit,
            added = changeSet.Added.Count,
            modified = changeSet.Modified.Count,
            removed = changeSet.Removed.Count,
            unchanged = changeSet.Unchanged.Count,
            documents = analysis.Documents.Count,
            nodes = analysis.Graph.Summary.NodeCount,
            edges = analysis.Graph.Summary.EdgeCount
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions()));
    }, repoOption);

    return index;
}

static Command BuildQueryCommand()
{
    var query = new Command("query", "Run repository retrieval against one or more persisted indexes.");
    var repoOption = new Option<string[]>("--repo", "Registered repository ids.") { Arity = ArgumentArity.OneOrMore };
    var textOption = new Option<string>("--q", "Question or lookup text.") { IsRequired = true };
    var intentOption = new Option<string>("--intent", () => "codeqa", "Query intent: codeqa, symbol, impact, test, bug.");
    var limitOption = new Option<int>("--limit", () => 10, "Maximum number of hits.");
    var graphDepthOption = new Option<int>("--graph-depth", () => 2, "Graph expansion depth for hybrid retrieval.");
    query.AddOption(repoOption);
    query.AddOption(textOption);
    query.AddOption(intentOption);
    query.AddOption(limitOption);
    query.AddOption(graphDepthOption);
    query.SetHandler(async (repos, text, intent, limit, graphDepth) =>
    {
        var codeBrainRoot = WorkspacePaths.ResolveCodeBrainRoot();
        var store = new SqliteRepositoryIndexStore(codeBrainRoot);
        var result = await store.QueryAsync(new RepositoryQuery
        {
            RepositoryIds = repos.ToList(),
            Text = text,
            Intent = ParseIntent(intent),
            Limit = limit,
            GraphDepth = graphDepth
        }, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
    }, repoOption, textOption, intentOption, limitOption, graphDepthOption);

    return query;
}

static Command BuildInitCommand()
{
    var init = new Command("init", "Initialize CodeBrain closed-loop testing in the current workspace.");
    var slnOption = new Option<string?>("--sln", "Path to solution.");
    var projectOption = new Option<string?>("--project", "Production project path to reference from a generated test project.");
    init.AddOption(slnOption);
    init.AddOption(projectOption);
    init.SetHandler(async (sln, project) =>
    {
        var workspaceRoot = WorkspacePaths.ResolveTargetWorkspaceRoot(sln ?? project);
        await RunInitAsync(workspaceRoot, sln, project);
    }, slnOption, projectOption);
    return init;
}

static Command BuildRunCommand()
{
    var run = new Command("run", "Run the existing closed-loop test workflow against a C# solution or project.");
    var slnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
    var targetOption = new Option<string[]>("--target", "Target symbol/namespace/project.") { Arity = ArgumentArity.ZeroOrMore };
    var topKOption = new Option<int>("--topk", () => 5);
    var depthOption = new Option<int>("--depth", () => 3);
    var coverageLineOption = new Option<double>("--coverage-line", () => 0.6);
    var coverageBranchOption = new Option<double>("--coverage-branch", () => 0.4);
    var iterationsOption = new Option<int>("--iterations", () => 10);
    var llmOption = new Option<string>("--llm", () => "qwen");
    run.AddOption(slnOption);
    run.AddOption(targetOption);
    run.AddOption(topKOption);
    run.AddOption(depthOption);
    run.AddOption(coverageLineOption);
    run.AddOption(coverageBranchOption);
    run.AddOption(iterationsOption);
    run.AddOption(llmOption);
    run.SetHandler(async (sln, targets, topK, depth, line, branch, iterations, llm) =>
    {
        var workspaceRoot = WorkspacePaths.ResolveTargetWorkspaceRoot(sln);
        await RunLoopAsync(workspaceRoot, sln, targets, topK, depth, line, branch, iterations, llm);
    }, slnOption, targetOption, topKOption, depthOption, coverageLineOption, coverageBranchOption, iterationsOption, llmOption);
    return run;
}

static Command BuildMapCommand()
{
    var map = new Command("map", "Print the Roslyn repository map for a solution or project.");
    var slnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
    map.AddOption(slnOption);
    map.SetHandler(async sln =>
    {
        var service = new RoslynRepoMapService();
        var result = await service.BuildMapAsync(Path.GetFullPath(sln), CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
    }, slnOption);
    return map;
}

static Command BuildDrillCommand()
{
    var drill = new Command("drill", "Inspect symbol dependencies through the current C# graph.");
    var slnOption = new Option<string>("--sln", "Solution or project path.") { IsRequired = true };
    var symbolOption = new Option<string>("--symbol", "Full symbol name.") { IsRequired = true };
    var topKOption = new Option<int>("--topk", () => 5);
    var depthOption = new Option<int>("--depth", () => 3);
    drill.AddOption(slnOption);
    drill.AddOption(symbolOption);
    drill.AddOption(topKOption);
    drill.AddOption(depthOption);
    drill.SetHandler(async (sln, symbol, topK, depth) =>
    {
        var service = new RoslynRepoMapService();
        await service.BuildMapAsync(Path.GetFullPath(sln), CancellationToken.None);
        var node = await service.BuildDrilldownAsync(symbol, new DrilldownPolicy { TopK = topK, MaxDepth = depth }, CancellationToken.None);
        var deps = await service.GetDependenciesAsync(symbol, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { symbol, deps, drilldown = node }, JsonOptions()));
    }, slnOption, symbolOption, topKOption, depthOption);
    return drill;
}

static Command BuildServeCommand()
{
    var serve = new Command("serve", "Start the local CodeBrain API and browser UI.");
    var portOption = new Option<int>("--port", () => 5088);
    serve.AddOption(portOption);
    serve.SetHandler(async port =>
    {
        var workspaceRoot = WorkspacePaths.ResolveCodeBrainRoot();
        await RunProcessAsync(
            "dotnet",
            $"run --project \"{Path.Combine(workspaceRoot, "src", "CodeBrain.Api", "CodeBrain.Api.csproj")}\" --urls http://localhost:{port}",
            workspaceRoot);
    }, portOption);
    return serve;
}

static async Task RunInitAsync(string workspaceRoot, string? slnInput, string? projectInput)
{
    var solution = slnInput is null ? FindFirst(workspaceRoot, "*.sln") : Path.GetFullPath(slnInput);
    if (solution is null)
    {
        throw new InvalidOperationException("No solution file found. Pass --sln.");
    }

    var existingTestProjects = Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => path.Contains("test", StringComparison.OrdinalIgnoreCase))
        .ToList();
    var nunitTestProject = existingTestProjects.FirstOrDefault(IsNUnitProject);

    if (nunitTestProject is null)
    {
        var testProjectDir = Path.Combine(workspaceRoot, "tests", "RepoGeneratedTests");
        Directory.CreateDirectory(testProjectDir);
        await RunProcessAsync("dotnet", $"new nunit -n RepoGeneratedTests -o \"{testProjectDir}\"", workspaceRoot);
        nunitTestProject = Path.Combine(testProjectDir, "RepoGeneratedTests.csproj");

        var projectToReference = projectInput is not null
            ? Path.GetFullPath(projectInput)
            : Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.Contains("test", StringComparison.OrdinalIgnoreCase));
        if (projectToReference is not null)
        {
            await RunProcessAsync("dotnet", $"add \"{nunitTestProject}\" reference \"{projectToReference}\"", workspaceRoot);
        }
    }

    await EnsurePackageAsync(nunitTestProject, "NUnit", "4.2.2", workspaceRoot);
    await EnsurePackageAsync(nunitTestProject, "NUnit3TestAdapter", "4.6.0", workspaceRoot);
    await EnsurePackageAsync(nunitTestProject, "Microsoft.NET.Test.Sdk", "17.11.1", workspaceRoot);
    await EnsurePackageAsync(nunitTestProject, "coverlet.collector", "6.0.2", workspaceRoot);
    await EnsureToolAsync(workspaceRoot, "dotnet-reportgenerator-globaltool", "5.4.3");

    var config = new
    {
        solution,
        testProject = nunitTestProject,
        defaultTargets = Array.Empty<string>(),
        drilldown = new { topK = 5, maxDepth = 3 },
        coverage = new { line = 0.6, branch = 0.4 },
        llm = "qwen",
        model = "qwen3-max-2026-01-23",
        baseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"
    };

    var configPath = Path.Combine(workspaceRoot, "codebrain.config.json");
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, JsonOptions()));

    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "understanding", "draft"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "understanding", "stable"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "reports", "coverage"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "reports", "test_runs"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "logs"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "plans"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "agent_artifacts", "index"));

    Console.WriteLine($"Initialized CodeBrain. Solution={solution}");
    Console.WriteLine($"TestProject={nunitTestProject}");
    Console.WriteLine($"Config={configPath}");
}

static async Task RunLoopAsync(
    string workspaceRoot,
    string solution,
    string[] targets,
    int topK,
    int depth,
    double lineThreshold,
    double branchThreshold,
    int iterations,
    string llm)
{
    var solutionPath = Path.GetFullPath(solution);
    var artifactStore = new FileArtifactStore(workspaceRoot);
    var mapService = new RoslynRepoMapService();
    var testExecution = new DotnetTestExecutionService();
    var coverageService = new CoverletCoverageService();
    ILLMProvider provider = CreateLlmProvider(llm);

    var testProject = ResolveTestProject(workspaceRoot);
    if (testProject is null)
    {
        throw new InvalidOperationException("No NUnit test project found. Run `codebrain init` first.");
    }

    var state = new PipelineState
    {
        CoverageThresholds = new CoverageThresholds { Line = lineThreshold, Branch = branchThreshold },
        IterationBudget = iterations
    };

    var normalizedTargets = targets.Length == 0
        ? await GuessDefaultTargetAsync(solutionPath, mapService)
        : targets;
    foreach (var target in normalizedTargets)
    {
        state.TargetSymbolsQueue.Enqueue(target);
    }

    var context = new PipelineContext
    {
        RepositoryRoot = workspaceRoot,
        SolutionOrProjectPath = solutionPath,
        ProviderName = provider.Name,
        DrilldownPolicy = new DrilldownPolicy { TopK = topK, MaxDepth = depth },
        State = state
    };
    context.Items[PipelineKeys.TestProjectPath] = testProject;

    var orchestrator = new LoopOrchestrator(
        new RepoMapperAgent(mapService, artifactStore),
        new UnderstanderAgent(mapService, artifactStore, artifactStore, provider),
        new DrilldownNavigatorAgent(mapService, artifactStore, artifactStore),
        new ChangeScopeAgent(artifactStore),
        new EditPlannerAgent(artifactStore),
        new TestPlannerAgent(artifactStore),
        new TestWriterAgent(artifactStore),
        new RunnerAgent(testExecution, artifactStore),
        new CoverageAgent(coverageService, artifactStore),
        new MemoryAgent(artifactStore, artifactStore),
        artifactStore);

    var result = await orchestrator.RunAsync(context, CancellationToken.None);
    Console.WriteLine(result.Summary);
}

static string? ResolveTestProject(string workspaceRoot)
{
    return Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => path.Contains("test", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault(IsNUnitProject);
}

static async Task<string[]> GuessDefaultTargetAsync(string solutionPath, RoslynRepoMapService mapService)
{
    var map = await mapService.BuildMapAsync(solutionPath, CancellationToken.None);
    var first = map.CallGraph.Callees.Keys.FirstOrDefault() ?? "global::Unknown.Target";
    return [first];
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

static QueryIntent ParseIntent(string raw)
{
    return raw.Trim().ToLowerInvariant() switch
    {
        "codeqa" or "qa" => QueryIntent.CodeQa,
        "symbol" or "symbollookup" => QueryIntent.SymbolLookup,
        "impact" or "impactanalysis" => QueryIntent.ImpactAnalysis,
        "test" or "testgeneration" => QueryIntent.TestGeneration,
        "bug" or "buglocalization" => QueryIntent.BugLocalization,
        _ => QueryIntent.CodeQa
    };
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

static async Task EnsureToolAsync(string workspaceRoot, string toolId, string version)
{
    var manifest = Path.Combine(workspaceRoot, ".config", "dotnet-tools.json");
    if (!File.Exists(manifest))
    {
        await RunProcessAsync("dotnet", "new tool-manifest", workspaceRoot);
    }

    var content = await File.ReadAllTextAsync(manifest);
    if (!content.Contains(toolId, StringComparison.OrdinalIgnoreCase))
    {
        await RunProcessAsync("dotnet", $"tool install {toolId} --version {version}", workspaceRoot);
    }
}

static async Task RunProcessAsync(string file, string args, string workDir)
{
    var startInfo = new ProcessStartInfo(file, args)
    {
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = new Process { StartInfo = startInfo };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed: {file} {args}\n{output}\n{error}");
    }

    if (!string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(output.Trim());
    }
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true
};

static IRepositoryAnalyzer ResolveAnalyzer(RegisteredRepository repository)
{
    IRepositoryAnalyzer[] analyzers =
    [
        new CSharpRepositoryAnalyzer(),
        new TextStructureRepositoryAnalyzer()
    ];

    return analyzers.FirstOrDefault(analyzer =>
               string.Equals(analyzer.Id, repository.AnalyzerId, StringComparison.OrdinalIgnoreCase) &&
               analyzer.CanHandle(repository))
           ?? analyzers.First(analyzer => analyzer.CanHandle(repository));
}
