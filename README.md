# CodeBrain

架构图文档：[ARCHITECTURE.md](/D:/code/CodeBase/agentic_test_loop/ARCHITECTURE.md)

`CodeBrain` 是一个基于 .NET 8 的本地仓库智能平台。它把仓库注册、增量索引、检索问答和测试闭环放到同一套本地工作流里，当前默认使用 C# / Roslyn 分析器。

## 当前能力

- 本地仓库注册与目录编目
- 基于 SQLite 的持久化索引加载
- 文件级增量索引规划
- 基于 Roslyn 的 C# 仓库扫描、符号解析与调用图分析
- 面向不同问题类型的仓库检索
- 理解卡片 `draft` / `stable` 双层记忆
- NUnit 测试计划、测试生成、执行与覆盖率闭环

## 项目结构

- `src/CodeBrain.Cli`：`codebrain` 命令行入口
- `src/CodeBrain.Core`：统一模型、接口与工作流契约
- `src/CodeBrain.Analysis.CSharp`：C# / Roslyn 分析器
- `src/CodeBrain.Storage`：SQLite 仓库目录、索引存储、增量规划与产物存储
- `src/CodeBrain.Execution`：测试执行与覆盖率采集
- `src/CodeBrain.Workflows`：测试闭环工作流
- `src/CodeBrain.Api`：本地 API 与浏览器界面
- `tests/CodeBrain.Tests`：测试

## 索引与存储

CodeBrain 当前把本地数据分成两层：

- `agent_artifacts/*`
  - 运行日志、理解卡片、测试报告、覆盖率报告
- `agent_artifacts/index/*.db`
  - `codebrain.catalog.db`：本地仓库注册目录
  - `codebrain.index.db`：持久化索引、图谱快照、检索文档
  - `knowledge.db`：现有图谱/卡片查询存储，继续为测试闭环服务

持久化索引的目标是让查询路径不再依赖每次全量重建。首次索引后，后续查询会优先加载 SQLite 中已保存的索引；重新索引时只对新增、修改、删除的文件做差异比对。

## 构建

```bash
dotnet build CodeBrain.sln
```

## 初始化测试闭环

```bash
dotnet run --project src/CodeBrain.Cli -- init --sln <path-to-sln>
```

`codebrain init` 会：

- 发现并优先复用已有 NUnit 测试项目
- 在缺失时生成 `RepoGeneratedTests`
- 安装测试与覆盖率依赖
- 生成 `codebrain.config.json`
- 初始化 `agent_artifacts/*`

## 注册本地仓库

```bash
dotnet run --project src/CodeBrain.Cli -- repos add --path <local-repository-path>
dotnet run --project src/CodeBrain.Cli -- repos list
```

当前只支持 `LocalPath`。`GitUrl` 和 `ZipFile` 暂不内置，因为实际执行和索引仍然基于本地目录。

## 构建或刷新索引

```bash
dotnet run --project src/CodeBrain.Cli -- index --repo <repository-id>
```

索引过程会：

- 从仓库目录中扫描 C# 相关文件
- 对比上次 `manifest`，计算新增、修改、删除、未变文件
- 生成新的图谱快照与检索文档
- 持久化到 SQLite

## 进行仓库检索

```bash
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "这个类负责什么？"
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "FindAsync" --intent symbol
dotnet run --project src/CodeBrain.Cli -- query --repo <repository-id> --q "修改这个接口会影响哪些调用方？" --intent impact
```

当前支持的检索意图：

- `codeqa`
- `symbol`
- `impact`
- `test`
- `bug`

## 运行测试闭环

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

可选 LLM：

- `--llm qwen`
- `--llm openai`
- `--llm dummy`

## 查询 API 与界面

```bash
dotnet run --project src/CodeBrain.Cli -- serve --port 5088
```

启动后可访问：

- `http://localhost:5088/`
- `GET /api/repos`
- `POST /api/repos`
- `POST /api/index/{repositoryId}`
- `POST /api/query`
- `GET /api/graph/summary?repositoryId=<id>`
- `GET /api/graph/nodes?repositoryId=<id>&term=<keyword>`
- `GET /api/graph/context?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/graph/impact?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/cards?symbol=<full-symbol>`

## 默认模型配置

默认运行方式仍然是千问兼容 OpenAI 接口：

- 默认 Provider：`qwen`
- 默认模型：`qwen3-max-2026-01-23`
- 默认 Base URL：`https://dashscope.aliyuncs.com/compatible-mode/v1`

优先读取的环境变量：

- `DASHSCOPE_API_KEY`
- `QWEN_MODEL`
- `QWEN_BASE_URL`

兼容读取的环境变量：

- `QWEN_API_KEY`
- `OPENAI_API_KEY`
- `OPENAI_MODEL`
- `OPENAI_BASE_URL`

## 当前边界

当前版本已经完成：

- 项目品牌从 `AgenticTestLoop` 切换为 `CodeBrain`
- 本地仓库注册、持久化索引、文件级增量规划
- 面向仓库检索的 CLI/API 主路径

当前仍未完成：

- 多语言分析 backend
- 向量检索落地
- 多仓库融合排序
- 非 C# backend 的统一 analyzer 实现
