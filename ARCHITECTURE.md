# CodeBrain Architecture

## Overview

CodeBrain now has three major execution paths that share the same persisted
repository intelligence model:

1. Index pipeline.
2. Query pipeline.
3. Closed-loop modification and verification pipeline.

## System View

```mermaid
flowchart LR
    U["User / CLI / API"] --> CLI["CodeBrain.Cli"]
    U --> API["CodeBrain.Api"]

    CLI --> CATALOG["Repository Catalog (SQLite)"]
    CLI --> INDEX["Index Pipeline"]
    CLI --> QUERY["Hybrid Query Pipeline"]
    CLI --> LOOP["Closed-loop Workflow"]

    API --> CATALOG
    API --> INDEX
    API --> QUERY

    INDEX --> ANALYZERS["Analyzer Backends"]
    ANALYZERS --> ROSLYN["CodeBrain.Analysis.CSharp"]
    ANALYZERS --> TEXT["CodeBrain.Analysis.Text"]

    INDEX --> STORE["SqliteRepositoryIndexStore"]
    QUERY --> STORE
    LOOP --> STORE

    LOOP --> EXEC["CodeBrain.Execution"]
    LOOP --> ART["agent_artifacts"]
```

## Index Pipeline

```mermaid
flowchart LR
    A["repos add"] --> B["RepositoryLanguageDetector"]
    B --> C["RegisteredRepository"]
    C --> D["index --repo <id>"]
    D --> E["Load previous manifest"]
    E --> F["Git-aware incremental planner"]
    F --> G["Resolve analyzer backend"]
    G --> H["Analyzer emits map + graph + documents"]
    H --> I["Local embeddings generated"]
    I --> J["SQLite persisted index"]
```

Key points:

- Repository registration selects the analyzer backend automatically.
- `LocalFileIncrementalIndexPlanner` now prefers Git state when available.
- The manifest records:
  - analyzer id,
  - primary language,
  - head commit,
  - change detection mode,
  - file fingerprints.

## Query Pipeline

```mermaid
flowchart LR
    A["query"] --> B["Load persisted docs + graph + embeddings"]
    B --> C["BM25 lexical scorer"]
    B --> D["Local vector scorer"]
    B --> E["Graph neighborhood expansion"]
    C --> F["Hybrid ranker"]
    D --> F
    E --> F
    F --> G["Context assembler"]
    G --> H["Edit planning service"]
    H --> I["RepositoryQueryResult"]
```

The hybrid query result includes:

- ranked hits,
- score decomposition,
- related symbols,
- assembled context bundle,
- suggested edit plan.

## Closed-loop Workflow

```mermaid
flowchart LR
    A["run"] --> B["RepoMapperAgent"]
    B --> C["UnderstanderAgent"]
    C --> D["DrilldownNavigatorAgent"]
    D --> E["ChangeScopeAgent"]
    E --> F["EditPlannerAgent"]
    F --> G["TestPlannerAgent"]
    G --> H["TestWriterAgent"]
    H --> I["RunnerAgent"]
    I --> J["CoverageAgent"]
    J --> K{"Threshold met?"}
    K -- "No" --> C
    K -- "Yes" --> L["MemoryAgent"]
```

This is the current implementation of the modification execution loop:

- retrieval and drilldown narrow the probable edit surface,
- change scope captures bounded impact,
- edit plan defines files, symbols, and verification targets,
- test generation and execution validate the proposed change path.

## Analyzer Backends

### `csharp-roslyn`

Responsibilities:

- solution/project loading,
- symbol resolution,
- call graph extraction,
- knowledge graph generation,
- C# retrieval document generation.

### `text-structure`

Responsibilities:

- non-C# file discovery,
- simple declaration extraction,
- simple import/reference extraction,
- structural graph generation,
- file/symbol document generation for retrieval.

This backend is intentionally lightweight. Its purpose is to make the analyzer
layer extensible before a deeper multi-language implementation is added.

## Core Data Models

- `RegisteredRepository`
  Repository identity and backend selection.
- `RepositoryIndexManifest`
  Persisted index metadata and Git/file fingerprints.
- `RepositoryChangeSet`
  Git-aware or filesystem-aware incremental diff summary.
- `RepositoryIndexDocument`
  Retrieval unit with explicit `SearchText`.
- `RepositoryContextBundle`
  Assembled evidence packet for QA and editing.
- `RepositoryChangeScope`
  Bounded edit surface derived from graph context.
- `RepositoryEditPlan`
  Suggested files, symbols, and verification actions.

## Design Notes

- The local vector layer is deterministic and self-contained. It avoids an
  external embedding dependency while still exercising the vector branch of the
  hybrid retriever.
- Hybrid retrieval quality is intentionally explainable. Each hit exposes the
  BM25, vector, and graph contribution used to compute the final score.
- The second analyzer backend is structural rather than semantic. It is
  designed to validate the abstraction boundary first.

## Next Recommended Improvements

1. Replace the local vector implementation with a pluggable embedding provider.
2. Add Git diff range selection and staged/unstaged filtering to the planner.
3. Move hybrid retrieval into a dedicated `CodeBrain.Search` project.
4. Replace placeholder test scaffolds with edit-aware test synthesis.
5. Extend the second backend from structural parsing to real AST-based parsing.
