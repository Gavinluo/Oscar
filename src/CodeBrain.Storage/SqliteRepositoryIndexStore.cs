using System.Text.Json;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeBrain.Storage;

/// <summary>
/// Persists repository manifests, graph snapshots, and retrieval documents into
/// SQLite so future runs can load an existing index instead of rebuilding from scratch.
/// </summary>
public sealed class SqliteRepositoryIndexStore : IRepositoryIndexStore, IRepositoryQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _connectionString;

    public SqliteRepositoryIndexStore(string workspaceRoot, string artifactsRoot = "agent_artifacts")
    {
        var indexRoot = Path.Combine(workspaceRoot, artifactsRoot, "index");
        Directory.CreateDirectory(indexRoot);
        _connectionString = $"Data Source={Path.Combine(indexRoot, "codebrain.index.db")}";
        Initialize();
    }

    public async Task SaveAsync(RepositoryIndex index, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)dbTransaction;

        await ExecuteAsync(connection, transaction,
            "DELETE FROM repository_documents WHERE repository_id = $repository_id;",
            [CreateParameter("$repository_id", index.Repository.Id)], cancellationToken);

        await ExecuteAsync(connection, transaction,
            "DELETE FROM repository_indexes WHERE repository_id = $repository_id;",
            [CreateParameter("$repository_id", index.Repository.Id)], cancellationToken);

        var insertIndex = connection.CreateCommand();
        insertIndex.Transaction = transaction;
        insertIndex.CommandText =
            """
            INSERT INTO repository_indexes (repository_id, repository_json, manifest_json, graph_json, indexed_at)
            VALUES ($repository_id, $repository_json, $manifest_json, $graph_json, $indexed_at);
            """;
        insertIndex.Parameters.AddWithValue("$repository_id", index.Repository.Id);
        insertIndex.Parameters.AddWithValue("$repository_json", JsonSerializer.Serialize(index.Repository, JsonOptions));
        insertIndex.Parameters.AddWithValue("$manifest_json", JsonSerializer.Serialize(index.Manifest, JsonOptions));
        insertIndex.Parameters.AddWithValue("$graph_json", JsonSerializer.Serialize(index.Graph, JsonOptions));
        insertIndex.Parameters.AddWithValue("$indexed_at", index.Manifest.IndexedAt.ToString("O"));
        await insertIndex.ExecuteNonQueryAsync(cancellationToken);

        foreach (var document in index.Documents)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO repository_documents (id, repository_id, kind, title, content, symbol, file_path, language, metadata_json)
                VALUES ($id, $repository_id, $kind, $title, $content, $symbol, $file_path, $language, $metadata_json);
                """;
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$repository_id", index.Repository.Id);
            command.Parameters.AddWithValue("$kind", document.Kind);
            command.Parameters.AddWithValue("$title", document.Title);
            command.Parameters.AddWithValue("$content", document.Content);
            command.Parameters.AddWithValue("$symbol", (object?)document.Symbol ?? DBNull.Value);
            command.Parameters.AddWithValue("$file_path", (object?)document.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$language", (object?)document.Language ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(document.Metadata, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RepositoryIndex?> LoadAsync(string repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT repository_json, manifest_json, graph_json
            FROM repository_indexes
            WHERE repository_id = $repository_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var repository = JsonSerializer.Deserialize<RegisteredRepository>(reader.GetString(0), JsonOptions) ?? new RegisteredRepository();
        var manifest = JsonSerializer.Deserialize<RepositoryIndexManifest>(reader.GetString(1), JsonOptions) ?? new RepositoryIndexManifest();
        var graph = JsonSerializer.Deserialize<RepositoryKnowledgeGraph>(reader.GetString(2), JsonOptions) ?? new RepositoryKnowledgeGraph();

        var documents = await LoadDocumentsAsync(connection, repositoryId, cancellationToken);
        return new RepositoryIndex
        {
            Repository = repository,
            Manifest = manifest,
            Graph = graph,
            Documents = documents
        };
    }

    public async Task<RepositoryIndexManifest?> LoadManifestAsync(string repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT manifest_json
            FROM repository_indexes
            WHERE repository_id = $repository_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);

        var manifestJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        return manifestJson is null
            ? null
            : JsonSerializer.Deserialize<RepositoryIndexManifest>(manifestJson, JsonOptions);
    }

    public async Task<RepositoryQueryResult> QueryAsync(RepositoryQuery query, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var hits = new List<RepositoryQueryHit>();
        foreach (var repositoryId in query.RepositoryIds)
        {
            var documents = await LoadDocumentsAsync(connection, repositoryId, cancellationToken);
            hits.AddRange(documents
                .Select(document => Score(query, document))
                .Where(hit => hit.Score > 0)
                .OrderByDescending(hit => hit.Score)
                .Take(query.Limit));
        }

        return new RepositoryQueryResult
        {
            QueryText = query.Text,
            Intent = query.Intent,
            Hits = hits
                .OrderByDescending(hit => hit.Score)
                .Take(query.Limit)
                .ToList(),
            RelatedSymbols = hits
                .Where(hit => !string.IsNullOrWhiteSpace(hit.Symbol))
                .Select(hit => hit.Symbol!)
                .Distinct(StringComparer.Ordinal)
                .Take(query.Limit)
                .ToList()
        };
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS repository_indexes (
                repository_id TEXT PRIMARY KEY,
                repository_json TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                graph_json TEXT NOT NULL,
                indexed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repository_documents (
                id TEXT PRIMARY KEY,
                repository_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                symbol TEXT NULL,
                file_path TEXT NULL,
                language TEXT NULL,
                metadata_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteParameter CreateParameter(string name, object? value) => new(name, value ?? DBNull.Value);

    private static async Task<List<RepositoryIndexDocument>> LoadDocumentsAsync(
        SqliteConnection connection,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, repository_id, kind, title, content, symbol, file_path, language, metadata_json
            FROM repository_documents
            WHERE repository_id = $repository_id;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);

        var items = new List<RepositoryIndexDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RepositoryIndexDocument
            {
                Id = reader.GetString(0),
                RepositoryId = reader.GetString(1),
                Kind = reader.GetString(2),
                Title = reader.GetString(3),
                Content = reader.GetString(4),
                Symbol = reader.IsDBNull(5) ? null : reader.GetString(5),
                FilePath = reader.IsDBNull(6) ? null : reader.GetString(6),
                Language = reader.IsDBNull(7) ? null : reader.GetString(7),
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8), JsonOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }

        return items;
    }

    private static RepositoryQueryHit Score(RepositoryQuery query, RepositoryIndexDocument document)
    {
        var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = $"{document.Title}\n{document.Content}\n{document.Symbol}\n{document.FilePath}";
        var score = 0d;

        foreach (var term in terms)
        {
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }

            if (!string.IsNullOrWhiteSpace(document.Symbol) &&
                document.Symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.5;
            }
        }

        if (query.Intent == QueryIntent.SymbolLookup && document.Kind == "symbol")
        {
            score += 1.5;
        }

        if (query.Intent == QueryIntent.ImpactAnalysis && document.Kind == "card")
        {
            score += 0.5;
        }

        return new RepositoryQueryHit
        {
            RepositoryId = document.RepositoryId,
            Kind = document.Kind,
            Title = document.Title,
            Content = document.Content,
            Symbol = document.Symbol,
            FilePath = document.FilePath,
            Score = score
        };
    }
}
