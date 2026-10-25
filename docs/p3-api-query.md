# P3：OpenAPI、hey-api 与 TanStack Query 验证

日期：2026-10-22。范围：补齐 API 契约，替换手写前端 API 类型与请求，迁移任务轮询及服务端状态，并复跑同一份六页真实 PDF。识谱和 PDF 排版算法沿用既有 P3 实现，未引入 pdfme。

## 实现与边界

### API 是契约来源

`MusicRecognition.Api/Program.cs` 为 10 个操作声明稳定 operationId；上传、保存修订、创建整份导出均为 `202 + JSON`。所有受保护操作声明 `X-Score-Token`，上传另声明 `Idempotency-Key`，包含服务端实际执行的长度限制。multipart 描述真实二进制文件和可选预处理值，工件下载声明二进制响应及 `206`。

`ContractSchemaTransformer` 补齐状态枚举、数值限制、必有字段和标注几何元组；JSON 数值采用严格数字格式。响应允许正常的可空错误字段及尚未生成的页导出状态。错误契约为 ProblemDetails（400 / 401 / 404 / 409 / 413 / 415 / 500）；框架直接返回的无正文错误也转换为 ProblemDetails。

新增 `GET /api/scores/{id}/layout?version=N`，只返回单页修订的浏览器布局投影，由 Application 中的 DTO 描述。文档父任务调用该接口会得到 409，必须选择具体页面。完整识谱审计信息继续通过原有 artifacts 下载，避免把父文档汇总 JSON 和单页布局混为同一种类型。Nuxt 固定上游与路径白名单增加 layout，保留同源代理、能力凭据和 `private, no-store`。

### 生成 SDK、类型与 Zod

前端集中固定 `@hey-api/openapi-ts 0.99.0`、`@tanstack/vue-query 5.104.5`、Vue `3.5.44`、Zod `4.6.7`，开发验证工具 tsx `4.23.15`；保留 npm lockfile。`openapi/score-api.json` 是去除 Aspire 动态端口的后端快照，`openapi-ts.config.ts` 生成 Fetch SDK、TypeScript 类型、Zod schema 和 TanStack Vue Query 辅助函数。

`shared/types/score.ts` 仅保留编辑器别名、本机记录和草稿结构，API 类型 / schema 均从生成代码重导出。草稿 schema 组合生成的 Pitch、Placement、Numbering；保存请求再使用生成的 RevisionRequest schema 校验。SDK 自动执行 JSON 成功响应 Zod 校验，错误响应保留服务端 ProblemDetails。二进制下载显式按 Blob 读取，不做 JSON 校验。上传文件不套用生成器的字符串文件 schema，以 Fetch 的 FormData serializer 发送 File。

生成目录不手改，ESLint 只排除生成的第三方客户端模板；应用代码仍完整 lint。`api:check` 在独立临时目录重新生成，逐字节比较全部 18 个文件，结束后清理本次创建的临时目录。

### Query 与 Pinia 的职责

`app/composables/useScoreServer.ts` 中的 Query 管理父任务、当前页任务、修订、标注布局、原谱 Blob 和整份导出。上传、保存、取消、重试、合并通过 mutation 调用 SDK，完成后失效相关查询。取消旧请求使用 AbortSignal；Query 自行去重与轮询，删除原有手写 setTimeout 任务循环。

识别与排版的 Queued / Processing / Cancelling 状态继续轮询；父任务还会检查每页 exportState，避免“识别已完成、排版未完成”时过早停止。前台间隔 1.5 秒，后台查询间隔配置为 5 秒，并遵守 Query 默认后台暂停策略；终态停止轮询，重新聚焦时可刷新状态。网络 / 服务端错误最多重试两次，业务 4xx 不自动重试，mutation 不自动重试。上传中断仍复用本机 capability 与幂等键。

Pinia 管理本机乐谱记录、当前文档 / 页码 / 选中版本、编辑选择、未保存草稿及 VueUse 的 50 步撤销重做。它暴露 Query 的只读投影供已有组件使用，不另存一份服务端快照。只有页面或修订身份变化时才恢复草稿，同一修订的后台刷新不会清空未保存编辑与历史。

每个 Nuxt app / SSR 请求创建独立 QueryClient；私有乐谱查询只在浏览器启用，不将缓存或凭据写入 SSR payload。Fetch client 为应用作用域，每次调用显式传凭据，没有全局 setConfig(token)。查询键使用随机会话作用域、资源 ID、页修订版本，不包含明文凭据。为避免 hey-api 默认生成键中的 headers 保留 capability，应用在此组合自己的资源查询键并复用生成 SDK；生成的 Query 辅助代码仍可供后续公开接口使用。换文档会取消并移除旧作用域缓存，mutation 缓存立即回收。

整份 Ready 导出只有在记录的每一页版本仍等于当前版本时才能下载；页面产生新修订后改为重新合并，避免下载旧快照。

## 六页真实流程

输入仍为 `output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf`，4,550,783 字节，SHA256：

`23d4b3a203992fe7e30e25d29c1fc1669c3340e9377a71ce56746e462f301324`

本次在 Edge 工作台通过真实 File 输入上传，生成新任务 `325a8974-c946-46ed-a7cc-03927c53f3e2`。浏览器在等待期间由 Query 轮询真实 API；六页均识别并排版 Ready。没有复用上一轮任务来代替这次上传。

浏览器逐页打开、选择音符并查看音名 / 八度；每页 SVG 音符数与 API 一致，未出现跨页数据串用。第一页将简谱方式保存为以 C4 为主音的 C 大调首调，形成真实修订 v2（其标注数值与固定 C 等价，用于验证保存链路，并非人工修改原谱音高）。其余页面 v1；整份导出 v1 记录快照 `[2,1,1,1,1,1]`。保存和发起合并实际 HTTP 状态均为 202。

