using CodeBrain.Analysis.Text;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;
using CodeBrain.Storage;

namespace CodeBrain.Tests;

[TestFixture]
public class HybridRetrievalTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codebrain-hybrid-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_root, true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch
        {
            // Test artifacts live under a unique temp directory, so cleanup
            // failures should not mask the retrieval assertions.
        }
    }

    [Test]
    public async Task QueryAsync_ReturnsHybridContextAndEditPlan()
    {
        var store = new SqliteRepositoryIndexStore(_root);
        var repository = new RegisteredRepository
        {
            Id = "demo-repo",
            DisplayName = "demo",
            RootPath = _root,
            AnalyzerId = "text-structure",
            PrimaryLanguage = "typescript"
        };

        await store.SaveAsync(new RepositoryIndex
        {
            Repository = repository,
            Manifest = new RepositoryIndexManifest
            {
                RepositoryId = repository.Id,
                RepositoryRoot = repository.RootPath,
                AnalyzerId = repository.AnalyzerId,
                PrimaryLanguage = repository.PrimaryLanguage,
                Files =
                [
                    new RepositoryFileFingerprint { RelativePath = "src/payment.ts", ContentHash = "1", Language = "typescript" }
                ]
            },
            Graph = new RepositoryKnowledgeGraph
            {
                RepositoryRoot = repository.RootPath,
                SourcePath = repository.RootPath,
                Nodes =
                [
                    new KnowledgeNode
                    {
                        Id = "symbol::src/payment.ts::PaymentService",
                        Kind = "symbol",
                        Label = "PaymentService",
                        Symbol = "src/payment.ts::PaymentService",
                        FilePath = Path.Combine(_root, "src", "payment.ts")
                    },
                    new KnowledgeNode
                    {
                        Id = "symbol::src/order.ts::OrderService",
                        Kind = "symbol",
                        Label = "OrderService",
                        Symbol = "src/order.ts::OrderService",
                        FilePath = Path.Combine(_root, "src", "order.ts")
                    }
                ],
                Edges =
                [
                    new KnowledgeEdge
                    {
                        From = "symbol::src/payment.ts::PaymentService",
                        To = "symbol::src/order.ts::OrderService",
                        Kind = "references"
                    }
                ],
                Summary = new GraphSummary { NodeCount = 2, EdgeCount = 1, ProjectCount = 1, SymbolCount = 2 }
            },
            Documents =
            [
                new RepositoryIndexDocument
                {
                    RepositoryId = repository.Id,
                    Kind = "symbol",
                    Title = "PaymentService",
                    Symbol = "src/payment.ts::PaymentService",
                    FilePath = Path.Combine(_root, "src", "payment.ts"),
                    Content = "Handles payment confirmation and retry flows.",
                    SearchText = "PaymentService payment confirmation retry flows"
                },
                new RepositoryIndexDocument
                {
                    RepositoryId = repository.Id,
                    Kind = "file",
                    Title = "order.ts",
                    FilePath = Path.Combine(_root, "src", "order.ts"),
                    Content = "Order service dispatches payment confirmation.",
                    SearchText = "Order service payment confirmation dispatch"
                }
            ]
        }, CancellationToken.None);

        var result = await store.QueryAsync(new RepositoryQuery
        {
            Text = "payment confirmation flow",
            Intent = QueryIntent.CodeQa,
            RepositoryIds = ["demo-repo"],
            Limit = 5,
            GraphDepth = 2
        }, CancellationToken.None);

        Assert.That(result.Hits, Is.Not.Empty);
        Assert.That(result.RetrievalStrategy, Is.EqualTo("bm25+vector+graph"));
        Assert.That(result.Context.Summary, Does.Contain("payment confirmation flow"));
        Assert.That(result.Context.Symbols, Contains.Item("src/payment.ts::PaymentService"));
        Assert.That(result.SuggestedEditPlan, Is.Not.Null);
        Assert.That(result.SuggestedEditPlan!.SymbolsToEdit, Contains.Item("src/payment.ts::PaymentService"));
    }

    [Test]
    public async Task QueryAsync_UsesInjectedEmbeddingProviderModel()
    {
        var provider = new KeywordEmbeddingProvider("test-provider-v1");
        var store = new SqliteRepositoryIndexStore(_root, embeddingProvider: provider);
        var repository = new RegisteredRepository
        {
            Id = "provider-repo",
            DisplayName = "provider-repo",
            RootPath = _root,
            AnalyzerId = "text-structure",
            PrimaryLanguage = "typescript"
        };

        await store.SaveAsync(new RepositoryIndex
        {
            Repository = repository,
            Manifest = new RepositoryIndexManifest
            {
                RepositoryId = repository.Id,
                RepositoryRoot = repository.RootPath,
                AnalyzerId = repository.AnalyzerId,
                PrimaryLanguage = repository.PrimaryLanguage
            },
            Graph = new RepositoryKnowledgeGraph(),
            Documents =
            [
                new RepositoryIndexDocument
                {
                    Id = "doc-payment",
                    RepositoryId = repository.Id,
                    Kind = "file",
                    Title = "payment.ts",
                    FilePath = Path.Combine(_root, "src", "payment.ts"),
                    Content = "payment token reconciliation",
                    SearchText = "payment token reconciliation"
                },
                new RepositoryIndexDocument
                {
                    Id = "doc-orders",
                    RepositoryId = repository.Id,
                    Kind = "file",
                    Title = "orders.ts",
                    FilePath = Path.Combine(_root, "src", "orders.ts"),
                    Content = "orders dashboard",
                    SearchText = "orders dashboard"
                }
            ]
        }, CancellationToken.None);

        var result = await store.QueryAsync(new RepositoryQuery
        {
            Text = "payment reconciliation",
            Intent = QueryIntent.CodeQa,
            RepositoryIds = [repository.Id],
            Limit = 5,
            GraphDepth = 1
        }, CancellationToken.None);

        Assert.That(result.Hits, Is.Not.Empty);
        Assert.That(result.Hits[0].Title, Is.EqualTo("payment.ts"));
        Assert.That(result.Hits[0].VectorScore, Is.GreaterThan(0.9d));
    }

    [Test]
    public void Detect_SelectsTextAnalyzerForNonCSharpRepository()
    {
        var repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "app.py"), "def run():\n    return 1\n");

        var (language, analyzerId) = RepositoryLanguageDetector.Detect(repoRoot);

        Assert.That(language, Is.EqualTo("python"));
        Assert.That(analyzerId, Is.EqualTo("text-structure"));
    }

    [Test]
    public async Task TextAnalyzer_IndexesSimpleSymbols()
    {
        var repoRoot = Path.Combine(_root, "text-repo");
        Directory.CreateDirectory(repoRoot);
        var sourcePath = Path.Combine(repoRoot, "service.py");
        await File.WriteAllTextAsync(sourcePath, "def process_order():\n    return True\n", CancellationToken.None);

        var analyzer = new TextStructureRepositoryAnalyzer();
        var result = await analyzer.AnalyzeAsync(new RepositoryAnalysisRequest
        {
            Repository = new RegisteredRepository
            {
                Id = "text-repo",
                DisplayName = "text-repo",
                RootPath = repoRoot,
                PrimaryLanguage = "python",
                AnalyzerId = analyzer.Id
            },
            ChangeSet = new RepositoryChangeSet()
        }, CancellationToken.None);

        Assert.That(result.Documents.Any(doc => doc.Kind == "symbol" && doc.Title == "process_order"), Is.True);
        Assert.That(result.Graph.Nodes.Any(node => node.Symbol == "service.py::process_order"), Is.True);
    }

    private sealed class KeywordEmbeddingProvider(string modelId) : IEmbeddingProvider
    {
        public Task<RepositoryEmbedding> EmbedAsync(string? text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = text ?? string.Empty;
            var values = new double[3];
            values[0] = normalized.Contains("payment", StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
            values[1] = normalized.Contains("reconciliation", StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
            values[2] = normalized.Contains("orders", StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
            return Task.FromResult(new RepositoryEmbedding
            {
                Model = modelId,
                Values = values
            });
        }
    }
}
