# P5：Docker Compose 部署与运维验收

2026-10-24，状态：**首批本机 Docker Compose 部署、真实样本、恢复和运维保护已实现并验证；P5 整阶段尚未验收。** 部署目标由用户选择为自托管 Docker Compose。远程主机、HTTPS、持续备份、生产容量和实际人员反馈仍需独立验收。

## 设计原则

保留 Nuxt / ASP.NET Core / Aspire 与既有整洁架构。Compose 从 AppHost 生成，AppHost 本身不作为部署服务。复用 PostgreSQL、Hangfire、OpenTelemetry、ASP.NET Core 限流和 Docker 资源约束，不增加任务调度器或自研刻谱器。固定外部工具版本与下载摘要。

开发运行环境与部署使用独立数据库卷、谱面卷和端口。部署包不包含用户本机 `.tools/`、`output/`、`tmp/` 和访问凭据。原图、保存修订与能力令牌校验仍按现有规则处理。

## 实施顺序和验收

1. **部署资源**：生成 Compose，Linux 识谱 / PDF 工具链、Nuxt Node 生产服务、共享持久谱面卷、数据库卷；仅 Web 对外开放。先检查生成文件，再构建和启动专用验证部署。
2. **运行保护**：上传与解压限制、写入限流、任务并发/超时、日志与低基数业务指标；拒绝越权、异常区间和路径绕过。
3. **恢复与备份**：在专用任务验证 Worker 重启恢复；停写后成对备份数据库和谱面卷，校验摘要，恢复到新的空白隔离目标并比较成功产物。绝不覆盖现有开发数据库。
4. **容量与清理**：记录请求、识谱/导出耗时、工具进程内存及失败；在有界并发下测量，给出本机结果，不据单样本承诺吞吐。过期数据清理须有预览和明确应用入口，默认不自动删除用户任务。
5. **使用验收**：提供初学者/教师检查表、打印样例与已知识谱问题；实际人员反馈、远程主机部署、HTTPS/域名和生产容量分别留待真实验收。

## 验证记录

以下均为本机 Docker Desktop 的 Linux amd64 结果；P4 音高/节奏/八度线的已知限制仍保留。

## 部署资源与入口

Compose 从 `MusicRecognition.AppHost/AppHost.cs` 生成，部署说明见 [deploy/README.md](../deploy/README.md)。已执行 `aspire deploy --environment p5`，项目名为 `aspire-compose-5bf6d3bd`。

| 资源 | 运行方式与约束 |
| --- | --- |
| Web | Nuxt Node 生产服务，`http://127.0.0.1:3080`，1 CPU / 512 MiB |
| API | 仅 Compose 内网 8080，UID 1654，2 CPU / 1 GiB |
| Worker | 仅内网 8080，UID 1654，2 CPU / 4 GiB，默认 1 个 Hangfire 工作线程 |
| PostgreSQL | 仅内网 5432，1 CPU / 512 MiB，独立数据库卷 |
| Aspire Dashboard | `http://127.0.0.1:18888`，1 CPU / 512 MiB，浏览器令牌和读取 API key；镜像按实际 SHA256 固定 |

谱面卷由 API / Worker 共享，开发卷和部署卷独立。根文件系统只读，应用不以 root 运行，丢弃 capabilities、禁止提权并限制日志轮转；私有 `/tmp` 和缓存挂载支持 JNA / JNI 需要的可执行映射。任务按目录、不可变修订和能力凭据隔离，共用 Worker 容器，未提供每任务独立 OS 沙箱。

工具使用固定 Audiveris 5.11.0 与 OpenCV 4.13.0.92；官方 Linux 发布包摘要见 `deploy/toolchain.lock.json`。Linux 库安装在 `/opt/recognition/.tools`，不依赖 Windows 路径。Worker 启动检查会验证 PDF 栅格化、字体、Java、适配器和 OpenCV。运行期间不下载工具。

首次迁移暴露的 OpenCV 缺失、Qt 显示依赖、临时挂载遮挡安装路径和 JNA 映射限制均已修复，随后重新执行真实样本验收。服务端实际镜像 ID、UID、只读设置、端口和资源限制记录于 [运行快照](../output/p5-validation/runtime-report.json)。

开发环境也已恢复运行，入口仍为 `http://localhost:3000`；本机 Aspire 开发仪表板与 Compose 仪表板独立。

