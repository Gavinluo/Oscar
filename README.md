# CodeBrain

CodeBrain is a local repository intelligence and closed-loop engineering
assistant built on .NET 8. The current version supports persistent indexing,
hybrid retrieval, context assembly, edit planning, Git-aware incremental
analysis, and a closed-loop workflow for test-oriented validation.

Architecture details: [ARCHITECTURE.md](/D:/code/CodeBase/agentic_test_loop/ARCHITECTURE.md)

## Current Capabilities

- Local repository registration backed by SQLite.
- Persistent repository indexes with manifest, graph snapshot, retrieval
  documents, and local dense embeddings.
- Git-aware incremental analysis with filesystem fallback.
- Two analyzer backends:
  - `csharp-roslyn` for C# solutions and projects.
  - `text-structure` for non-C# local repositories using structural text
    analysis.
- Hybrid retrieval:
  - BM25-style lexical scoring.
  - Local vector scoring.
  - Graph expansion and reranking.
- Context assembly that returns:
  - evidence hits,
  - candidate symbols,
  - candidate files,
  - graph neighbors,
  - verification targets.
- Edit planning that turns retrieval context into a bounded modification plan.
- Closed-loop workflow for:
  - understanding,
  - drilldown,
  - change scope planning,
  - edit planning,
  - test planning,
  - test scaffold generation,
  - execution,
  - coverage verification,
  - draft-to-stable memory promotion.

## Project Layout

- `src/CodeBrain.Core`
  Shared contracts, repository models, context/editing models, and pipeline
  state.
- `src/CodeBrain.Storage`
  SQLite catalog, persisted index store, Git-aware incremental planner, hybrid
  retrieval support.
- `src/CodeBrain.Analysis.CSharp`
  Roslyn-based analyzer for C# repositories.
- `src/CodeBrain.Analysis.Text`
  Lightweight structural analyzer for non-C# repositories.
- `src/CodeBrain.Workflows`
  Closed-loop agents and orchestrator.
- `src/CodeBrain.Execution`
  Test execution and coverage collection.
- `src/CodeBrain.Cli`
  CLI entry point.
- `src/CodeBrain.Api`
  Local HTTP API and browser UI.
- `tests/CodeBrain.Tests`
  Unit tests for storage, indexing, and retrieval behavior.

## Build

```bash
dotnet build CodeBrain.sln
dotnet test CodeBrain.sln --no-build
```

## Register a Repository

```bash
dotnet run --project src/CodeBrain.Cli -- repos add --path <local-repository-path>
dotnet run --project src/CodeBrain.Cli -- repos list
```

Repository registration auto-detects the primary language and selects the
default analyzer backend.

## Build or Refresh an Index

```bash
dotnet run --project src/CodeBrain.Cli -- index --repo <repository-id>
```

Indexing behavior:

- Loads the previous manifest if one exists.
- Uses Git change information when the target repository is a Git checkout.
- Falls back to filesystem fingerprints when Git is unavailable.
- Persists:
  - repository manifest,
  - graph snapshot,
  - retrieval documents,
  - local dense embeddings.

Expected incremental behavior:

- The first run after upgrading an older index can report one-time additions if
  new tracked file types were added to the scanner.
- The second run on an unchanged repository should normally report:
  - `added = 0`
  - `modified = 0`
  - `removed = 0`

## Run Hybrid Retrieval

```bash
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "Where is payment confirmation handled?" --intent codeqa --graph-depth 2
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "FindAsync" --intent symbol
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "What breaks if token billing changes?" --intent impact --graph-depth 3
```

Supported intents:

- `codeqa`
- `symbol`
- `impact`
- `test`
- `bug`

Query responses now include:

- ranked hits,
- score breakdown (`bm25`, `vector`, `graph`),
- assembled repository context,
- suggested edit plan.

## Initialize the Closed-loop Workflow

```bash
dotnet run --project src/CodeBrain.Cli -- init --sln <path-to-sln>
```

Initialization behavior:

- Reuses an existing NUnit test project when possible.
- Creates `RepoGeneratedTests` when needed.
- Writes `codebrain.config.json`.
- Prepares `agent_artifacts`.

## Run the Closed-loop Workflow

```bash
dotnet run --project src/CodeBrain.Cli -- run \
  --sln <path-to-sln-or-csproj> \
  --target <symbol> \
  --topk 5 \
  --depth 3 \
  --coverage-line 0.6 \
  --coverage-branch 0.4 \
  --iterations 10
```

Closed-loop workflow stages:

1. Repository mapping.
2. Understanding card generation.
3. Drilldown dependency analysis.
4. Change scope planning.
5. Edit plan generation.
6. Test plan generation.
7. Test scaffold generation.
8. Test execution.
9. Coverage evaluation.
10. Memory promotion if thresholds are met.

The generated workflow artifacts now include:

- `*.changescope.json`
- `*.editplan.json`
- `*.testplan.json`
- generated test scaffolds
- coverage and run reports

## API and UI

```bash
dotnet run --project src/CodeBrain.Cli -- serve --port 5088
```

Important endpoints:

- `GET /api/repos`
- `POST /api/repos`
- `POST /api/index/{repositoryId}`
- `POST /api/query`
- `GET /api/graph/summary?repositoryId=<id>`
- `GET /api/graph/context?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/graph/impact?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/source/snippet?repositoryId=<id>&symbol=<full-symbol>`

The browser workspace now supports:

- repository selection and reindexing,
- direct natural-language repository questions,
- hybrid answer rendering with score breakdown,
- suggested edit-plan presentation,
- evidence cards,
- source snippet preview,
- symbol context and impact exploration.

## Workspace Resolution Rules

- `repos add`, `repos list`, `index`, `query`, and `serve` always resolve the
  CodeBrain workspace from `CodeBrain.sln`.
- `init` and `run` resolve the target repository workspace from the explicit
  `--sln` or `--project` path.
- This keeps the local catalog/index databases stable while ensuring generated
  test projects and artifacts land in the target repository.

## Recommended Validation Flow

```bash
dotnet build CodeBrain.sln
dotnet run --no-build --project .\src\CodeBrain.Cli -- repos add --path <repo-path>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- query --repo <repo-id> --q "target method" --intent symbol
dotnet run --no-build --project .\src\CodeBrain.Cli -- drill --sln <path-to-sln> --symbol "<symbol-from-query>"
```

Using `--no-build` is recommended after the first successful build to skip the
normal restore/build check that `dotnet run` performs before each launch.