| 页 | 识别音符 | MusicXML 锚点匹配 | 待确认节奏声部 | 八度线范围 | 应用八度修复的音符 | 已放置黑色标注 | 待人工放置 |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 291 | 285 | 6 | 3 | 4 | 283 | 8 |
| 2 | 399 | 399 | 0 | 6 | 173 | 360 | 39 |
| 3 | 551 | 551 | 0 | 1 | 2 | 504 | 47 |
| 4 | 520 | 520 | 0 | 4 | 22 | 426 | 94 |
| 5 | 625 | 622 | 3 | 2 | 7 | 527 | 98 |
| 6 | 541 | 541 | 0 | 0 | 0 | 492 | 49 |
| 合计 | 2927 | 2918 | 9 | 16 | 208 | 2592 | 335 |

工作台成功合并并下载 `source · 全曲标注版 v1.pdf`。浏览器下载副本与生成 SDK 下载的 PDF 逐字节哈希一致：

- 文件：`output/pdf/p3-query-six-page-annotated.pdf`
- 大小：5,409,409 字节，6 页。
- SHA256：`0313799683358aea53c55bfeaa18f75d046432a678b524e0e0098fbfd4063da9`
- 本次浏览器 pageerror：0。

`verify-p3-export.py --evidence output/p3-query-validation` 对整份导出和六个实际修订运行独立验证：页数与页序、原始图像哈希、原谱绘制操作、MediaBox / CropBox / Rotate 均保留；六页标注区域外像素变化 0、原墨迹覆盖 0、相互重叠 0、不可见已放置标注 0。标注颜色为 `#000000`，像素验证为黑色抗锯齿混合；最大 PDF 基线误差小于 0.001 pt。已渲染整份 PDF 并检查六页联系图。

证据位于 `output/p3-query-validation/`：`job.json`、`export.json`、`metrics.json`、`sdk-validation.json`、`browser-validation.json`、`draft-regression.json`、`http-contract.json`、`verification.json`、六页验证日志、`contact.png`、工作台截图。运行 capability 只放在已忽略的 tmp/ 与本机浏览器记录中，不写入报告。

## 验证与局限

- 契约检查：10 个稳定操作、必需凭据头、异步 202、ProblemDetails、音高数值限制；损坏响应由 SDK 内的 Zod 拒绝。
- 真实 HTTP：缺失凭据 400、过短凭据 401、错误凭据 404，均为 application/problem+json；原文件 Range 请求 206，同源代理保持 private,no-store。
- 生成复现、Nuxt typecheck、ESLint、生产构建通过。
- .NET Domain 25/25、Infrastructure 1/1、Python 20/20 通过。
- 受控浏览器回归：旧本机记录恢复、50 步历史、撤销分支、输入框焦点、跨页 / 修订隔离、刷新保留草稿、保存清理、损坏草稿恢复全部通过；另验证终态 3.5 秒无继续轮询，同一修订的服务端刷新保留脏草稿。该用例使用受控 API 响应，不能替代上述真实识谱流程。
- 最终 Aspire api / worker / web / PostgreSQL 均 Healthy。
- 新引入 hey-api 的 js-yaml 4.2.0 审计问题通过 package.json override 到兼容的 4.3.2 解决，生成复现检查重新通过。npm audit 仍有 11 个 high，来源为现有 Nuxt 依赖链的 braces / node-forge 及其上游影响；本次未强制降级 Nuxt 或忽略审计。生产发布尚未验收。

这些结果证明集成和导出闭环，不代表原谱每一个音符都识别正确。原有局限保留：部分低音全音符仍可能漏识别；9 个已识别音符缺少节奏声部；335 个标注因碰撞策略等待人工放置。2,927 是已识别音符数，不是原谱总音符真值。本次逐页检查是界面与数据一致性抽查，尚未建立全曲人工音高真值。真实 iOS / Safari 和生产环境仍需单独验收。

## 复跑

从 `aspire describe --format json` 获取当前 API / web 地址，先 `aspire wait api`、`aspire wait worker`、`aspire wait web`。

```powershell
cd MusicRecognition.Web
npm ci
npm run api:pull -- http://localhost:<API端口>
npm run api:generate
npm run api:check
npm run test:contract
npm run typecheck
npm run lint
npm run build
```

真实浏览器新上传同一 PDF，待六页排版就绪，使用 `tests/e2e/p3-query-browser-check.js` 在已打开的 Playwright CLI 会话执行：该脚本要求新任务各页起始 v1，逐页检查后保存第 1 页 v2、导出并下载。独立 SDK 收集脚本从仅本地的 access JSON（id / token）读取凭据：

```powershell
# 在 MusicRecognition.Web 中；不要输出 private access 文件内容。
npx tsx tests/e2e/check-live-sdk.ts http://localhost:<API端口> ../tmp/p3-query-access.json ../output/p3-query-validation
# 在仓库根目录中：
.tools/poc-python/Scripts/python.exe tests/acceptance/verify-p3-export.py --source output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf --pdf output/pdf/p3-query-six-page-annotated.pdf --evidence output/p3-query-validation
```

实现依据：[hey-api Zod](https://heyapi.dev/docs/openapi/typescript/plugins/zod)、[TanStack 生成插件](https://heyapi.dev/docs/openapi/typescript/plugins/tanstack-query)、[TanStack Vue Query SSR](https://tanstack.com/query/latest/docs/framework/vue/guides/ssr)。