## 真实 PDF、恢复与容量数据

原谱样本：仓库公开样本 `MusicRecognition.Web/public/samples/minuet.pdf`。专用任务 `e3800e4e-cd5a-4b0d-a977-675ec3f82d75`，保存修订 v1。

| 检查 | 实际结果 |
| --- | --- |
| 上传至导出，含一次 Worker 中断恢复 | 22.84 秒；该值包含排队、停服/启动与导出，不是纯识谱耗时或 SLA |
| Worker 恢复 | 处理中正常停止，任务持久状态回到 Queued；启动后第 2 次执行成功；过期重复执行跳过 |
| 标注 | 201 个已识别符头，201 个放置成功；198 个在下方，3 个在上方；黑色、自适应字号 |
| 原谱保护 | 复用独立 Poppler / C# 标签验收；标注区外变化 0 像素、原深色墨迹变化 0 像素、重叠标注对 0 |
| 字形 / 坐标 | 所有导出标签与权威业务 NoteId、音高标签匹配；最大文本基线误差约 0.000048 PDF 点 |
| 有界读取 | 80 次真实 HTTP 请求、并发 4；P50 5.89 ms，P95 27.16 ms，均成功 |
| 权限与产物隔离 | 错误凭据 404，非公开诊断产物 404 |
| 幂等 | 重放同一上传请求得到同一任务 ID |
| 上传限制 | 超过 21 MiB 请求包提前返回 413，业务文件仍限制 20 MiB |
| 写入预算 | 实际触发 429；被限流后任务读取仍返回 200；返回 Retry-After |

Web 流式转发上传，Kestrel / multipart 均限制请求包，图片最多 2000 万像素，PDF 最多 30 页。MusicXML 解压限制包括最多 1000 个条目、单条目 32 MiB、总计 64 MiB，并增加压缩膨胀回归测试。失败/重复上传会清理尚未发布的原图，清理接口拒绝已含修订的目录。

写入预算为单 API 实例全局默认 20 次/分钟、并发 4，普通任务查询和下载不占此预算。`Operations:WritesPerMinute`、`Operations:ConcurrentWrites`、`Recognition:WorkerCount` 和 `Recognition:MinimumFreeSpaceMiB` 可配置。识谱/导出单次超时 900 秒，磁盘低于默认 512 MiB 时 readiness 失败。磁盘健康状态须由运维监控处理，不代表数据会自动清理。

证据：[HTTP 与恢复结果](../output/p5-validation/live-report.json)、[标注 PDF](../output/p5-validation/Minuet-annotated.pdf)、[像素检查](../output/p5-validation/job/revisions/1/verification.json)、[谱面图](../output/p5-validation/job/revisions/1/annotated-render.png)。已查看渲染图，未建立全页人工音高真值。

这些数据覆盖单 Worker 和少量本机样本；读取延迟不等于 OMR 吞吐，不推断高并发生产容量。

本机 Edge 还通过部署 Web 的真实页面打开《小步舞曲》，完成 202 上传、生成 SDK 读取 Ready 修订和 PDF.js 画布渲染，未捕获 pageerror。保存 [桌面截图](../output/p5-validation/workbench-desktop.png) 与 [390 px 视口截图](../output/p5-validation/workbench-mobile.png)，390 px 视口的文档宽度也为 390 px；此检查不替代真实 iOS 设备或人员使用验收。

## 备份、还原与过期清理

新增入口位于 [scripts/ops](../scripts/ops/README.md)，均需明确 Compose 文件、环境文件和项目名。备份会短暂停止指定部署的 Web / API / Worker，以同一停写窗口保存 PostgreSQL custom dump 与谱面卷 tar，并在 `finally` 恢复原本运行的服务。

- 已生成 `output/p5-backups/20261024-verified`，两份数据均通过 SHA256 检查；没有覆盖开发数据库。
- 最终还原到新的 `musicrecognition-restore-p5-final20261024`，先恢复空白数据库与谱面卷，再单独启动还原 API。
- 原任务能力凭据和 Ready / v1 状态恢复，下载 PDF 的 SHA256 与原导出一致：`b2136127874a660ade32edc1d3bb59ac5e76549bc700cf52ef60cb9c936f628b`。
- 对已有目标再次运行恢复被拒绝。演练 API / PostgreSQL 已停止，卷保留供复核；未删除原部署、开发卷或用户任务。
- 清理默认仅预览，按整份文档及其子页检查年龄和所有任务/修订状态。专用 40 天前的夹具预览为 1 个，创建完整备份后应用清理，仅删除该夹具，候选归零；当前真实样本仍可读取。

