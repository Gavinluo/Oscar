# CodeBrain 架构说明

## 概览

CodeBrain 当前包含两条并行能力链：

- 仓库智能链：本地仓库注册、增量索引、持久化索引加载、仓库检索
- 测试闭环链：理解、下钻、测试计划、测试生成、执行、覆盖率、记忆升级

## 总体架构

```mermaid
flowchart LR
    U["用户 / CLI / API"] --> CLI["CodeBrain.Cli\nrepos / index / query / run / serve"]
    U --> API["CodeBrain.Api\nREST + UI"]

    CLI --> CATALOG["Repository Catalog\nSQLite"]
    CLI --> INDEX["Index Pipeline"]
    CLI --> QUERY["Query Pipeline"]
    CLI --> LOOP["Closed-loop Test Workflow"]

    API --> CATALOG
    API --> INDEX
    API --> QUERY

    INDEX --> ANALYZER["CodeBrain.Analysis.CSharp\nRoslyn Analyzer"]
    INDEX --> STORE["CodeBrain.Storage\nIndex Store / Artifact Store"]
    QUERY --> STORE
    LOOP --> ANALYZER
    LOOP --> STORE
    LOOP --> EXEC["CodeBrain.Execution\nTests / Coverage"]

    STORE --> ART["agent_artifacts"]
    ANALYZER --> TARGET["Local Repository"]
```

## 当前项目依赖图

```mermaid
flowchart TD
    CORE["CodeBrain.Core"]
    ANALYZER["CodeBrain.Analysis.CSharp"]
    STORE["CodeBrain.Storage"]
    EXEC["CodeBrain.Execution"]
    WORKFLOWS["CodeBrain.Workflows"]
    CLI["CodeBrain.Cli"]
    API["CodeBrain.Api"]
    TESTS["CodeBrain.Tests"]

    ANALYZER --> CORE
    STORE --> CORE
    EXEC --> CORE
    WORKFLOWS --> CORE
    WORKFLOWS --> ANALYZER
    WORKFLOWS --> STORE
    WORKFLOWS --> EXEC
    CLI --> CORE
    CLI --> ANALYZER
    CLI --> STORE
    CLI --> EXEC
    CLI --> WORKFLOWS
    API --> CORE
    API --> ANALYZER
    API --> STORE
    TESTS --> CORE
    TESTS --> ANALYZER
    TESTS --> STORE
    TESTS --> EXEC
    TESTS --> WORKFLOWS
    TESTS --> API
```

## 索引链路

```mermaid
flowchart LR
    A["codebrain repos add"] --> B["Repository Catalog"]
    B --> C["codebrain index --repo <id>"]
    C --> D["Load previous manifest"]
    D --> E["Compare local files"]
    E --> F["RepositoryChangeSet"]
    F --> G["CSharpRepositoryAnalyzer"]
    G --> H["RepositoryIndex\nmanifest + graph + documents"]
    H --> I["SQLite persisted index"]
```

关键点：

- 只支持 `LocalPath`
- 增量索引目前是文件级
- 检索文档来自图谱节点和符号摘要
- 图谱快照和检索文档统一持久化

## 查询链路

```mermaid
flowchart LR
    A["codebrain query"] --> B["Load persisted index"]
    B --> C["Parse QueryIntent"]
    C --> D["RepositoryQuery"]
    D --> E["RepositoryIndexDocument scoring"]
    E --> F["RepositoryQueryResult"]
```

当前支持的 `QueryIntent`：

- `CodeQa`
- `SymbolLookup`
- `ImpactAnalysis`
- `TestGeneration`
- `BugLocalization`

当前检索策略仍然是轻量规则检索，不包含向量检索。后续可以在 `IRepositoryQueryService` 背后替换为 BM25 + 向量 + 图融合检索，而不改 CLI/API 契约。

## 测试闭环链路

```mermaid
flowchart LR
    A["codebrain run"] --> B["RepoMapper"]
    B --> C["Understander"]
    C --> D["DrilldownNavigator"]
    D --> E["TestPlanner"]
    E --> F["TestWriter"]
    F --> G["Runner"]
    G --> H["Coverage"]
    H --> I{"Coverage OK?"}
    I -- "No" --> C
    I -- "Yes" --> J["Memory"]
```

测试闭环仍然复用：

- Roslyn 图谱
- 理解卡片
- 测试执行与覆盖率收集
- `draft -> stable` 的记忆升级规则

## 数据模型分层

- `RegisteredRepository`
  - 仓库目录项
- `RepositoryIndexManifest`
  - 上次索引的文件指纹集合
- `RepositoryChangeSet`
  - 本次增量差异
- `RepositoryIndexDocument`
  - 检索文档
- `RepositoryIndex`
  - 持久化索引聚合
- `RepositoryQuery`
  - 查询请求
- `RepositoryQueryResult`
  - 查询结果

## 后续扩展方向

1. 增加 `IRepositoryAnalyzer` 的新 backend，实现多语言适配。
2. 在 `IRepositoryQueryService` 后增加向量检索与混合排序。
3. 在 `IRepositoryCatalog` 和 `IRepositoryIndexStore` 上增加多仓库批量查询与选择策略。
4. 将当前 C# analyzer 从“Roslyn map + graph”进一步升级为统一中间语义模型输出。
