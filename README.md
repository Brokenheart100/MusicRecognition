# MusicRecognition

面向钢琴初学者的五线谱简谱音高辅助标注。Nuxt 4 前端、ASP.NET Core 后端、Aspire 编排，采用整洁架构。

- [设计方案](docs/design.md)
- [分阶段实施与验收](docs/implementation-plan.md)
- [P0 可行性报告](docs/poc-report.md)
- [P1 后端设计与复跑](docs/p1-backend.md)
- [P1 验证报告](docs/p1-report.md)
- [样谱出处](samples/README.md)
- [一键复跑说明](scripts/poc/README.md)

当前阶段：P1 已实现 ASP.NET Core API、独立 Worker、PostgreSQL / EF Core 与 Hangfire 持久任务。可上传一页 PDF / 图片、读取结果、纠错保存修订并重新导出，保持黑色与扩大后的字号。真实 PDF、API 重启保留任务和 Worker 中断恢复已经本地验证。Nuxt 4 上传与校对工作台在 P2；全页识别准确率仍待人工真值验证。
