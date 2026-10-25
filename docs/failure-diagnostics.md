# 上传与识谱失败诊断

更新：2026-10-22。

2026-10-23：[Ninelie 页眉八度误识别修复与真实任务验证](octave-header-fix.md)。八度线范围或所属谱表无法可靠判断时，返回 `octave-evidence-ambiguous`，具体候选证据保存在 `octave-repairs.json`。

## Aspire 中查看

打开「结构化日志」，选择 `api` 或 `worker`，按任务 ID 搜索。识谱失败事件现在包含：

| 字段 | 含义 |
| --- | --- |
| `ScoreId` | 当前页任务 ID |
| `DocumentId` / `PageNumber` | 原文档和页号，位于识谱失败汇总事件 |
| `Operation` | `prepare`、`recognize`、`render` 或 `merge`，位于工具失败事件 |
| `ErrorCode` | 稳定错误分类，例如 `pitch-consistency-failed` |
| `FailureReason` | Python 诊断文件中的具体原因；文件不可用时回退到 stderr |
| `ExitCode` | 工具退出码 |
| `DiagnosticLog` | 本地完整工具日志绝对路径 |
| `StderrTail` | 最后 8 行 stderr 的摘要，最多 4000 字符 |
| `DiagnosticReportIssue` | 诊断 JSON 无法读取、格式损坏或超过 64 KiB 等情况 |

`FailureReason` 最多 4000 字符。音高校验报告保留问题小节、谱表、声部、符头 `interId`、候选音高和 MusicXML 音高；示例明细最多列出 8 个符头，并保留缺失总数。

上传阶段没有持久化任务时，在 `api` 中使用响应里的 `traceId` 搜索 `RequestTraceId`。事件同时记录 `Operation`、HTTP 状态码、错误码、具体原因；异常处理中读取原始 endpoint 和 route values，保持 `ImportScore`、`GetScore` 等操作名及已有任务 ID。

服务端具体原因与客户端提示分开。客户端不会收到 stderr、服务器目录或内部堆栈。诊断文件缺失、损坏或过大不会覆盖原始工具失败；每次运行前清理旧的 `error.json`，避免把上一次失败误认为本次原因。

## 本次真实 PDF

原文档任务：`9106e3fb-7363-4c5e-bc00-7ef1fded31c6`，3 页，上传及拆页成功。

| 原页 | 原任务 | 结果 |
| --- | --- | --- |
| 1 | `c79ae11e-5391-4c8a-8714-a7bd1d290dfc` | `pitch-consistency-failed`，MusicXML 缺少已分配节奏声部的符头 |
| 2 | `b22d5055-1454-4949-9483-db9ba1d57bde` | `pitch-consistency-failed`，第 12 小节附近的音高、声部或坐标不一致 |
| 3 | `7e8d3f2c-70ed-46a5-ad5c-a7222136b4ae` | 213 个音符，识谱、逐页标注导出成功 |

为验证新日志，分别重新上传这两份失败页的原始 PDF 字节，使用独立的验证任务，不修改原任务：

- 第 1 页验证任务 `6831308d-bf38-4f1b-86dc-8a63d7d67bb0`：缺失 15 个已分配声部符头，日志列出前 8 个，例如 `interId=2632`、`xmlMeasure=2`、E5、voice 1。
- 第 2 页验证任务 `90db209b-f49c-40fe-9030-ae8f2c800f3d`：第 12 小节，MusicXML 的 F♯5 与候选符头 F5 不一致；日志同时保留对应音高、声部及符头 ID。

这些是两份识谱输出之间的不一致，不足以证明原谱音高有误。日志增强阶段尚未修复识谱结果；后续修复与新任务验收见下节，历史失败记录保留。

## 根因与修复（2026-10-22 追加）

此前“单一调号缓存错误解释全部失败”的表述不准确，现以实际引擎堆栈和独立回放结果更正：

- 第一页：Audiveris 的渐强端点和弦没有时间偏移，`WedgeIterators` 抛出异常，导致整小节从 MusicXML 被移除；在工作副本中排除该异常力度符号，再调用官方导出器恢复小节，原谱和符头不变。
- 第二页：无效 `KEY_CANCEL` 的 `fifths=null`，MusicXML 导出器继续沿用有效调号，旧适配器却按空调号计算音高；适配器现按导出器规则维护各谱表上下文，仍保留临时升降号，并提示人工校对不完整调号。

最新三页原文档 `71dfe3d0-f306-44ba-9957-60e710bbc564` 的失败页回放通过，全新验证任务 `32f7df19-0dff-4aac-82c6-3ed971899a07` 的三页识谱、逐页导出和整份合并通过。827 个符头保留，739 个标注安全放置，88 个待人工放置；24 个节奏声部未确定，第二页 13 个符头的调号需重点校对。历史任务和历史失败日志不会自动改写。

完整根因、边界、原六页 OMR 的回归及真实负例证据见 [音高一致性修复与验证报告](pitch-consistency-fix.md)。编译适配器使用 `scripts/setup/prepare.ps1`，`pipeline.py` 不提供 `compile` 操作。前面的历史日志验证和下面的测试记录保留其原有范围。

## 验证

- 4 项新增真实 Python 子进程测试通过：有效诊断 JSON、损坏 JSON、诊断文件缺失、遗留旧报告；确认根因进入结构化字段，客户端提示不包含内部详情。
- 12 项音高及八度校验测试通过；缺失符头测试同时验证报告包含定位信息。
- API、Worker 构建通过，0 警告、0 错误，并通过 Aspire 健康检查。
- 实际 API 测试：伪装 PDF 的文件签名错误返回 HTTP 400；不存在的任务返回 HTTP 404；响应 traceId 与日志 `RequestTraceId` 可关联。
- 两个真实失败页已经在 Aspire 中产生包含具体 `FailureReason`、stderr 摘要及日志路径的事件。

复现：`tests/e2e/validate-failure-logging.py --url <API 地址> --sources <失败页 PDF> ...`。省略 `--sources` 仅检查 API 失败诊断。

证据保存在忽略目录 `output/logging-validation/`，包括 `evidence.json`、`api-errors.json` 和 `aspire-worker-errors.json`。
