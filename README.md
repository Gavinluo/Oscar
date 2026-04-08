# CodeBrain

CodeBrain 是一个基于 .NET 8 的本地仓库智能分析与闭环工程助手。当前版本已经支持持久化索引、混合检索、上下文组装、修改规划、Git 感知的增量分析，以及面向测试验证的闭环工作流。

架构说明见：[ARCHITECTURE.md](/D:/code/CodeBase/agentic_test_loop/ARCHITECTURE.md)

## 当前能力

- 基于 SQLite 的本地仓库注册。
- 持久化仓库索引，包含清单、图快照、检索文档和本地稠密向量。
- 带 Git 感知能力的增量分析，并在 Git 不可用时回退到文件系统模式。
- 两种分析后端：
  - `csharp-roslyn`：用于 C# 解决方案与项目。
  - `text-structure`：用于非 C# 本地仓库的结构化文本分析。
- 混合检索能力：
  - 类 BM25 的词法评分；
  - 本地向量评分；
  - 图扩展与重排。
- 上下文组装结果包含：
  - 证据命中；
  - 候选符号；
  - 候选文件；
  - 图邻居；
  - 验证目标。
- 修改规划能力，可将检索上下文转成受控的修改计划。
- 闭环工作流覆盖：
  - 仓库理解；
  - 深入分析；
  - 变更范围规划；
  - 修改规划；
  - 测试规划；
  - 测试脚手架生成；
  - 执行；
  - 覆盖率验证；
  - 从草稿记忆提升为稳定记忆。

## 项目结构

- `src/CodeBrain.Core`
  共享契约、仓库模型、上下文/编辑模型以及流水线状态。
- `src/CodeBrain.Storage`
  SQLite 目录、持久化索引存储、Git 感知增量规划与混合检索支撑。
- `src/CodeBrain.Analysis.CSharp`
  面向 C# 仓库的 Roslyn 分析器。
- `src/CodeBrain.Analysis.Text`
  面向非 C# 仓库的轻量结构化分析器。
- `src/CodeBrain.Workflows`
  闭环代理与编排层。
- `src/CodeBrain.Execution`
  测试执行与覆盖率采集。
- `src/CodeBrain.Cli`
  CLI 入口。
- `src/CodeBrain.Api`
  本地 HTTP API 与浏览器界面。
- `tests/CodeBrain.Tests`
  存储、索引与检索行为的单元测试。

## 构建

```bash
dotnet build CodeBrain.sln
dotnet test CodeBrain.sln --no-build
```

## 注册仓库

```bash
dotnet run --project src/CodeBrain.Cli -- repos add --path <本地仓库路径>
dotnet run --project src/CodeBrain.Cli -- repos list
```

仓库注册会自动检测主语言，并选择默认分析后端。

## 构建或刷新索引

```bash
dotnet run --project src/CodeBrain.Cli -- index --repo <仓库ID>
```

索引行为：

- 如果存在旧清单，则先加载旧清单。
- 当目标仓库是 Git 工作区时，优先使用 Git 变更信息。
- 当 Git 不可用时，回退到文件系统指纹。
- 持久化内容包括：
  - 仓库清单；
  - 图快照；
  - 检索文档；
  - 本地稠密向量。

期望的增量行为：

- 升级旧索引后的第一次运行，若扫描器新增了跟踪文件类型，可能会出现一次性新增。
- 对未变化仓库的第二次运行，通常应报告：
  - `added = 0`
  - `modified = 0`
  - `removed = 0`

## 运行混合检索

```bash
dotnet run --project src/CodeBrain.Cli -- query --repo <仓库ID> --q "支付确认逻辑在哪里处理？" --intent codeqa --graph-depth 2
dotnet run --project src/CodeBrain.Cli -- query --repo <仓库ID> --q "FindAsync" --intent symbol
dotnet run --project src/CodeBrain.Cli -- query --repo <仓库ID> --q "如果 token 计费规则变化，会影响哪些地方？" --intent impact --graph-depth 3
```

支持的意图：

- `codeqa`
- `symbol`
- `impact`
- `test`
- `bug`

查询结果现在包含：

- 排名后的命中项；
- 评分拆解（`bm25`、`vector`、`graph`）；
- 组装后的仓库上下文；
- 建议的修改计划。

## 初始化闭环工作流

```bash
dotnet run --project src/CodeBrain.Cli -- init --sln <解决方案路径>
```

初始化行为：

- 尽量复用已有 NUnit 测试项目。
- 必要时创建 `RepoGeneratedTests`。
- 写入 `codebrain.config.json`。
- 准备 `agent_artifacts` 目录。

## 运行闭环工作流

```bash
dotnet run --project src/CodeBrain.Cli -- run \
  --sln <解决方案或 csproj 路径> \
  --target <目标符号> \
  --topk 5 \
  --depth 3 \
  --coverage-line 0.6 \
  --coverage-branch 0.4 \
  --iterations 10
```

闭环工作流阶段：

1. 仓库映射。
2. 理解卡片生成。
3. 深入依赖分析。
4. 变更范围规划。
5. 修改计划生成。
6. 测试计划生成。
7. 测试脚手架生成。
8. 测试执行。
9. 覆盖率评估。
10. 达标后进行记忆提升。

当前生成的工作流产物包括：

- `*.changescope.json`
- `*.editplan.json`
- `*.testplan.json`
- 自动生成的测试脚手架
- 覆盖率与运行报告

## API 与 UI

```bash
dotnet run --project src/CodeBrain.Cli -- serve --port 5088
```

重要接口：

- `GET /api/repos`
- `POST /api/repos`
- `POST /api/index/{repositoryId}`
- `POST /api/query`
- `GET /api/graph/summary?repositoryId=<id>`
- `GET /api/graph/context?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/graph/impact?repositoryId=<id>&symbol=<full-symbol>`
- `GET /api/source/snippet?repositoryId=<id>&symbol=<full-symbol>`

当前浏览器工作台支持：

- 选择仓库并重新建立索引；
- 直接用自然语言提问仓库问题；
- 以评分拆解形式展示混合答案；
- 展示建议修改计划；
- 证据卡片浏览；
- 源码片段预览；
- 符号上下文与影响范围探索。

## 工作区解析规则

- `repos add`、`repos list`、`index`、`query` 和 `serve` 总是通过定位 `CodeBrain.sln` 来解析 CodeBrain 工作区。
- `init` 和 `run` 则通过显式传入的 `--sln` 或 `--project` 路径解析目标仓库工作区。
- 这样既能保持本地目录与索引数据库稳定，又能保证生成的测试项目和产物写入目标仓库。

## 推荐验证流程

```bash
dotnet build CodeBrain.sln
dotnet run --no-build --project .\src\CodeBrain.Cli -- repos add --path <repo-path>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- query --repo <repo-id> --q "target method" --intent symbol
dotnet run --no-build --project .\src\CodeBrain.Cli -- drill --sln <path-to-sln> --symbol "<symbol-from-query>"
```

第一次成功构建后，建议后续使用 `--no-build`，以跳过 `dotnet run` 每次启动前默认执行的 restore/build 检查。
