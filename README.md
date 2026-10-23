# MusicRecognition

面向钢琴初学者的五线谱简谱音高辅助标注。Nuxt 4 前端、ASP.NET Core 后端、Aspire 编排，采用整洁架构。

- [设计方案](docs/design.md)
- [分阶段实施与验收](docs/implementation-plan.md)
- [P0 可行性报告](docs/poc-report.md)
- [P1 后端设计与复跑](docs/p1-backend.md)
- [P1 验证报告](docs/p1-report.md)
- [P2 工作台设计与验证](docs/p2-workbench.md)
- [P3 多页识别与导出](docs/p3-multipage.md)
- [P3 OpenAPI、SDK 与 Query 验证](docs/p3-api-query.md)
- [Aspire AI 代理支持与运行检查](docs/aspire-agent-support.md)
- [Python 修复与 OpenCV A/B 验证](docs/opencv-validation.md)
- [Poppler 路径、启动依赖检查与两页真实谱验证](docs/poppler-runtime-validation.md)
- [音高一致性失败修复与三页真实谱验证](docs/pitch-consistency-fix.md)
- [MusicXML 网页渲染试用](docs/musicxml-preview.md)
- [依赖版本集中管理](docs/dependency-management.md)
- [TickerQ / Hangfire 评估](docs/scheduler-evaluation.md)
- [样谱出处](samples/README.md)
- [一键复跑说明](scripts/poc/README.md)

当前阶段：P3 多页工作台已接入生成的 hey-api SDK / 类型 / Zod 校验，TanStack Query 管理任务轮询与服务端状态，Pinia 管理本机草稿及撤销历史。同一六页真实 PDF 已重新跑通上传、逐页打开校对、保存修订、整份黑色标注合并与浏览器下载，独立验证保留原谱内容与页序。仍有漏识别、节奏声部不确定及待人工放置标注；全曲人工音高真值、真实 iOS / Safari 与生产发布尚未验收，依赖审计剩余问题见 P3 报告。
