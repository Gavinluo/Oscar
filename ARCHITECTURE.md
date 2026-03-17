# Agentic Test Loop 架构说明

## 概览

当前仓库可以分为两个协作层：

- `webapi`：被分析、被理解、被测试的业务解决方案
- `agentic_test_loop`：负责扫描代码、生成理解、写测试、执行验证、沉淀记忆的 Agent 框架

## 总体架构

```mermaid
flowchart LR
    U["用户 / CLI"] --> CLI["AgenticTestLoop.Cli\natl init/run/map/drill"]
    CLI --> ORCH["LoopOrchestrator"]
    ORCH --> AGENTS["Agent 层\nRepoMapper / Understander / Drilldown / TestPlanner / TestWriter / Runner / Coverage / Memory"]

    AGENTS --> CORE["AgenticTestLoop.Core\n契约 / 模型 / PipelineState / ILLMProvider"]
    AGENTS --> ROSLYN["AgenticTestLoop.Roslyn\nSolution 加载 / Symbol 解析 / 调用图 / 下钻"]
    AGENTS --> EXEC["AgenticTestLoop.Execution\ndotnet test / coverage / reportgenerator"]
    AGENTS --> STORE["AgenticTestLoop.Storage\nunderstanding / logs / reports / plans"]

    ROSLYN --> TARGET["webapi / QuantumCorp.ApiGates.sln\n业务源码"]
    EXEC --> TESTPROJ["NUnit 测试工程"]
    STORE --> ART["agent_artifacts\nunderstanding / reports / logs / plans"]

    LLM["LLM Provider\nDummy / OpenAI 兼容 / Qwen 兼容"] --> AGENTS
```

## `agentic_test_loop` 内部项目依赖图

```mermaid
flowchart TD
    CORE["AgenticTestLoop.Core"]
    ROSLYN["AgenticTestLoop.Roslyn"]
    EXEC["AgenticTestLoop.Execution"]
    STORE["AgenticTestLoop.Storage"]
    AGENTS["AgenticTestLoop.Agents"]
    CLI["AgenticTestLoop.Cli"]
    TESTS["AgenticTestLoop.Tests"]

    ROSLYN --> CORE
    EXEC --> CORE
    STORE --> CORE
    AGENTS --> CORE
    AGENTS --> ROSLYN
    AGENTS --> EXEC
    AGENTS --> STORE
    CLI --> CORE
    CLI --> ROSLYN
    CLI --> EXEC
    CLI --> STORE
    CLI --> AGENTS
    TESTS --> CORE
    TESTS --> ROSLYN
    TESTS --> EXEC
    TESTS --> STORE
    TESTS --> AGENTS
```

## 闭环执行流程

```mermaid
flowchart LR
    A["atl run"] --> B["RepoMapper\n建立 solution / symbol / 调用图"]
    B --> C["Understander\n生成 Understanding Card"]
    C --> D["DrilldownNavigator\n执行 Top-K / Depth 下钻"]
    D --> E["TestPlanner\n生成测试矩阵"]
    E --> F["TestWriter\n生成 NUnit 测试代码"]
    F --> G["Runner\ndotnet test"]
    G --> H["Coverage\ncoverlet + reportgenerator"]
    H --> I{"达到阈值?"}
    I -- "否" --> C
    I -- "是" --> J["Memory\n将 draft 升级为 stable"]
```

## 当前边界划分

- `agentic_test_loop`：平台层
- `webapi`：业务目标层
- `agent_artifacts`：执行产物层
