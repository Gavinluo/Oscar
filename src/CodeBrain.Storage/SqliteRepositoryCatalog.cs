using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeBrain.Storage;

/// <summary>
/// Stores local repository registrations in a small SQLite catalog so the CLI/API
/// can address repositories by stable ids instead of repeatedly asking for paths.
/// </summary>
public sealed class SqliteRepositoryCatalog : IRepositoryCatalog
{
    private readonly string _connectionString;

    public SqliteRepositoryCatalog(string workspaceRoot, string artifactsRoot = "agent_artifacts")
    {
        var indexRoot = Path.Combine(workspaceRoot, artifactsRoot, "index");
        Directory.CreateDirectory(indexRoot);
        _connectionString = $"Data Source={Path.Combine(indexRoot, "codebrain.catalog.db")}";
        Initialize();
    }

    public async Task<RegisteredRepository> RegisterAsync(string rootPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {fullPath}");
        }

        var existing = await FindByRootAsync(fullPath, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var repository = new RegisteredRepository
        {
            Id = BuildRepositoryId(fullPath),
            DisplayName = Path.GetFileName(fullPath),
            RootPath = fullPath,
            AnalyzerId = "csharp-roslyn",
            PrimaryLanguage = "csharp"
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO repositories (id, display_name, root_path, kind, analyzer_id, primary_language, registered_at, last_indexed_at)
            VALUES ($id, $display_name, $root_path, $kind, $analyzer_id, $primary_language, $registered_at, NULL);
            """;
        command.Parameters.AddWithValue("$id", repository.Id);
        command.Parameters.AddWithValue("$display_name", repository.DisplayName);
        command.Parameters.AddWithValue("$root_path", repository.RootPath);
        command.Parameters.AddWithValue("$kind", repository.Kind.ToString());
        command.Parameters.AddWithValue("$analyzer_id", repository.AnalyzerId);
        command.Parameters.AddWithValue("$primary_language", repository.PrimaryLanguage);
        command.Parameters.AddWithValue("$registered_at", repository.RegisteredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return repository;
    }

    public async Task<IReadOnlyCollection<RegisteredRepository>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<RegisteredRepository>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, root_path, kind, analyzer_id, primary_language, registered_at, last_indexed_at
            FROM repositories
            ORDER BY display_name, id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadRepository(reader));
        }

        return items;
    }

    public async Task<RegisteredRepository?> GetAsync(string repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, root_path, kind, analyzer_id, primary_language, registered_at, last_indexed_at
            FROM repositories
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", repositoryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRepository(reader) : null;
    }

    public async Task UpdateLastIndexedAsync(string repositoryId, DateTimeOffset indexedAt, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE repositories SET last_indexed_at = $indexed_at WHERE id = $id;";
        command.Parameters.AddWithValue("$id", repositoryId);
        command.Parameters.AddWithValue("$indexed_at", indexedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<RegisteredRepository?> FindByRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, root_path, kind, analyzer_id, primary_language, registered_at, last_indexed_at
            FROM repositories
            WHERE root_path = $root_path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root_path", rootPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRepository(reader) : null;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS repositories (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                root_path TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                analyzer_id TEXT NOT NULL,
                primary_language TEXT NOT NULL,
                registered_at TEXT NOT NULL,
                last_indexed_at TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static RegisteredRepository ReadRepository(SqliteDataReader reader)
    {
        return new RegisteredRepository
        {
            Id = reader.GetString(0),
            DisplayName = reader.GetString(1),
            RootPath = reader.GetString(2),
            Kind = Enum.Parse<RepositoryKind>(reader.GetString(3), ignoreCase: true),
            AnalyzerId = reader.GetString(4),
            PrimaryLanguage = reader.GetString(5),
            RegisteredAt = DateTimeOffset.Parse(reader.GetString(6)),
            LastIndexedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
        };
    }

    private static string BuildRepositoryId(string rootPath)
    {
        var name = Path.GetFileName(rootPath).ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPath)))[..8]
            .ToLowerInvariant();
        return $"{name}-{hash}";
    }
}