证据：[还原报告](../output/p5-validation/restore-report.json)、[还原后 PDF](../output/p5-validation/restored-Minuet.pdf)、[清理演练](../output/p5-validation/retention-report.json)。备份是本机快照，不等于已有独立备份介质、定时保留策略或经过故障场景验证的 RPO / RTO。

## 可观测性

复用 OpenTelemetry 和受保护的 Aspire Dashboard。已用 API key 通过 CLI 读取部署的中文结构化日志与真实 `score.prepare`、`score.recognize`、`score.render` 等追踪，包含成功、中断及耗时。指标为 `musicrecognition.tool.operations`、`musicrecognition.tool.duration` 和 `musicrecognition.tool.root_process.peak_memory`，仅使用 operation / outcome 等低基数维度，任务 ID、令牌和文件名不作为指标标签。

root_process 指标仅指 Python 主进程，不能当成 Java 子进程或整个容器的峰值；上述 664 MiB 来自 Docker cgroup。自定义指标发出由 MeterListener 测试覆盖；本轮 CLI 检查覆盖日志/追踪，未导出仪表板的指标图表或配置持续告警。

证据：[日志](../output/p5-validation/telemetry-logs.json)、[追踪](../output/p5-validation/telemetry-traces.json)。部署密码和仪表板密钥仅位于忽略的 `.env.p5` / 本机配置，不复制到报告。

## 自动检查与复跑

`tests/run.ps1` 通过：63 项 Python、40 项领域、11 项基础设施非集成测试；18 个 OpenAPI 操作契约、SDK 生成可复现、布局规则、TypeScript 与 ESLint 均通过。OpenAPI / SDK 已补充 429。Nuxt 生产构建通过。API 依赖的 NuGet 审计未报告已知漏洞。

前端 `npm audit` 仍为 **7 high + 1 low**：高危链涉及 node-forge / listhen / Nuxt，低危项涉及 serialize-javascript。审计建议包含将 Nuxt 降为 4.1.3 的不兼容调整，未使用 `audit fix --force` 强行通过。容器 OS 全量漏洞扫描、远程部署与生产发布门禁尚未完成。

```powershell
pwsh -File tests/run.ps1
.tools/poc-python/Scripts/python.exe tests/e2e/validate-p5.py --url http://127.0.0.1:3080 --source MusicRecognition.Web/public/samples/minuet.pdf --compose output/p5-compose/docker-compose.yaml --env output/p5-compose/.env.p5 --project aspire-compose-5bf6d3bd --output output/p5-validation --access tmp/p5-access.json
pwsh -File tests/acceptance/validate-p5-retention.ps1 -ComposeFile output/p5-compose/docker-compose.yaml -EnvFile output/p5-compose/.env.p5 -Project aspire-compose-5bf6d3bd -BackupDestination output/p5-backups/<新的备份目录> -Output output/p5-validation/retention-report.json
```

演练会创建专用任务、重启专用 Worker、短暂停写及清理专用夹具，应串行执行，不要在备份停写窗口同时跑 HTTP 容量测试。示例项目名为本次生成值，其他主机须以自身 `docker compose ls` 结果为准。`output/` 和 `tmp/` 均为忽略的可再生/私人数据，不自动提交。

## 仍待验收

1. 远程主机的 TLS / 域名、管理员访问控制、主机和对象存储故障、离机备份及持续保留策略。
2. 前端已知漏洞、容器 OS 扫描与生产发布门禁。
3. 六页及更大质量集的持续容量、OOM / 磁盘耗尽、硬中断恢复与恢复时间目标；本轮覆盖正常 Worker 停止恢复，未模拟宿主机突然断电。
4. 持续告警、长期遥测保留、多个 API / Worker 副本的共享限额与存储验证。
5. [初学者与教师使用验收](p5-user-acceptance.md)、实际 iOS / Safari 和打印反馈。尚无实际参与者签收，P5 整阶段保持未验收。
