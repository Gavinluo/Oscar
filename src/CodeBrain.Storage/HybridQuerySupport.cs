using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Storage;

/// <summary>
/// Assembles a compact evidence packet from hybrid retrieval output. The packet
/// is shared by query responses and the closed-loop workflow so both layers
/// reason over the same narrowed repository context.
/// </summary>
internal sealed class HybridContextAssembler : IContextAssembler
{
    public RepositoryContextBundle Assemble(
        RepositoryQuery query,
        IReadOnlyCollection<RepositoryQueryHit> hits,
        IReadOnlyCollection<KnowledgeNode> relatedNodes)
    {
        var symbols = hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.Symbol))
            .Select(hit => hit.Symbol!)
            .Concat(relatedNodes.Where(node => !string.IsNullOrWhiteSpace(node.Symbol)).Select(node => node.Symbol!))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        var files = hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.FilePath))
            .Select(hit => hit.FilePath!)
            .Concat(relatedNodes.Where(node => !string.IsNullOrWhiteSpace(node.FilePath)).Select(node => node.FilePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var graphNeighbors = relatedNodes
            .Select(node => node.Symbol ?? node.Label)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

        return new RepositoryContextBundle
        {
            Summary = BuildSummary(query, hits, files, graphNeighbors),
            Evidence = hits.Take(8).ToList(),
            Symbols = symbols,
            Files = files,
            GraphNeighbors = graphNeighbors,
            VerificationTargets = BuildVerificationTargets(query, hits, files)
        };
    }

    private static string BuildSummary(
        RepositoryQuery query,
        IReadOnlyCollection<RepositoryQueryHit> hits,
        IReadOnlyCollection<string> files,
        IReadOnlyCollection<string> graphNeighbors)
    {
        var topTitles = hits.Take(3).Select(hit => hit.Title).ToList();
        return $"Hybrid context for '{query.Text}' ({query.Intent}) selected {hits.Count} evidence items, " +
               $"{files.Count} files, and {graphNeighbors.Count} graph neighbors. " +
               $"Top anchors: {string.Join(", ", topTitles)}.";
    }

    private static List<string> BuildVerificationTargets(
        RepositoryQuery query,
        IReadOnlyCollection<RepositoryQueryHit> hits,
        IReadOnlyCollection<string> files)
    {
        var targets = new List<string>();
        targets.AddRange(hits.Where(hit => !string.IsNullOrWhiteSpace(hit.Symbol)).Select(hit => $"symbol:{hit.Symbol}"));
        if (query.Intent == QueryIntent.TestGeneration || query.Intent == QueryIntent.BugLocalization)
        {
            targets.AddRange(files.Select(file => $"file:{file}"));
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }
}

/// <summary>
/// Produces an initial edit plan from the assembled context. This keeps the
/// modification loop explicit and reviewable before any file edits are made.
/// </summary>
internal sealed class HybridEditPlanningService : IEditPlanningService
{
    public RepositoryEditPlan BuildPlan(RepositoryQuery query, RepositoryContextBundle context)
    {
        return new RepositoryEditPlan
        {
            Intent = query.Intent,
            Goal = $"Address query '{query.Text}' with a bounded edit scoped to the top retrieved evidence.",
            FilesToInspect = context.Files.Take(6).ToList(),
            SymbolsToEdit = context.Symbols.Take(6).ToList(),
            VerificationSteps = context.VerificationTargets.Take(6).ToList(),
            RiskNotes = BuildRiskNotes(query, context)
        };
    }

    private static string BuildRiskNotes(RepositoryQuery query, RepositoryContextBundle context)
    {
        var graphCount = context.GraphNeighbors.Count;
        return query.Intent switch
        {
            QueryIntent.ImpactAnalysis => $"Graph expansion identified {graphCount} nearby nodes. Verify downstream callers before editing.",
            QueryIntent.TestGeneration => $"Favor deterministic seams. Re-run tests for {context.VerificationTargets.Count} selected targets.",
            QueryIntent.BugLocalization => "Prefer a minimal reproduction and narrow the fix to the highest ranked file/symbol pair.",
            _ => $"Inspect {context.Files.Count} candidate files and keep edits constrained to the top ranked symbols."
        };
    }
}

/// <summary>
/// Implements deterministic local vectors so the project can exercise hybrid
/// retrieval without relying on an external embedding service.
/// </summary>
internal static class LocalVectorMath
{
    private const int Dimensions = 128;

    public static double[] Embed(string? text)
    {
        var vector = new double[Dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        foreach (var token in text
                     .Split([' ', '\r', '\n', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '<', '>'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(token => token.ToLowerInvariant()))
        {
            var index = Math.Abs(DeterministicHash(token)) % Dimensions;
            vector[index] += 1d;
        }

        Normalize(vector);
        return vector;
    }

    public static double CosineSimilarity(double[] left, double[] right)
    {
        if (left.Length != right.Length)
        {
            return 0d;
        }

        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static void Normalize(double[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (magnitude <= 0)
        {
            return;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= magnitude;
        }
    }

    private static int DeterministicHash(string token)
    {
        unchecked
        {
            var hash = 23;
            foreach (var ch in token)
            {
                hash = (hash * 31) + ch;
            }

            return hash;
        }
    }
}
