# P1 验证报告

日期：2026-10-22（北京时间）。结论：**后端最小闭环已实现并通过本地验证**；不代表生产就绪，也不代表整页音符语义已人工验收。

## 实现范围

已建立 Domain、Application、Infrastructure、Api、Worker、ServiceDefaults 与测试项目。ASP.NET Core API 支持上传、读取任务、获取音符、纠错、不可变修订、下载、取消和重试。音高 / 等音拼写 / 固定 C / 首调 / 小节转调规则在 C# Domain；Application 用例通过接口依赖 EF Core、文件和工具适配器。

Aspire 编排实际 PostgreSQL、API 和独立 Worker；数据库采用命名卷，修订快照存 PostgreSQL jsonb，文件单独保留。Hangfire + PostgreSQL provider 提供持久队列、锁与服务器恢复机制。Python / Java 适配器重用 P0 识谱和原谱叠加，没有引入新的识谱模型或 OpenCV。

## 自动检查

| 检查 | 结果与证据 |
| --- | --- |
| 全解决方案构建 | `dotnet build MusicRecognition.slnx` 最终 0 warnings / 0 errors |
| EF Core 迁移 | InitialScores 已应用；`has-pending-model-changes` 无待迁移模型差异 |
| 领域测试 | 18 / 18：固定 C、升降 / 重升、等音不同拼写、低八度边界、G 大调、A 自然小调、按小节转调、非法调性、四方向小数裁剪坐标 |
| 真实工具超时 | 1 / 1 集成测试通过；真实 Audiveris 流程 10 秒超时，返回 `processing-timeout`，未发布 raw；`output/p1/timeout/evidence.json` |
| 依赖漏洞检查 | 修正 Hangfire 引入的旧 Newtonsoft.Json，显式锁定 13.0.4；Infrastructure 含传递依赖的当前 NuGet audit 无已知漏洞提示 |
| 就绪和 OpenAPI | API、Worker、PostgreSQL Healthy；`/openapi/v1.json` 返回 6 组乐谱路径（含多方法） |

最初构建曾发现 EF Core 10.0.4 / 10.0.13 冲突，现显式统一 EF Core / Relational / Design 10.0.13，Npgsql provider 10.0.3。复验日志还发现派发器先 enqueue、Worker 已开始时更新 xmin 的冲突，已改为条件更新派发标识；处理状态不被派发器覆盖。扫描批次采用确定排序，避免 EF Take 无 OrderBy 警告。

## 真实 PDF 上传与纠错

使用 P0 同一许可来源的 `Minuet-xpose-a4.pdf`，SHA-256：`5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7`。通过真实 HTTP multipart 上传，未用预先填充的识别结果替代后台执行。

主验证 Score：`3720a69c-4e26-4341-9e64-df0cecf3ca3a`；下载证据 `output/p1/final/flow/`；服务端产物 `output/backend/3720a69c4e2643419e64df0cecf3ca3a/`。

- 201 个 HeadInter 与 201 个 MusicXML 音高多重集相符；200 个自动放置，1 个标记需要人工放置。
- 黑色 `#000000`、15 像素 / 5 pt。保留 3,858 个原页面绘图操作；PDF 文本基线最大误差约 0.00004286 pt。
- Revision 1 为识别结果；通过业务 NoteId 纠正一个音符，Revision 2 的 C# 音高和 PDF / JSON 标注随之变化，Revision 1 的音高保持原值。识别 Attempts 仍为 1。
- 独立 Poppler 渲染比较 Revision 2：10,460 个变化像素，**预留区域外变化 0、覆盖原谱深色墨迹 0、标注重叠 0、不可见标注 0**，黑色混合不一致像素 0。
- 全部导出音符的 NoteId、数字、变化音、八度点及修订音高与 C# labels 一致。已打开栅格预览检查字形与原谱位置。

导出样本：[纠错后的固定 C PDF](../output/pdf/p1-minuet-corrected-r2.pdf)。预览局部：[黑色标注](../output/p1/final/first-system.png)。这次人为改音只用于证明修订链路，不能当作乐谱校订后的教学版本。

## 持久化与恢复

扫描图也通过相同 API 重新运行：`BachInvention5.jpg`，Score `5f54393d-bef7-4cf2-9724-1902ccc2ea32`，395 个符头、393 个自动放置、2 个待人工放置，完成识别、音高纠错、不可变修订和导出。证据位于 `output/p1/final-scan/flow.json`，维持黑色和约 5.23 pt 字号。独立栅格检查：29,183 个变化像素，预留区域外变化 / 深色原墨迹覆盖 / 标注重叠均为 0，黑色混合不一致像素 0。纸张带色，抗锯齿边缘仍保留纸张色度，这是黑色透明混合的预期结果；不以边缘必须纯灰作为黑色判据。[扫描图修订 PDF](../output/pdf/p1-bach-scan-corrected-r2.pdf)。

