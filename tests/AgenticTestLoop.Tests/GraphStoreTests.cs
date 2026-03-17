using AgenticTestLoop.Core.Abstractions;
using AgenticTestLoop.Core.Models;
using AgenticTestLoop.Storage;

namespace AgenticTestLoop.Tests;

[TestFixture]
public class GraphStoreTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "atl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
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
            catch (IOException)
            {
                return;
            }
        }
    }

    [Test]
    public async Task SaveGraphAsync_PersistsQueryableNodes()
    {
        var store = new FileArtifactStore(_root);
        var graphStore = (IKnowledgeGraphStore)store;
        var graph = new RepositoryKnowledgeGraph
        {
            RepositoryRoot = _root,
            SourcePath = Path.Combine(_root, "demo.sln"),
            Nodes = new()
            {
                new KnowledgeNode
                {
                    Id = "symbol::global::Demo.Service.Run()",
                    Kind = "symbol",
                    Label = "Run",
                    Symbol = "global::Demo.Service.Run()",
                    Namespace = "Demo",
                    FilePath = Path.Combine(_root, "Service.cs"),
                    StartLine = 10,
                    EndLine = 20
                },
                new KnowledgeNode
                {
                    Id = "symbol::global::Demo.Service.Helper()",
                    Kind = "symbol",
                    Label = "Helper",
                    Symbol = "global::Demo.Service.Helper()",
                    Namespace = "Demo"
                }
            },
            Edges = new()
            {
                new KnowledgeEdge
                {
                    From = "symbol::global::Demo.Service.Run()",
                    To = "symbol::global::Demo.Service.Helper()",
                    Kind = "calls"
                }
            },
            Summary = new GraphSummary
            {
                NodeCount = 2,
                EdgeCount = 1,
                ProjectCount = 1,
                SymbolCount = 2
            }
        };

        await graphStore.SaveGraphAsync(graph, CancellationToken.None);

        var summary = await graphStore.GetSummaryAsync(CancellationToken.None);
        var nodes = await graphStore.SearchNodesAsync("Run", 10, CancellationToken.None);
        var context = await graphStore.GetSymbolContextAsync("global::Demo.Service.Run()", CancellationToken.None);

        Assert.That(summary.NodeCount, Is.EqualTo(2));
        Assert.That(nodes.Select(n => n.Symbol), Contains.Item("global::Demo.Service.Run()"));
        Assert.That(context?.OutgoingEdges.Count, Is.EqualTo(1));
        Assert.That(context?.RelatedNodes.Select(n => n.Symbol), Contains.Item("global::Demo.Service.Helper()"));
    }
}
