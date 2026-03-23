using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeBrain.Analysis.CSharp;

public sealed class RoslynRepoMapService : IRepoMapService
{
    private static readonly SymbolDisplayFormat SymbolKeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private RepositoryMap? _cachedMap;
    private string? _cachedPath;

    public async Task<RepositoryMap> BuildMapAsync(string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(solutionOrProjectPath);
            if (_cachedMap is not null &&
                string.Equals(_cachedPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedMap;
            }

            using var workspace = MSBuildWorkspace.Create();
            var map = new RepositoryMap();
            var graph = map.CallGraph;
            var entryTypes = new HashSet<string>(StringComparer.Ordinal);
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            var typeRegistry = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);

            if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken);
                foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
                {
                    map.Projects.Add(project.FilePath ?? project.Name);
                    await AnalyzeProjectAsync(project, graph, entryTypes, namespaces, typeRegistry, cancellationToken);
                }
            }
            else
            {
                var project = await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken);
                map.Projects.Add(project.FilePath ?? project.Name);
                await AnalyzeProjectAsync(project, graph, entryTypes, namespaces, typeRegistry, cancellationToken);
            }

            map.EntryTypes = entryTypes.Order(StringComparer.Ordinal).ToList();
            map.Namespaces = namespaces.Order(StringComparer.Ordinal).ToList();
            _cachedMap = map;
            _cachedPath = fullPath;
            return map;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    public async Task<RepositoryKnowledgeGraph> BuildKnowledgeGraphAsync(string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        var map = await BuildMapAsync(solutionOrProjectPath, cancellationToken);
        var graph = new RepositoryKnowledgeGraph
        {
            RepositoryRoot = Path.GetDirectoryName(Path.GetFullPath(solutionOrProjectPath)) ?? string.Empty,
            SourcePath = Path.GetFullPath(solutionOrProjectPath),
            GeneratedAt = DateTimeOffset.UtcNow
        };

        foreach (var projectPath in map.Projects.Distinct(StringComparer.Ordinal))
        {
            graph.Nodes.Add(new KnowledgeNode
            {
                Id = $"project::{projectPath}",
                Kind = "project",
                Label = Path.GetFileNameWithoutExtension(projectPath),
                Project = projectPath,
                FilePath = projectPath
            });
        }

        foreach (var ns in map.Namespaces.Distinct(StringComparer.Ordinal))
        {
            graph.Nodes.Add(new KnowledgeNode
            {
                Id = $"namespace::{ns}",
                Kind = "namespace",
                Label = ns,
                Namespace = ns
            });
        }

        foreach (var symbol in map.CallGraph.Callees.Keys.Union(map.CallGraph.Callers.Keys, StringComparer.Ordinal))
        {
            var location = map.CallGraph.Locations.GetValueOrDefault(symbol);
            graph.Nodes.Add(new KnowledgeNode
            {
                Id = $"symbol::{symbol}",
                Kind = "symbol",
                Label = GetSimpleMemberName(symbol),
                Symbol = symbol,
                Namespace = TryExtractNamespace(symbol),
                Project = TryFindProject(map.Projects, location?.FilePath),
                FilePath = location?.FilePath,
                StartLine = location?.StartLine,
                EndLine = location?.EndLine
            });
        }

        foreach (var ns in map.Namespaces.Distinct(StringComparer.Ordinal))
        {
            var project = map.Projects.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), ns.Split('.')[0], StringComparison.OrdinalIgnoreCase));
            graph.Edges.Add(new KnowledgeEdge
            {
                From = project is null ? $"namespace::{ns}" : $"project::{project}",
                To = $"namespace::{ns}",
                Kind = "contains"
            });
        }

        foreach (var symbolNode in graph.Nodes.Where(n => n.Kind == "symbol"))
        {
            if (!string.IsNullOrWhiteSpace(symbolNode.Namespace))
            {
                graph.Edges.Add(new KnowledgeEdge
                {
                    From = $"namespace::{symbolNode.Namespace}",
                    To = symbolNode.Id,
                    Kind = "contains"
                });
            }
        }

        foreach (var kvp in map.CallGraph.Callees)
        {
            foreach (var callee in kvp.Value)
            {
                var dynamicEdge = map.CallGraph.DynamicEdges.FirstOrDefault(
                    item => string.Equals(item.Caller, kvp.Key, StringComparison.Ordinal) &&
                            string.Equals(item.DeclaredTarget, callee, StringComparison.Ordinal));
                graph.Edges.Add(new KnowledgeEdge
                {
                    From = $"symbol::{kvp.Key}",
                    To = $"symbol::{callee}",
                    Kind = "calls",
                    IsUncertain = dynamicEdge is not null,
                    Reason = dynamicEdge?.Reason
                });
            }
        }

        graph.Summary = new GraphSummary
        {
            NodeCount = graph.Nodes.Count,
            EdgeCount = graph.Edges.Count,
            ProjectCount = graph.Nodes.Count(n => n.Kind == "project"),
            SymbolCount = graph.Nodes.Count(n => n.Kind == "symbol")
        };

        return graph;
    }

    public async Task<ResolvedSymbol?> ResolveSymbolAsync(string query, CancellationToken cancellationToken)
    {
        if (_cachedMap is null)
        {
            throw new InvalidOperationException("BuildMapAsync must be called before symbol queries.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var resolvedKey = ResolveSymbolKey(query);
        if (resolvedKey is null)
        {
            return null;
        }

        _cachedMap.CallGraph.Callees.TryGetValue(resolvedKey, out var deps);
        var resolved = new ResolvedSymbol
        {
            Query = query,
            Symbol = resolvedKey,
            Location = _cachedMap.CallGraph.Locations.GetValueOrDefault(resolvedKey)
        };

        foreach (var dep in deps?.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>())
        {
            var dynamicEdge = _cachedMap.CallGraph.DynamicEdges.FirstOrDefault(
                edge => string.Equals(edge.Caller, resolvedKey, StringComparison.Ordinal) &&
                        string.Equals(edge.DeclaredTarget, dep, StringComparison.Ordinal));
            resolved.Dependencies.Add(new ResolvedDependency
            {
                Symbol = dep,
                Location = _cachedMap.CallGraph.Locations.GetValueOrDefault(dep),
                IsDynamicEdge = dynamicEdge is not null,
                UncertaintyReason = dynamicEdge?.Reason
            });
        }

        return await Task.FromResult(resolved);
    }

    public async Task<IReadOnlyCollection<string>> GetDependenciesAsync(string symbol, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSymbolAsync(symbol, cancellationToken);
        return resolved is null
            ? Array.Empty<string>()
            : resolved.Dependencies.Select(dep => dep.Symbol).ToArray();
    }

    public async Task<DrilldownNode> BuildDrilldownAsync(string symbol, DrilldownPolicy policy, CancellationToken cancellationToken)
    {
        if (_cachedMap is null)
        {
            throw new InvalidOperationException("BuildMapAsync must be called before drilldown queries.");
        }

        var resolved = await ResolveSymbolAsync(symbol, cancellationToken)
                       ?? throw new InvalidOperationException($"Unable to resolve symbol '{symbol}'.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return await BuildNodeAsync(resolved.Symbol, 0, policy, visited, cancellationToken);
    }

    private async Task<DrilldownNode> BuildNodeAsync(
        string symbol,
        int depth,
        DrilldownPolicy policy,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        visited.Add(symbol);
        var node = new DrilldownNode { Symbol = symbol, Depth = depth };
        if (depth >= policy.MaxDepth)
        {
            return node;
        }

        var resolved = await ResolveSymbolAsync(symbol, cancellationToken)
                       ?? new ResolvedSymbol { Symbol = symbol };
        var scored = resolved.Dependencies
            .Where(dep => policy.IgnoreNamespaces.All(prefix => !dep.Symbol.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(dep => !string.IsNullOrWhiteSpace(dep.Symbol))
            .Select(dep => ScoreDependency(symbol, dep, policy))
            .OrderByDescending(score => score.Total)
            .ThenBy(score => score.Symbol, StringComparer.Ordinal)
            .Take(policy.TopK)
            .ToList();

        node.SelectedDependencies = scored;
        foreach (var dep in scored.Select(item => item.Symbol))
        {
            if (visited.Contains(dep))
            {
                continue;
            }

            var child = await BuildNodeAsync(dep, depth + 1, policy, visited, cancellationToken);
            node.Children.Add(child);
        }

        return node;
    }

    private DependencyScore ScoreDependency(string parent, ResolvedDependency dependency, DrilldownPolicy policy)
    {
        _cachedMap!.CallGraph.Callers.TryGetValue(dependency.Symbol, out var callers);
        _cachedMap.CallGraph.Callees.TryGetValue(dependency.Symbol, out var callees);

        var risk = Math.Clamp((callers?.Count ?? 0) > 10 ? 5 : (callers?.Count ?? 0) / 2, 0, 5);
        var uncertainty = dependency.IsDynamicEdge ? 4 : 1;
        var testNeed = Math.Clamp((callees?.Count ?? 0) >= 5 ? 5 : (callees?.Count ?? 0), 0, 5);
        var total = testNeed * policy.ScoringWeights.TestNeed +
                    uncertainty * policy.ScoringWeights.Uncertainty +
                    risk * policy.ScoringWeights.Risk;

        return new DependencyScore
        {
            Symbol = dependency.Symbol,
            TestNeed = testNeed,
            Uncertainty = uncertainty,
            Risk = risk,
            Total = total,
            IsDynamicEdge = dependency.IsDynamicEdge,
            Reason = dependency.UncertaintyReason
        };
    }

    private string? ResolveSymbolKey(string query)
    {
        if (_cachedMap is null)
        {
            return null;
        }

        if (_cachedMap.CallGraph.Callees.ContainsKey(query))
        {
            return query;
        }

        var normalizedQuery = query.Trim();
        var suffixMatches = _cachedMap.CallGraph.Callees.Keys
            .Where(key => string.Equals(GetSimpleMemberName(key), normalizedQuery, StringComparison.Ordinal) ||
                          key.EndsWith($".{normalizedQuery}", StringComparison.Ordinal) ||
                          key.EndsWith($".{normalizedQuery}(", StringComparison.Ordinal))
            .ToList();

        if (suffixMatches.Count == 1)
        {
            return suffixMatches[0];
        }

        var exactTypeMethodMatches = _cachedMap.CallGraph.Callees.Keys
            .Where(key => key.Contains(normalizedQuery, StringComparison.Ordinal))
            .OrderBy(key => key.Length)
            .ToList();

        if (exactTypeMethodMatches.Count == 1)
        {
            return exactTypeMethodMatches[0];
        }

        var normalizedMatches = _cachedMap.CallGraph.Callees.Keys
            .Where(key => string.Equals(NormalizeSymbolKey(key), NormalizeSymbolKey(normalizedQuery), StringComparison.Ordinal))
            .ToList();
        if (normalizedMatches.Count == 1)
        {
            return normalizedMatches[0];
        }

        var queryMemberName = ExtractTypeAndMember(normalizedQuery);
        if (queryMemberName is not null)
        {
            var memberMatches = _cachedMap.CallGraph.Callees.Keys
                .Where(key => string.Equals(ExtractTypeAndMember(key), queryMemberName, StringComparison.Ordinal))
                .OrderBy(key => key.Length)
                .ToList();
            if (memberMatches.Count == 1)
            {
                return memberMatches[0];
            }
        }

        return null;
    }

    private static string NormalizeSymbolKey(string value)
    {
        var builder = new string(value
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Where(char.IsLetterOrDigit)
            .ToArray());
        return builder.ToLowerInvariant();
    }

    private static string? ExtractTypeAndMember(string symbol)
    {
        var trimmed = symbol.Replace("global::", string.Empty, StringComparison.Ordinal);
        var parenIndex = trimmed.IndexOf('(');
        if (parenIndex >= 0)
        {
            trimmed = trimmed[..parenIndex];
        }

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return null;
        }

        var typeBoundary = trimmed.LastIndexOf('.', lastDot - 1);
        if (typeBoundary < 0)
        {
            return trimmed;
        }

        return trimmed[(typeBoundary + 1)..];
    }

    private static string GetSimpleMemberName(string symbol)
    {
        var trimmed = symbol.Replace("global::", string.Empty);
        var genericIndex = trimmed.IndexOf('<');
        if (genericIndex >= 0)
        {
            trimmed = trimmed[..genericIndex];
        }

        var parenIndex = trimmed.IndexOf('(');
        if (parenIndex >= 0)
        {
            trimmed = trimmed[..parenIndex];
        }

        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    private static string? TryExtractNamespace(string symbol)
    {
        var clean = symbol.Replace("global::", string.Empty, StringComparison.Ordinal);
        var memberIndex = clean.LastIndexOf('.');
        if (memberIndex <= 0)
        {
            return null;
        }

        var typePath = clean[..memberIndex];
        var lastTypeSeparator = typePath.LastIndexOf('.');
        if (lastTypeSeparator <= 0)
        {
            return null;
        }

        return typePath[..lastTypeSeparator];
    }

    private static string? TryFindProject(IEnumerable<string> projects, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return projects.FirstOrDefault(project =>
        {
            var projectDir = Path.GetDirectoryName(project);
            return projectDir is not null &&
                   filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task AnalyzeProjectAsync(
        Project project,
        SymbolCallGraph graph,
        HashSet<string> entryTypes,
        HashSet<string> namespaces,
        Dictionary<string, ITypeSymbol> typeRegistry,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            return;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);

            foreach (var namespaceNode in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                namespaces.Add(namespaceNode.Name.ToString());
            }

            foreach (var methodNode in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken) as IMethodSymbol;
                if (methodSymbol is null)
                {
                    continue;
                }

                var caller = ToSymbolKey(methodSymbol);
                if (string.IsNullOrWhiteSpace(caller))
                {
                    continue;
                }

                typeRegistry[caller] = methodSymbol.ContainingType;
                EnsureNode(graph.Callees, caller);
                EnsureNode(graph.Callers, caller);
                graph.Locations[caller] = GetLocation(methodNode.GetLocation());

                if (IsEntryMethod(methodSymbol))
                {
                    entryTypes.Add(methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }

                foreach (var invocation in methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    AnalyzeCall(graph, semanticModel, caller, invocation, typeRegistry, cancellationToken);
                }

                foreach (var creation in methodNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var ctor = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol;
                    if (ctor is not null)
                    {
                        AddEdge(graph, caller, ctor);
                    }
                }

                foreach (var access in methodNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    var symbol = semanticModel.GetSymbolInfo(access, cancellationToken).Symbol;
                    if (symbol is IPropertySymbol prop)
                    {
                        AddEdge(graph, caller, prop.GetMethod ?? prop.SetMethod);
                    }
                    else if (symbol is IEventSymbol evt)
                    {
                        AddEdge(graph, caller, evt.AddMethod ?? evt.RemoveMethod);
                    }
                }
            }
        }
    }

    private static void AnalyzeCall(
        SymbolCallGraph graph,
        SemanticModel semanticModel,
        string caller,
        InvocationExpressionSyntax invocation,
        Dictionary<string, ITypeSymbol> typeRegistry,
        CancellationToken cancellationToken)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        var direct = symbolInfo.Symbol as IMethodSymbol;
        if (direct is not null)
        {
            AddEdge(graph, caller, direct.ReducedFrom ?? direct);
            return;
        }

        var candidateMethods = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().Take(5).ToList();
        if (candidateMethods.Count > 0)
        {
            foreach (var candidate in candidateMethods)
            {
                AddEdge(graph, caller, candidate);
            }

            return;
        }

        var exprType = semanticModel.GetTypeInfo(invocation.Expression, cancellationToken).Type;
        if (exprType is null)
        {
            return;
        }

        var candidates = typeRegistry
            .Where(kv => SymbolEqualityComparer.Default.Equals(kv.Value, exprType) ||
                         kv.Value.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, exprType)))
            .Select(kv => kv.Key)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var declaredTarget = exprType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        foreach (var candidate in candidates)
        {
            AddEdge(graph, caller, candidate);
        }

        graph.DynamicEdges.Add(new DynamicEdge
        {
            Caller = caller,
            DeclaredTarget = declaredTarget,
            CandidateImplementations = candidates,
            Reason = "Interface/virtual dispatch cannot be resolved statically with full precision."
        });
    }

    private static bool IsEntryMethod(IMethodSymbol methodSymbol)
    {
        return string.Equals(methodSymbol.Name, "Main", StringComparison.Ordinal) ||
               methodSymbol.GetAttributes().Any(a => a.AttributeClass?.Name is "HttpGetAttribute" or "HttpPostAttribute" or "HttpPutAttribute" or "HttpDeleteAttribute");
    }

    private static void AddEdge(SymbolCallGraph graph, string caller, ISymbol? calleeSymbol)
    {
        var callee = ToSymbolKey(calleeSymbol);
        AddEdge(graph, caller, callee);
        if (string.IsNullOrWhiteSpace(callee))
        {
            return;
        }

        if (calleeSymbol?.DeclaringSyntaxReferences.FirstOrDefault() is { } syntaxRef)
        {
            var location = syntaxRef.GetSyntax().GetLocation();
            graph.Locations[callee] = GetLocation(location);
        }
    }

    private static void AddEdge(SymbolCallGraph graph, string caller, string callee)
    {
        if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(callee))
        {
            return;
        }

        EnsureNode(graph.Callees, caller);
        EnsureNode(graph.Callers, callee);
        graph.Callees[caller].Add(callee);
        graph.Callers[callee].Add(caller);
    }

    private static void EnsureNode(Dictionary<string, HashSet<string>> map, string symbol)
    {
        if (!map.ContainsKey(symbol))
        {
            map[symbol] = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static SymbolLocation GetLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SymbolLocation
        {
            FilePath = span.Path,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1
        };
    }

    private static string ToSymbolKey(ISymbol? symbol)
    {
        return symbol?.OriginalDefinition.ToDisplayString(SymbolKeyFormat) ?? string.Empty;
    }
}