| 场景 | 操作与结果 |
| --- | --- |
| API 重启保留排队任务 | 停 Worker → 上传 → 重启 API → 查询仍 Queued → 启 Worker → 完成识别、纠错和导出；Score `64a62a0f-c0cb-4680-800b-fc343c986131`，`output/p1/recovery.json` |
| 识谱中断恢复 | 上传 → 确认 Processing → 正常停止 Worker → 启动 Worker → 同一 Score Ready，Attempts=2；Score `cefc17c5-8d25-440d-bcbf-3dae70c3230b`，`output/p1/interrupted.json` |
| AppHost 全部停止 / 启动 | 应用更新期间数次停止本 AppHost，命名卷与文件保留；原任务、旧修订、已取消任务重启后仍可通过原凭据读取 |
| 排队任务取消 | Worker 停止时上传并取消；重启后仍 Cancelled、Attempts=0、CurrentRevision=0；Score `67801b5f-0d2a-44fd-8484-64c70df7793b` |
| 运行中取消 | 确认真实识别进入 Processing，取消后经过 Cancelling 到 Cancelled、CurrentRevision=0；`output/p1/active-cancel/flow.json`。Cancelling 阶段不允许 retry，以免取消标志被过早清除 |
| 取消后显式重试 | 同一 Score `3f422774-ed17-47b7-a6fe-a60f001070c4` 调用 retry 重新排队，随后识别成功、201 个音符、完成两个修订和导出；证据保留 running-recognition-cancelled-before-publish 与 explicit-retry-accepted 检查 |

这里的“中断”指 Aspire 正常停止进程，验证了服务取消与重新调度。尚未执行 OS 强杀、主机断电、数据库重启、跨主机部署、备份恢复和多 Worker 并发故障试验。

## 旋转裁剪与首调导出

恢复样本就是 P0 构造的真实 PDF 变体 `shifted-crop-r270.pdf`，不是空白坐标测试页。MediaBox `[37,-19,632.28,822.89]`，CropBox `[49.25,53.5,620.75,806.25]`，`/Rotate=270`。

Revision 1 / 2 固定 C 路径 200 / 201 个自动放置。之后创建 Revision 3，使用明确的 G4 大调、从物理小节 5 起 D4 大调；两个并发保存请求分别得到 **202 和 409**，只生成一个新修订。原始识别数据未重新生成。

Revision 3 的数字字符串变短，布局结果为 201 / 201 自动放置；这只是本次首调配置的布局结果，不表示所有配置都能放下。原旋转、裁剪、页面起点和 3,861 个原绘图操作保留。Poppler 独立比较：9,669 个变化像素，**预留区域外变化 / 深色原墨迹覆盖 / 标注重叠均为 0**，所有放置标注可见且为黑色。C# labels / NoteId / 音高与导出 JSON 相符。保持原 `/Rotate`，所以打开时页面本身仍为横转方向。

证据：`output/backend/64a62a0fc0cb4680800bfc343c986131/revisions/3/{metrics,verification,annotations}.json`。[旋转裁剪后的首调 PDF](../output/pdf/p1-minuet-crop-rotate-movable-r3.pdf)。P1 不重复宣称整个 P0 九案例矩阵都通过了新的 API；本次 API 完整覆盖原页与这个组合变体。

## HTTP 负例与诊断

真实 API 已验证：重复幂等键 + 相同字节返回同一 ID；相同键 + 不同字节 409；错误合法凭据 404、无凭据 401；不支持扩展名 415、扩展名与魔数不符 400；未知产物 404；首调未提供从小节 1 开始的调性 400；过期 ExpectedRevision 409；并发保存恰好一个成功。证据见 `output/p1/final/flow.json` 与 `output/p1/recovery.json`。

复杂 `SchbAvMaSample.pdf` 通过 API 重新实际执行，结果 Failed / `pitch-consistency-failed` / CurrentRevision=0。Score `358eb076-30c5-4b21-ae8d-85131a18e7e8`；HeadInter 与 MusicXML 的不一致保持失败，未生成可下载的合格导出，也没有按顺序凑音符。详细原因保留在该任务日志及 error.json。

## 仍未覆盖

全页人工语义 ground truth、多页 PDF、任意倾斜扫描、图片 EXIF 方向、完整 ottava、跨谱表高级语义、手动移动标注、MusicXML 输入重排、独立导出重试 / 取消、账户和凭据 TTL、限流、隔离沙箱、容器化工具与字体、清理、容量、生产部署与恢复均未验收。Worker 目前与本机工具、共享本地目录配套。

当前的“200 / 201 放置”是布局分母，不是识谱准确率。识别与关联一致也不能代替人工音高真值。P2 将建立真实 Nuxt 4 校对工作台，并显示待放置 / 未校对状态。
