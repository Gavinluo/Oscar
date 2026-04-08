# 已完成事项

## 2026-04-08

- 完成 `CodeBrain.Api` 的仓库管理工作台重构，将旧版表单式页面切换为“左侧仓库管理、右侧对话与预览”的双栏工程工作台布局，更贴近 Figma 中的用户仓库管理界面。
- 新增 `GET /api/workspace` 聚合接口，统一返回仓库卡片所需的分支、文件数、索引状态和图摘要，避免前端自己拼接多轮请求。
- 将仓库问答结果改造成右侧会话流表达，保留证据入口、修改建议和源码/上下文/影响预览之间的联动，强化“基于仓库的对话”而不是单次查询表单。
- 重新运行 `dotnet test CodeBrain.sln --no-restore`，结果为 25/25 通过。

## 2026-04-08（早些时候）

- 完成 `CodeBrain.Storage` 的可插拔 embedding provider 抽象，新增 `IEmbeddingProvider` 和 `RepositoryEmbedding`，让索引与查询不再直接依赖内嵌本地向量实现。
- 在存储层落地默认离线 provider `LocalHashEmbeddingProvider`，继续保留无外部服务时的可运行路径，并把 embedding 模型标识写入 SQLite。
- 调整查询阶段的向量评分逻辑，只在查询 embedding 与文档 embedding 模型一致时参与向量相似度计算，为后续 provider 切换和离线回退留下稳定边界。
- 补充注入式测试验证 provider 抽象可用，并再次运行 `dotnet test CodeBrain.sln --no-restore`，结果为 25/25 通过。

## 2026-04-04

- 由于 `ToDoList.md` 为空，已根据当前仓库状态重新建立项目待办列表。
- 通过运行 `dotnet test CodeBrain.sln --no-restore` 确认当前基线，24/24 测试通过。
- 将下一阶段工作重新聚焦到当前架构已暴露出的产品缺口：embedding provider、Git diff 过滤、感知编辑意图的测试生成、搜索模块化，以及更深入的非 C# 分析路径。
- 将仓库内现有 Markdown 文档统一更新为中文，覆盖 README、架构说明、验证说明、待办、完成记录与演进记录，并修复 `evolution.md` 的乱码问题。
