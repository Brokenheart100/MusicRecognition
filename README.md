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
- [P4 重新排版与质量扩展](docs/p4-reflow.md)
- [P5 Docker Compose 部署与运维](docs/p5-operations.md)
- [P5 初学者与教师使用验收](docs/p5-user-acceptance.md)
- [依赖版本集中管理](docs/dependency-management.md)
- [TickerQ / Hangfire 评估](docs/scheduler-evaluation.md)
- [样谱出处](samples/README.md)
- [一键复跑说明](tests/acceptance/README.md)
- [环境准备与运行工具](scripts/README.md)
- [测试与验收入口](tests/README.md)
- [脚本目录整理与验证](docs/repository-layout.md)
- [下方优先标注与真实谱验证](docs/below-first-layout.md)
- [自适应字号与密集和弦验证](docs/adaptive-font-sizing.md)
- [工作台快捷键](docs/keyboard-shortcuts.md)
- [图片片段识谱与 MusicXML 拼谱方案](docs/fragment-score-composition.md)
- [图片片段拼谱实现与 Orange 真实验证](docs/fragment-score-composition-validation.md)
- [Ninelie 页眉八度误识别修复与验证](docs/octave-header-fix.md)

当前阶段：P3 多页工作台已接入生成的 hey-api SDK / 类型 / Zod 校验，TanStack Query 管理任务轮询与服务端状态，Pinia 管理本机草稿及撤销历史。同一六页真实 PDF 已重新跑通上传、逐页打开校对、保存修订、整份黑色标注合并与浏览器下载，独立验证保留原谱内容与页序。仍有漏识别、节奏声部不确定及待人工放置标注；全曲人工音高真值、真实 iOS / Safari 与生产发布尚未验收，依赖审计剩余问题见 P3 报告。

P4 已启动，新增受限的「重排辅助谱」。简单参考谱完成保存修订、ID 映射、间距切换及辅助标注；复杂样本的 Verovio 导入覆盖不足，下载被准入检查阻止。整阶段尚未通过，详见 P4 报告与质量集约定。

图片片段拼谱首版已可从侧栏或 `/fragments` 使用：批量上传、排序、裁剪、完整小节范围、历史版本、MusicXML / MXL 与重排 PDF。两张 Orange 截图本地跑通 11 小节、193 个已识别音符与 2 页 PDF；节奏与八度线仍需校对，当前提供记谱软件继续编辑的草稿，详见专项验证报告。

P5 首批已完成本机 Docker Compose 部署、Linux 工具链、持久卷、限流、Worker 正常中断恢复、成对备份/隔离还原与过期清理演练。部署 Web 默认 `http://127.0.0.1:3080`，开发入口仍为 `http://localhost:3000`。P5 整阶段尚未验收：前端已知漏洞、远程主机与 HTTPS、生产容量、持续备份告警和实际使用反馈仍待完成。
