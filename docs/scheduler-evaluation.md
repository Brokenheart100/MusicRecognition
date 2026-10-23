# TickerQ 与 Hangfire：当前项目的选择

核对日期：2026-10-22。结论：TickerQ 的编译期注册和 EF Core 集成具有吸引力，不能据此认定它在本项目全面优于 Hangfire。本次保留已验证的 Hangfire，完成依赖集中管理；没有安装或迁移 TickerQ。

## 结合识谱任务判断

| 维度 | TickerQ | 当前 Hangfire 实现 |
| --- | --- | --- |
| 注册与调用 | 源码生成、编译期函数注册，适合关注 Core AOT 的场景 | 已使用方法表达式和 DI；本项目没有 Native AOT 验收要求 |
| PostgreSQL | 有维护者提供的 EF Core 包 | 使用社区 Hangfire.PostgreSql provider，P1 已执行真实数据库恢复试验 |
| 持久任务与取消 | 文档提供持久化、取消、重试和多节点说明 | 已实测 API 重启保留任务、Worker 正常停止恢复、运行中取消、显式重试 |
| 性能决策 | 调度开销优势需要使用同版本、同数据库和实际负载测量 | 当前耗时主要在 Python / Java 的 Audiveris 识别，不应把调用微基准当作端到端吞吐 |

前两项依据：[TickerQ 10.4.0 包说明](https://www.nuget.org/packages/TickerQ/10.4.0)、[TickerQ EF Core 文档](https://tickerq.net/docs/entity-framework)。当前项目实测见 [P1 报告](p1-report.md)。Hangfire 的取消与正常停止重排队行为也有[官方说明](https://docs.hangfire.io/en/latest/background-methods/using-cancellation-tokens.html)。

“主要耗时来自识谱”是基于当前处理链路和 P1 日志的工程判断；尚未执行两个调度器的本项目对照基准。换调度器也不能自动解决原谱识别准确率、PDF 坐标或标注碰撞。

## 必须按版本区分的资料

本次 NuGet 页面展示的稳定 TickerQ 为 **10.4.0**，包声明 MIT OR Apache-2.0。官网同时提供另一条 **5.x 功能版本线**的商业、源码可见文档，对 .NET 10 对应 **10.5.x**；主仓库授权说明将商业过渡起点标为 10.5.0。不能把主分支的新授权套用到所有历史包，也不能把旧开源版的介绍当作未来版本承诺。[已发布包](https://www.nuget.org/packages/TickerQ/10.4.0)、[版本线说明](https://tickerq.net/docs/5.x)、[维护者授权说明](https://github.com/Arcenox-co/TickerQ/blob/main/LICENSING.md)。

例如，新功能文档中的 `StageAsync` 可以把任务与业务行放进同一 EF 事务。这可能减少当前持久派发扫描逻辑，但它位于 5.x / 10.5.x 文档，**不能未经确认就在 10.4.0 上使用这个 API**。普通 `AddAsync` 也不能仅因共享 DbContext 就假定与业务数据原子提交。[事务调度文档](https://tickerq.net/docs/5.x/entity-framework/transactional-scheduling)。

## 若后续采用 TickerQ

优先价值应是减少应用需要维护的调度适配代码、统一 EF 持久化和可观测性，而不是单纯追求调度器微基准数字。迁移前选定确切发布版本、对应文档和包内授权，复用现有 PostgreSQL 与单 Worker 约束，避免为当前负载提前增加 Redis。

迁移验收至少重复 P1 的上传到导出、API / Worker 重启、运行中取消和重试、幂等重复执行、并发修订，并增加进程强杀后恢复、重复投递、数据库事务回滚试验。旧 Hangfire 中未完成任务需要明确排空或转移，不能只替换注册方法。Application / Domain 继续通过端口依赖基础设施。
