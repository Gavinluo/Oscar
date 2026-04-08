using System.Text.Json;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeBrain.Storage;

/// <summary>
/// Persists repository manifests, graph snapshots, retrieval documents, and
/// lightweight dense embeddings into SQLite. Query execution combines BM25-like
/// lexical scoring, deterministic local vectors, and graph expansion so the
/// same persisted index can answer both direct lookup and broader repository
/// questions.
/// </summary>
public sealed class SqliteRepositoryIndexStore : IRepositoryIndexStore, IRepositoryQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HybridContextAssembler ContextAssembler = new();
    private static readonly HybridEditPlanningService EditPlanningService = new();
    private readonly string _connectionString;
    private readonly IEmbeddingProvider _embeddingProvider;

    public SqliteRepositoryIndexStore(
        string workspaceRoot,
        string artifactsRoot = "agent_artifacts",
        IEmbeddingProvider? embeddingProvider = null)
    {
        var indexRoot = Path.Combine(workspaceRoot, artifactsRoot, "index");
        Directory.CreateDirectory(indexRoot);
        _connectionString = $"Data Source={Path.Combine(indexRoot, "codebrain.index.db")}";
        _embeddingProvider = embeddingProvider ?? new LocalHashEmbeddingProvider();
        Initialize();
    }

    public async Task SaveAsync(RepositoryIndex index, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)dbTransaction;

        await ExecuteAsync(connection, transaction,
            "DELETE FROM repository_embeddings WHERE repository_id = $repository_id;",
            [CreateParameter("$repository_id", index.Repository.Id)], cancellationToken);
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
            document.SearchText = string.IsNullOrWhiteSpace(document.SearchText)
                ? $"{document.Title} {document.Content} {document.Symbol} {document.FilePath}"
                : document.SearchText;
            var embedding = await _embeddingProvider.EmbedAsync(document.SearchText, cancellationToken);

            var documentCommand = connection.CreateCommand();
            documentCommand.Transaction = transaction;
            documentCommand.CommandText =
                """
                INSERT INTO repository_documents (id, repository_id, kind, title, content, symbol, file_path, language, search_text, metadata_json)
                VALUES ($id, $repository_id, $kind, $title, $content, $symbol, $file_path, $language, $search_text, $metadata_json);
                """;
            documentCommand.Parameters.AddWithValue("$id", document.Id);
            documentCommand.Parameters.AddWithValue("$repository_id", index.Repository.Id);
            documentCommand.Parameters.AddWithValue("$kind", document.Kind);
            documentCommand.Parameters.AddWithValue("$title", document.Title);
            documentCommand.Parameters.AddWithValue("$content", document.Content);
            documentCommand.Parameters.AddWithValue("$symbol", (object?)document.Symbol ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue("$file_path", (object?)document.FilePath ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue("$language", (object?)document.Language ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue("$search_text", document.SearchText);
            documentCommand.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(document.Metadata, JsonOptions));
            await documentCommand.ExecuteNonQueryAsync(cancellationToken);

            var embeddingCommand = connection.CreateCommand();
            embeddingCommand.Transaction = transaction;
            embeddingCommand.CommandText =
                """
                INSERT INTO repository_embeddings (document_id, repository_id, embedding_json, embedding_model)
                VALUES ($document_id, $repository_id, $embedding_json, $embedding_model);
                """;
            embeddingCommand.Parameters.AddWithValue("$document_id", document.Id);
            embeddingCommand.Parameters.AddWithValue("$repository_id", index.Repository.Id);
            embeddingCommand.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(embedding.Values, JsonOptions));
            embeddingCommand.Parameters.AddWithValue("$embedding_model", embedding.Model);
            await embeddingCommand.ExecuteNonQueryAsync(cancellationToken);
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
        var allHits = new List<RepositoryQueryHit>();
        var relatedNodes = new List<KnowledgeNode>();

        foreach (var repositoryId in query.RepositoryIds)
        {
            var index = await LoadAsync(repositoryId, cancellationToken);
            if (index is null)
            {
                continue;
            }

            var queryEmbedding = await _embeddingProvider.EmbedAsync(query.Text, cancellationToken);
            var embeddings = await LoadEmbeddingsAsync(repositoryId, cancellationToken);
            var repoHits = ScoreRepository(query, index, embeddings, queryEmbedding);
            allHits.AddRange(repoHits);
            relatedNodes.AddRange(ExpandGraphNeighborhood(index.Graph, repoHits, query.GraphDepth));
        }

        var topHits = allHits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Title, StringComparer.OrdinalIgnoreCase)
            .Take(query.Limit)
            .ToList();
        var context = ContextAssembler.Assemble(query, topHits, relatedNodes);

        return new RepositoryQueryResult
        {
            QueryText = query.Text,
            Intent = query.Intent,
            Hits = topHits,
            RelatedSymbols = context.Symbols,
            RetrievalStrategy = "bm25+vector+graph",
            Context = context,
            SuggestedEditPlan = EditPlanningService.BuildPlan(query, context)
        };
    }

    private List<RepositoryQueryHit> ScoreRepository(
        RepositoryQuery query,
        RepositoryIndex index,
        IReadOnlyDictionary<string, RepositoryEmbedding> embeddings,
        RepositoryEmbedding queryEmbedding)
    {
        var docs = index.Documents;
        var queryTerms = Tokenize(query.Text);
        var bm25Scores = ComputeBm25Scores(docs, queryTerms);
        var vectorScores = docs.ToDictionary(
            document => document.Id,
            document => embeddings.TryGetValue(document.Id, out var embedding)
                && string.Equals(embedding.Model, queryEmbedding.Model, StringComparison.Ordinal)
                    ? LocalVectorMath.CosineSimilarity(queryEmbedding.Values, embedding.Values)
                : 0d,
            StringComparer.Ordinal);
        var graphBoosts = ComputeGraphBoosts(index.Graph, docs, bm25Scores, vectorScores, query.GraphDepth);

        var hits = new List<RepositoryQueryHit>(docs.Count);
        foreach (var document in docs)
        {
            var bm25 = bm25Scores.TryGetValue(document.Id, out var bm25Score) ? bm25Score : 0d;
            var vector = vectorScores.TryGetValue(document.Id, out var vectorScore) ? vectorScore : 0d;
            var graph = graphBoosts.TryGetValue(document.Id, out var graphScore) ? graphScore : 0d;
            if (!ShouldKeepDocument(query.Intent, document, bm25, vector, graph))
            {
                continue;
            }

            var finalScore = BlendScores(query.Intent, document.Kind, bm25, vector, graph);

            if (finalScore <= 0)
            {
                continue;
            }

            hits.Add(new RepositoryQueryHit
            {
                RepositoryId = document.RepositoryId,
                Kind = document.Kind,
                Title = document.Title,
                Content = document.Content,
                Symbol = document.Symbol,
                FilePath = document.FilePath,
                Bm25Score = bm25,
                VectorScore = vector,
                GraphScore = graph,
                Score = finalScore,
                RankReason = BuildRankReason(query.Intent, bm25, vector, graph)
            });
        }

        return hits;
    }

    private static Dictionary<string, double> ComputeBm25Scores(
        IReadOnlyCollection<RepositoryIndexDocument> documents,
        IReadOnlyCollection<string> queryTerms)
    {
        var terms = queryTerms.Count == 0 ? Tokenize(string.Join(' ', documents.Select(doc => doc.Title).Take(1))) : queryTerms;
        var tokenizedDocs = documents.ToDictionary(doc => doc.Id, doc => Tokenize(doc.SearchText));
        var documentCount = Math.Max(documents.Count, 1);
        var averageLength = tokenizedDocs.Values.Count == 0 ? 1d : tokenizedDocs.Values.Average(tokens => Math.Max(tokens.Count, 1));
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var term in terms.Distinct(StringComparer.Ordinal))
        {
            var docsWithTerm = tokenizedDocs.Values.Count(tokens => tokens.Contains(term));
            if (docsWithTerm == 0)
            {
                continue;
            }

            var idf = Math.Log(1 + (documentCount - docsWithTerm + 0.5) / (docsWithTerm + 0.5));
            foreach (var (documentId, tokens) in tokenizedDocs)
            {
                var frequency = tokens.Count(token => string.Equals(token, term, StringComparison.Ordinal));
                if (frequency == 0)
                {
                    continue;
                }

                const double k1 = 1.2;
                const double b = 0.75;
                var docLength = Math.Max(tokens.Count, 1);
                var normalized = frequency * (k1 + 1) /
                                 (frequency + k1 * (1 - b + b * docLength / averageLength));
                scores[documentId] = scores.TryGetValue(documentId, out var existing)
                    ? existing + idf * normalized
                    : idf * normalized;
            }
        }

        return scores;
    }

    private static Dictionary<string, double> ComputeGraphBoosts(
        RepositoryKnowledgeGraph graph,
        IReadOnlyCollection<RepositoryIndexDocument> documents,
        IReadOnlyDictionary<string, double> bm25Scores,
        IReadOnlyDictionary<string, double> vectorScores,
        int depth)
    {
        var symbolSeeds = documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Symbol))
            .Select(document => new
            {
                Document = document,
                SeedScore = (bm25Scores.TryGetValue(document.Id, out var bm25) ? bm25 : 0d) +
                            (vectorScores.TryGetValue(document.Id, out var vector) ? vector : 0d)
            })
            .Where(item => item.SeedScore > 0.05d)
            .OrderByDescending(item => item.SeedScore)
            .Take(5)
            .Select(item => item.Document.Symbol!)
            .ToList();

        var graphScores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (symbolSeeds.Count == 0)
        {
            return graphScores;
        }

        var nodeBySymbol = graph.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Symbol))
            .ToDictionary(node => node.Symbol!, node => node, StringComparer.Ordinal);
        var docBySymbol = documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Symbol))
            .GroupBy(document => document.Symbol!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var docByFile = documents
            .Where(document => !string.IsNullOrWhiteSpace(document.FilePath))
            .GroupBy(document => document.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<(string NodeId, int Distance)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbolSeeds)
        {
            if (!nodeBySymbol.TryGetValue(symbol, out var node))
            {
                continue;
            }

            visited.Add(node.Id);
            queue.Enqueue((node.Id, 0));
        }

        while (queue.Count > 0)
        {
            var (nodeId, distance) = queue.Dequeue();
            if (distance >= Math.Max(depth, 1))
            {
                continue;
            }

            foreach (var edge in graph.Edges.Where(edge => edge.From == nodeId || edge.To == nodeId))
            {
                var nextId = edge.From == nodeId ? edge.To : edge.From;
                if (!visited.Add(nextId))
                {
                    continue;
                }

                var nextNode = graph.Nodes.FirstOrDefault(node => node.Id == nextId);
                if (nextNode is null)
                {
                    continue;
                }

                var boost = 1d / (distance + 2d);
                if (!string.IsNullOrWhiteSpace(nextNode.Symbol) &&
                    docBySymbol.TryGetValue(nextNode.Symbol, out var symbolDocs))
                {
                    foreach (var document in symbolDocs)
                    {
                        graphScores[document.Id] = graphScores.TryGetValue(document.Id, out var existing)
                            ? Math.Max(existing, boost)
                            : boost;
                    }
                }

                if (!string.IsNullOrWhiteSpace(nextNode.FilePath) &&
                    docByFile.TryGetValue(nextNode.FilePath, out var fileDocs))
                {
                    foreach (var document in fileDocs)
                    {
                        graphScores[document.Id] = graphScores.TryGetValue(document.Id, out var existing)
                            ? Math.Max(existing, boost * 0.8)
                            : boost * 0.8;
                    }
                }

                queue.Enqueue((nextId, distance + 1));
            }
        }

        return graphScores;
    }

    private static double BlendScores(QueryIntent intent, string kind, double bm25, double vector, double graph)
    {
        if (bm25 <= 0d && vector <= 0d && graph <= 0d)
        {
            return 0d;
        }

        var (bm25Weight, vectorWeight, graphWeight) = intent switch
        {
            QueryIntent.SymbolLookup => (0.7, 0.2, 0.1),
            QueryIntent.ImpactAnalysis => (0.25, 0.15, 0.60),
            QueryIntent.TestGeneration => (0.30, 0.20, 0.50),
            QueryIntent.BugLocalization => (0.45, 0.35, 0.20),
            _ => (0.40, 0.40, 0.20)
        };

        var kindBoost = kind switch
        {
            "symbol" when intent == QueryIntent.SymbolLookup => 0.5,
            "file" when intent == QueryIntent.CodeQa => 0.2,
            "namespace" when intent == QueryIntent.CodeQa => 0.1,
            _ => 0d
        };

        return bm25 * bm25Weight + vector * vectorWeight + graph * graphWeight + kindBoost;
    }

    private static bool ShouldKeepDocument(
        QueryIntent intent,
        RepositoryIndexDocument document,
        double bm25,
        double vector,
        double graph)
    {
        if (string.Equals(document.Kind, "symbol", StringComparison.OrdinalIgnoreCase) &&
            !IsActionableSymbolDocument(document))
        {
            return false;
        }

        return intent switch
        {
            QueryIntent.SymbolLookup => bm25 > 0d || graph >= 0.4d || IsStrongSemanticMatch(vector),
            QueryIntent.ImpactAnalysis => (bm25 > 0d || vector > 0.2d) && (graph > 0d || bm25 > 0.5d),
            QueryIntent.TestGeneration => bm25 > 0d || vector > 0.22d || graph > 0.2d,
            QueryIntent.BugLocalization => bm25 > 0d || vector > 0.24d || graph > 0.2d,
            _ => bm25 > 0d || vector > 0.22d || graph > 0.2d
        };
    }

    private static bool IsStrongSemanticMatch(double vector)
    {
        return vector >= 0.7d;
    }

    private static bool IsActionableSymbolDocument(RepositoryIndexDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Title) ||
            string.IsNullOrWhiteSpace(document.Symbol) ||
            string.IsNullOrWhiteSpace(document.FilePath))
        {
            return false;
        }

        var symbol = document.Symbol!;
        if (document.Title.Length <= 2)
        {
            return false;
        }

        if (symbol.Contains("<anonymous type", StringComparison.OrdinalIgnoreCase) ||
            symbol.Contains("AnonymousType", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (symbol.StartsWith("global::System.", StringComparison.Ordinal) ||
            symbol.StartsWith("global::Microsoft.", StringComparison.Ordinal) ||
            symbol.StartsWith("global::Hangfire.", StringComparison.Ordinal) ||
            symbol.StartsWith("global::Newtonsoft.", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string BuildRankReason(QueryIntent intent, double bm25, double vector, double graph)
    {
        return $"intent={intent}; bm25={bm25:F3}; vector={vector:F3}; graph={graph:F3}";
    }

    private static List<KnowledgeNode> ExpandGraphNeighborhood(
        RepositoryKnowledgeGraph graph,
        IReadOnlyCollection<RepositoryQueryHit> hits,
        int depth)
    {
        var symbols = hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.Symbol))
            .Select(hit => hit.Symbol!)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (symbols.Count == 0)
        {
            return new List<KnowledgeNode>();
        }

        var symbolNodes = graph.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Symbol))
            .ToDictionary(node => node.Symbol!, node => node, StringComparer.Ordinal);
        var nodeLookup = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string NodeId, int Depth)>();
        var result = new List<KnowledgeNode>();

        foreach (var symbol in symbols)
        {
            if (!symbolNodes.TryGetValue(symbol, out var node))
            {
                continue;
            }

            visited.Add(node.Id);
            queue.Enqueue((node.Id, 0));
        }

        while (queue.Count > 0)
        {
            var (nodeId, currentDepth) = queue.Dequeue();
            if (currentDepth >= Math.Max(depth, 1))
            {
                continue;
            }

            foreach (var edge in graph.Edges.Where(edge => edge.From == nodeId || edge.To == nodeId))
            {
                var nextId = edge.From == nodeId ? edge.To : edge.From;
                if (!visited.Add(nextId) || !nodeLookup.TryGetValue(nextId, out var node))
                {
                    continue;
                }

                result.Add(node);
                queue.Enqueue((nextId, currentDepth + 1));
            }
        }

        return result;
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
                search_text TEXT NOT NULL DEFAULT '',
                metadata_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repository_embeddings (
                document_id TEXT NOT NULL,
                repository_id TEXT NOT NULL,
                embedding_json TEXT NOT NULL,
                embedding_model TEXT NOT NULL,
                PRIMARY KEY (document_id, repository_id)
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "repository_documents", "search_text", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";
        var exists = false;
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
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
            SELECT id, repository_id, kind, title, content, symbol, file_path, language, search_text, metadata_json
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
                SearchText = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(9), JsonOptions) ??
                           new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }

        return items;
    }

    private async Task<IReadOnlyDictionary<string, RepositoryEmbedding>> LoadEmbeddingsAsync(string repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, embedding_json, embedding_model
            FROM repository_embeddings
            WHERE repository_id = $repository_id;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);

        var result = new Dictionary<string, RepositoryEmbedding>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embedding = JsonSerializer.Deserialize<double[]>(reader.GetString(1), JsonOptions);
            if (embedding is null)
            {
                continue;
            }

            result[reader.GetString(0)] = new RepositoryEmbedding
            {
                Values = embedding,
                Model = reader.GetString(2)
            };
        }

        return result;
    }

    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Split([' ', '\r', '\n', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '<', '>'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .Where(token => token.Length > 1)
            .ToList();
    }
}
