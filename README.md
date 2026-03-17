# Agentic Test Loop

架构图文档：[ARCHITECTURE.md](/D:/code/CodeBase/agentic_test_loop/ARCHITECTURE.md)

`agentic_test_loop` 是一个基于 .NET 8 的多 Agent 闭环框架，主要用于：

- 基于 Roslyn 的仓库扫描、符号解析与调用图分析
- 生成结构化理解卡片，并维护 `draft` / `stable` 两级记忆
- 生成 NUnit 测试计划与测试代码
- 执行 `dotnet test` 并解析失败结果
- 基于 coverlet + reportgenerator 做覆盖率闭环
- 通过日志、计划和报告形成可追溯的迭代过程

## 默认大模型运行方式

当前默认运行方式为千问，使用 DashScope 的 OpenAI 兼容接口。

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

如果没有任何外部模型可用，仍然可以使用：

- `--llm dummy`

该模式下无需 API Key，也可以跑通完整骨架流程，只是理解和测试生成质量会低一些。

## 项目结构

- `src/AgenticTestLoop.Cli`：`atl` 命令行入口
- `src/AgenticTestLoop.Core`：核心契约、模型、流水线状态、LLM 抽象
- `src/AgenticTestLoop.Roslyn`：solution 加载、符号解析、调用图、下钻
- `src/AgenticTestLoop.Execution`：测试执行与覆盖率采集
- `src/AgenticTestLoop.Storage`：理解卡片、日志、报告、计划的持久化
- `src/AgenticTestLoop.Agents`：RepoMapper、Understander、Drilldown、TestPlanner、TestWriter、Runner、Coverage、Memory、Orchestrator
- `tests/AgenticTestLoop.Tests`：框架自身测试

产物默认写入仓库根目录下的：

- `agent_artifacts/understanding/draft`
- `agent_artifacts/understanding/stable`
- `agent_artifacts/reports/coverage`
- `agent_artifacts/reports/test_runs`
- `agent_artifacts/logs`
- `agent_artifacts/plans`

## 构建

```bash
dotnet build agentic_test_loop/AgenticTestLoop.sln
```

## 初始化

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- init --sln <path-to-sln>
```

`atl init` 会执行以下动作：

- 探测已有测试项目，并优先复用 NUnit
- 如果不存在 NUnit 测试项目，则自动创建
- 安装所需 NuGet 包
- 安装 `dotnet-reportgenerator-globaltool`
- 生成 `agentic_test_loop.config.json`
- 初始化 `agent_artifacts/*` 目录

## 运行闭环

默认方式为千问：

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- run \
  --sln <path-to-sln-or-csproj> \
  --target <symbol> \
  --topk 5 \
  --depth 3 \
  --coverage-line 0.6 \
  --coverage-branch 0.4 \
  --iterations 10
```

显式指定千问：

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- run \
  --sln <path-to-sln-or-csproj> \
  --target <symbol> \
  --llm qwen
```

无 API Key 的本地骨架模式：

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- run \
  --sln <path-to-sln-or-csproj> \
  --target <symbol> \
  --llm dummy
```

说明：

- `--target` 可重复传入多个目标
- 如果未传 `--target`，CLI 会自动选择一个候选 symbol
- `dummy` 模式能跑完整流程，但理解和测试设计质量较低
- `qwen` 模式是默认推荐方式，适合更好的理解和测试设计

## Repo Map 与 Drilldown

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- map --sln <path>
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- drill --sln <path> --symbol <symbol> --topk 5 --depth 3
```

## 查询 API 与可视化界面

启动本地查询 API 与可视化界面：

```bash
dotnet run --project agentic_test_loop/src/AgenticTestLoop.Cli -- serve --port 5088
```

启动后可访问：

- `http://localhost:5088/`：本地图谱管理界面
- `http://localhost:5088/api/graph/summary`：图谱摘要
- `http://localhost:5088/api/graph/nodes?term=<keyword>`：节点搜索
- `http://localhost:5088/api/graph/context?symbol=<full-symbol>`：符号上下文
- `http://localhost:5088/api/graph/impact?symbol=<full-symbol>`：影响分析
- `http://localhost:5088/api/graph/rebuild`：重建图谱
- `http://localhost:5088/api/cards?symbol=<full-symbol>`：理解卡片查询

当前采用双层存储：

- 文件层：`agent_artifacts/*`，用于审计和追溯
- SQLite 层：`agent_artifacts/index/knowledge.db`，用于图谱与查询索引

## 记忆升级规则

理解卡片总是先写入 `draft`。

只有满足以下条件时，才允许升级为 `stable`：

- 测试全部通过
- 覆盖率达到配置阈值
- 能够记录 `verified_by`，并关联测试与覆盖率结果

## 扩展点

- 实现 `ILLMProvider` 可增加新的大模型 Provider
- 扩展 `TestPlannerAgent` 或 `TestWriterAgent` 可自定义测试生成策略
- 实现 `ITestExecutionService` 或 `ICoverageService` 可替换执行与覆盖率模块
- 实现 `IAgent` 并接入 `LoopOrchestrator` 可增加新的 Agent

## 常见输出

- `agent_artifacts/reports/test_runs/latest.md`：最近一次运行摘要
- `agent_artifacts/reports/coverage/summary.json`：覆盖率摘要与热点
- `agent_artifacts/plans/*.testplan.json`：生成的测试矩阵
- `agent_artifacts/understanding/draft|stable/*.json`：理解卡片
