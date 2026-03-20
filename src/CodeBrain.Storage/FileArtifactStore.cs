using System.Text;
using System.Text.Json;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeBrain.Storage;

public sealed class FileArtifactStore : IArtifactStore, IUnderstandingStorage, IKnowledgeGraphStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly char[] AdditionalUnsafeFileNameChars = ['<', '>', '(', ')', '[', ']', '{', '}', ',', ' '];

    private readonly string _repoRoot;
    private readonly string _artifactsRoot;
    private readonly string _dbPath;

    public FileArtifactStore(string repoRoot, string artifactsRoot = "agent_artifacts")
    {
        _repoRoot = repoRoot;
        _artifactsRoot = artifactsRoot;
        EnsureDirectory("understanding", "draft");
        EnsureDirectory("understanding", "stable");
        EnsureDirectory("reports", "coverage");
        EnsureDirectory("reports", "test_runs");
        EnsureDirectory("logs");
        EnsureDirectory("plans");
        EnsureDirectory("index");
        _dbPath = Path.Combine(EnsureDirectory("index"), "knowledge.db");
        InitializeDatabase();
    }

    public string EnsureDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { _repoRoot, _artifactsRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetSafeArtifactName(string value) => SanitizeFileName(value);

    public async Task WriteLogAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var fullPath = Resolve(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, cancellationToken);
    }

    public async Task WriteJsonAsync<T>(string relativePath, T model, CancellationToken cancellationToken)
    {
        var fullPath = Resolve(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken);
    }

    public Task SaveDraftAsync(UnderstandingCard card, CancellationToken cancellationToken)
    {
        card.Status = CardStatus.Draft;
        var fileName = $"{SanitizeFileName(card.Symbol)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
        return PersistCardAsync(card, Path.Combine("understanding", "draft", fileName), "draft", cancellationToken);
    }

    public async Task PromoteToStableAsync(UnderstandingCard card, VerificationInfo verification, CancellationToken cancellationToken)
    {
        card.Status = CardStatus.Stable;
        card.VerifiedBy = verification;
        var fileName = $"{SanitizeFileName(card.Symbol)}.json";
        await PersistCardAsync(card, Path.Combine("understanding", "stable", fileName), "stable", cancellationToken);
    }

    public async Task<UnderstandingCard?> LoadLatestAsync(string symbol, CardStatus status, CancellationToken cancellationToken)
    {
        var level = status == CardStatus.Stable ? "stable" : "draft";
        var folder = EnsureDirectory("understanding", level);
        var filePattern = $"{SanitizeFileName(symbol)}*.json";
        var candidate = Directory.GetFiles(folder, filePattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault();

        if (candidate is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(candidate.FullName);
        return await JsonSerializer.DeserializeAsync<UnderstandingCard>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveGraphAsync(RepositoryKnowledgeGraph graph, CancellationToken cancellationToken)
    {
        graph.Summary.UnderstandingCardCount = CountCards();
        await WriteJsonAsync(Path.Combine("index", "graph.latest.json"), graph, cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "DELETE FROM graph_nodes;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DELETE FROM graph_edges;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DELETE FROM graph_meta;", cancellationToken);

        foreach (var node in graph.Nodes)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO graph_nodes (id, kind, label, project, namespace, symbol, file_path, start_line, end_line, properties_json)
                VALUES ($id, $kind, $label, $project, $namespace, $symbol, $file, $start, $end, $props);
                """;
            cmd.Parameters.AddWithValue("$id", node.Id);
            cmd.Parameters.AddWithValue("$kind", node.Kind);
            cmd.Parameters.AddWithValue("$label", node.Label);
            cmd.Parameters.AddWithValue("$project", (object?)node.Project ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$namespace", (object?)node.Namespace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$symbol", (object?)node.Symbol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file", (object?)node.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", (object?)node.StartLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$end", (object?)node.EndLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$props", JsonSerializer.Serialize(node.Properties, JsonOptions));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var edge in graph.Edges)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO graph_edges (source_id, target_id, kind, is_uncertain, reason)
                VALUES ($source, $target, $kind, $uncertain, $reason);
                """;
            cmd.Parameters.AddWithValue("$source", edge.From);
            cmd.Parameters.AddWithValue("$target", edge.To);
            cmd.Parameters.AddWithValue("$kind", edge.Kind);
            cmd.Parameters.AddWithValue("$uncertain", edge.IsUncertain ? 1 : 0);
            cmd.Parameters.AddWithValue("$reason", (object?)edge.Reason ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var meta = connection.CreateCommand();
        meta.CommandText =
            """
            INSERT INTO graph_meta (repository_root, source_path, generated_at, node_count, edge_count, project_count, symbol_count, understanding_card_count)
            VALUES ($root, $source, $generated, $nodes, $edges, $projects, $symbols, $cards);
            """;
        meta.Parameters.AddWithValue("$root", graph.RepositoryRoot);
        meta.Parameters.AddWithValue("$source", graph.SourcePath);
        meta.Parameters.AddWithValue("$generated", graph.GeneratedAt.ToString("O"));
        meta.Parameters.AddWithValue("$nodes", graph.Summary.NodeCount);
        meta.Parameters.AddWithValue("$edges", graph.Summary.EdgeCount);
        meta.Parameters.AddWithValue("$projects", graph.Summary.ProjectCount);
        meta.Parameters.AddWithValue("$symbols", graph.Summary.SymbolCount);
        meta.Parameters.AddWithValue("$cards", graph.Summary.UnderstandingCardCount);
        await meta.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RepositoryKnowledgeGraph?> LoadLatestGraphAsync(CancellationToken cancellationToken)
    {
        var path = Resolve(Path.Combine("index", "graph.latest.json"));
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RepositoryKnowledgeGraph>(stream, JsonOptions, cancellationToken);
    }

    public async Task<GraphSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT repository_root, source_path, generated_at, node_count, edge_count, project_count, symbol_count, understanding_card_count
            FROM graph_meta
            LIMIT 1;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new GraphSummary();
        }

        return new GraphSummary
        {
            NodeCount = reader.GetInt32(3),
            EdgeCount = reader.GetInt32(4),
            ProjectCount = reader.GetInt32(5),
            SymbolCount = reader.GetInt32(6),
            UnderstandingCardCount = reader.GetInt32(7)
        };
    }

    public async Task<IReadOnlyCollection<KnowledgeNode>> SearchNodesAsync(string? term, int limit, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, kind, label, project, namespace, symbol, file_path, start_line, end_line, properties_json
            FROM graph_nodes
            WHERE $term IS NULL OR label LIKE $pattern OR symbol LIKE $pattern OR namespace LIKE $pattern
            ORDER BY kind, label
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$term", (object?)term ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pattern", $"%{term}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return await ReadNodesAsync(cmd, cancellationToken);
    }

    public async Task<SymbolContextResult?> GetSymbolContextAsync(string symbol, CancellationToken cancellationToken)
    {
        var node = (await SearchNodesAsync(symbol, 200, cancellationToken))
            .FirstOrDefault(n => string.Equals(n.Symbol, symbol, StringComparison.Ordinal) ||
                                 string.Equals(n.Id, $"symbol::{symbol}", StringComparison.Ordinal));
        if (node is null)
        {
            return null;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var outgoing = await ReadEdgesAsync(connection, "SELECT source_id, target_id, kind, is_uncertain, reason FROM graph_edges WHERE source_id = $id;", node.Id, cancellationToken);
        var incoming = await ReadEdgesAsync(connection, "SELECT source_id, target_id, kind, is_uncertain, reason FROM graph_edges WHERE target_id = $id;", node.Id, cancellationToken);
        var relatedIds = outgoing.Select(e => e.To).Concat(incoming.Select(e => e.From)).Distinct(StringComparer.Ordinal).ToList();
        var relatedNodes = new List<KnowledgeNode>();
        foreach (var relatedId in relatedIds)
        {
            var related = await LoadNodeByIdAsync(connection, relatedId, cancellationToken);
            if (related is not null)
            {
                relatedNodes.Add(related);
            }
        }

        return new SymbolContextResult
        {
            Symbol = node.Symbol ?? symbol,
            Node = node,
            OutgoingEdges = outgoing,
            IncomingEdges = incoming,
            RelatedNodes = relatedNodes,
            DraftCard = await LoadLatestAsync(node.Symbol ?? symbol, CardStatus.Draft, cancellationToken),
            StableCard = await LoadLatestAsync(node.Symbol ?? symbol, CardStatus.Stable, cancellationToken)
        };
    }

    public async Task<ImpactAnalysisResult> GetImpactAsync(string symbol, GraphQueryOptions options, CancellationToken cancellationToken)
    {
        var context = await GetSymbolContextAsync(symbol, cancellationToken);
        if (context?.Node is null)
        {
            return new ImpactAnalysisResult { Symbol = symbol };
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var queue = new Queue<(string NodeId, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { context.Node.Id };
        var impacted = new List<KnowledgeNode>();
        var traversed = new List<KnowledgeEdge>();

        queue.Enqueue((context.Node.Id, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= options.Depth)
            {
                continue;
            }

            var edges = await ReadEdgesAsync(connection, "SELECT source_id, target_id, kind, is_uncertain, reason FROM graph_edges WHERE source_id = $id OR target_id = $id;", current, cancellationToken);
            foreach (var edge in edges)
            {
                traversed.Add(edge);
                var next = string.Equals(edge.From, current, StringComparison.Ordinal) ? edge.To : edge.From;
                if (!visited.Add(next))
                {
                    continue;
                }

                var node = await LoadNodeByIdAsync(connection, next, cancellationToken);
                if (node is not null)
                {
                    impacted.Add(node);
                    if (impacted.Count < options.Limit)
                    {
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }
        }

        return new ImpactAnalysisResult
        {
            Symbol = context.Symbol,
            ImpactedNodes = impacted.Take(options.Limit).ToList(),
            TraversedEdges = traversed
        };
    }

    private string Resolve(string relativePath)
    {
        return Path.Combine(_repoRoot, _artifactsRoot, relativePath);
    }

    private async Task PersistCardAsync(UnderstandingCard card, string relativePath, string level, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(relativePath, card, cancellationToken);
        await UpsertCardIndexAsync(card, level, cancellationToken);
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS graph_nodes (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                label TEXT NOT NULL,
                project TEXT NULL,
                namespace TEXT NULL,
                symbol TEXT NULL,
                file_path TEXT NULL,
                start_line INTEGER NULL,
                end_line INTEGER NULL,
                properties_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS graph_edges (
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                is_uncertain INTEGER NOT NULL,
                reason TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS graph_meta (
                repository_root TEXT NOT NULL,
                source_path TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                node_count INTEGER NOT NULL,
                edge_count INTEGER NOT NULL,
                project_count INTEGER NOT NULL,
                symbol_count INTEGER NOT NULL,
                understanding_card_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS understanding_cards (
                symbol TEXT NOT NULL,
                level TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (symbol, level)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_dbPath}");

    private async Task UpsertCardIndexAsync(UnderstandingCard card, string level, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO understanding_cards (symbol, level, updated_at, payload_json)
            VALUES ($symbol, $level, $updated, $payload)
            ON CONFLICT(symbol, level)
            DO UPDATE SET updated_at = excluded.updated_at, payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$symbol", card.Symbol);
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(card, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<KnowledgeNode>> ReadNodesAsync(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeNode>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadNode(reader));
        }

        return result;
    }

    private static KnowledgeNode ReadNode(SqliteDataReader reader)
    {
        var propertiesJson = reader.IsDBNull(9) ? "{}" : reader.GetString(9);
        return new KnowledgeNode
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            Label = reader.GetString(2),
            Project = reader.IsDBNull(3) ? null : reader.GetString(3),
            Namespace = reader.IsDBNull(4) ? null : reader.GetString(4),
            Symbol = reader.IsDBNull(5) ? null : reader.GetString(5),
            FilePath = reader.IsDBNull(6) ? null : reader.GetString(6),
            StartLine = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            EndLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Properties = JsonSerializer.Deserialize<Dictionary<string, string>>(propertiesJson, JsonOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static async Task<List<KnowledgeEdge>> ReadEdgesAsync(SqliteConnection connection, string sql, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var result = new List<KnowledgeEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new KnowledgeEdge
            {
                From = reader.GetString(0),
                To = reader.GetString(1),
                Kind = reader.GetString(2),
                IsUncertain = reader.GetInt32(3) == 1,
                Reason = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return result;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<KnowledgeNode?> LoadNodeByIdAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, kind, label, project, namespace, symbol, file_path, start_line, end_line, properties_json
            FROM graph_nodes
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNode(reader) : null;
    }

    private int CountCards()
    {
        var draftCount = Directory.GetFiles(EnsureDirectory("understanding", "draft"), "*.json", SearchOption.TopDirectoryOnly).Length;
        var stableCount = Directory.GetFiles(EnsureDirectory("understanding", "stable"), "*.json", SearchOption.TopDirectoryOnly).Length;
        return draftCount + stableCount;
    }

    private static string SanitizeFileName(string value)
    {
        value = value.Replace("global::", string.Empty, StringComparison.Ordinal);

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        foreach (var c in AdditionalUnsafeFileNameChars)
        {
            value = value.Replace(c, '_');
        }

        value = value.Replace(':', '_').Replace('.', '_');

        while (value.Contains("__", StringComparison.Ordinal))
        {
            value = value.Replace("__", "_", StringComparison.Ordinal);
        }

        return value.Trim('_');
    }
}
