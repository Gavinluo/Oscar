# 验证说明（2026-03）

本文档记录了 2026 年 3 月修复后，已经验证过的 CLI 行为变化。

## 工作区解析

- `repos add`、`repos list`、`index`、`query` 和 `serve` 总是通过定位 `CodeBrain.sln` 来解析 CodeBrain 工作区。
- 因此，即使命令从不同的 shell 工作目录启动，仓库目录和持久化索引数据库仍会落在同一个 `agent_artifacts/index` 目录下。
- `init` 和 `run` 则通过显式传入的 `--sln` 或 `--project` 路径解析目标仓库工作区。
- 生成的 `codebrain.config.json`、`agent_artifacts` 以及测试项目发现逻辑，现在都绑定到目标仓库，而不再依赖当前 shell 目录。

## 增量索引

- 增量索引现在为变更规划和清单持久化共用同一个文件扫描器。
- 扫描器会跟踪 `.cs`、`.csproj`、`.sln`、`.json` 和 `.md` 文件，同时跳过 `bin`、`obj`、`.git` 和 `.vs`。
- 升级扫描器后的第一次重建，可能会出现一次性新增，因为旧清单并未覆盖所有当前跟踪的文件类型。
- 从第二次重建开始，如果仓库没有变化，通常应报告 `added=0`、`modified=0`、`removed=0`。

## Drilldown 符号解析

- `drill` 支持直接接收 `query --intent symbol` 返回的精确符号。
- `drill` 现在也能容忍一些常见格式差异，包括：
  - `global::` 前缀差异；
  - 命名空间分隔差异，例如 `OpenAi.ManagedApis` 与 `OpenAiManagedApis`；
  - 参数命名空间格式差异，例如 `Domain.Models` 与 `DomainModels`。

## CLI 验证建议

- 先运行一次 `dotnet build CodeBrain.sln`。
- 后续重复验证时，优先使用 `dotnet run --no-build --project ...`，避免每次启动前重复执行 restore/build 检查。
- 对已注册仓库，一次最小验证流程如下：

```bash
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- query --repo <repo-id> --q CalculateAccountingFromTokens --intent symbol
dotnet run --no-build --project .\src\CodeBrain.Cli -- drill --sln <path-to-sln> --symbol "<symbol-from-query>"
```
