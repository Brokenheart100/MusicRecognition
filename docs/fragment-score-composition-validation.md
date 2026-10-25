# 图片片段拼谱首版实现与真实验证

日期：2026-10-24。结论：**网页到 MusicXML / PDF 的辅助拼谱流程已实现并本地验证，原谱语义准确率尚未验收。** 本报告补充 [原设计方案](fragment-score-composition.md)，不代表 P4 整阶段完成。

## 已交付的使用流程

在原谱工作台侧栏点击「图片片段拼谱」，或打开 `/fragments`：

1. 添加同一钢琴编配的 PNG / JPEG 图片，最多 12 张、合计 20 MiB；每张不超过 2000 万像素。原件独立保存。
2. 拖动或用上下箭头排序。默认仅移除明显视频黑边；可用 Cropper.js 框选谱面，保留谱号、拍号、调号、八度线及完整小节。
3. 开始识别，查看当前阶段、逐片段识别小节数、整份 MusicXML 预览与诊断。范围采用「本片段第几小节」，不依赖截图印刷编号。
4. 按完整小节选择保留区间，以处理相邻截图的重叠部分；范围、顺序、裁剪或标题修改均保存为新版本。勾选阅读校对提示后保存版本，即可下载 PDF。
5. 下载 `.musicxml`、`.mxl` 和重排 `.pdf`；历史成功版本可独立查看。刷新后从本机最近项目恢复，访问凭据不放入 URL。

新模式暂不提供浏览器内音符、时值、声部编辑，也未加入简谱数字标注。需要修音或改节奏时，下载 MusicXML 到 MuseScore 等记谱软件继续校对。确认复选框表示用户了解草稿限制，不表示系统已证明所有音符正确。

## 复用的架构与依赖

| 层次 | 实现 |
| --- | --- |
| Domain | `FragmentPlan` 校验顺序、归一化裁剪和完整小节区间 |
| Application | `FragmentService` 通过端口完成导入、版本、权限与产物读取 |
| Infrastructure | 复用 `ScoreJob` / `ScoreRevision`、EF Core outbox、Hangfire、取消/超时和中文诊断；内部以 `.fragments` 区分任务，无新增表或迁移 |
| 识谱 | 固定 Audiveris 5.11.0，把截图作为有序多页谱识别，共享上文谱号与拍号；依据 MusicXML 分页标记对应原片段 |
| 音乐结构 | music21 10.5.1 审核音符数量与时序；XML 结构选取保留原始音符元素，传递切点前有效的谱号/调号/拍号等属性 |
| PDF 刻谱 | 固定 MuseScore Studio 4.7.5，CLI 重新刻谱并回导 MusicXML，比较音高、谱表、起始时刻和时值后发布 |
| Web | Nuxt 4 / Nuxt UI、Cropper.js 1.6.2、现有 OSMD；hey-api SDK / Zod 由 OpenAPI 生成，TanStack Query 管理服务端状态，Pinia 管理本机草稿 |

新增 7 个 `/api/fragments` 操作，使用现有 `X-Score-Token` 能力凭据和请求幂等机制。原谱接口拒绝把拼谱任务当成原谱标注任务修改；文件接口采用固定名称白名单，不能通过任意路径读取服务器文件。

可选依赖准备方法见 [scripts/README.md](../scripts/README.md)。MuseScore 从官方固定 MSI 做便携行政提取，校验 SHA256，保存在忽略的 `.tools/`；没有全局安装。Windows 包只包含 `windows` Qt 平台插件，转换时明确使用该插件，避免强制 `offscreen` 导致启动失败。处理上传文件期间不下载工具。

## Orange 两张截图的实际结果

输入为用户提供的 `10-484.png`、`16-572.png` 两张 Orange 截图，均为 1920×1080；原件位于 `D:/Downloads/`，未修改。默认裁剪只去左右黑边。

真实浏览器验证项目：`465a82e4-74d6-4e0a-96ba-6ef1c1a10a68`，标题「Orange · 两张截图真实验证」。

| 检查项 | 结果 |
| --- | --- |
| 片段识别与顺序 | 第一张 4 小节，第二张 7 小节，合计 11 小节 |
| XML 已识别音符 | 193 个有音高符头；不等于原图所有音符的人工真值 |
| 最终导出 | v7，11 小节、2 页 PDF，MusicXML / MXL 可下载 |
| 范围修改 | v6 将第一片段起点改为 2，导出 10 小节；v7 恢复为 1，重新生成 11 小节 |
| 错误恢复 | v3 设置末小节 99，明确失败 `invalid-measure-range`，不发布该版 PDF；修正范围后 v4 成功 |
| 识谱缓存 | 只改确认状态或保留范围时复用相同识谱结果；原图、顺序或裁剪改变时使用不同缓存键 |
| 历史导出 | v6 / v7 后再次比较旧成功版本文件哈希，未变化 |
| 权限与并发 | 无凭据读取返回 404；过期 ExpectedRevision 返回 409 |
| 浏览器 | 本机 Edge：上传、排序、框选/自动裁剪、预览、下载、失败恢复、刷新重开；390 px 视口无页面横向溢出。未实测 iOS / Safari |

补充回归：修复已有 XML Blob 在组件重新挂载时未绘制的问题；已验证 PDF→MusicXML 切回、刷新后重开项目均能生成 SVG 预览。对应断言已加入浏览器验收脚本。

可直接查看：

- [生成的 PDF](../output/fragment-validation/Orange-composed.pdf)、[MusicXML](../output/fragment-validation/Orange-composed.musicxml)、[MXL](../output/fragment-validation/Orange-composed.mxl)
- [桌面工作台](../output/playwright/fragments-desktop.png)、[PDF 预览](../output/playwright/fragments-pdf-preview.png)、[移动宽度](../output/playwright/fragments-mobile.png)
- [版本与缓存验证](../output/fragment-validation/revision-integrity.json)、[独立导出检查](../output/fragment-validation/export-verification.json)、[浏览器流程记录](../output/fragment-validation/browser-report.json)

这些可再生产物位于忽略的 `output/`，不会自动纳入版本控制。

## 内容保护与验证边界

每版输出保留 `geometry.json` 和 `provenance.json`：原图尺寸、裁剪像素、原图↔裁剪图平移矩阵、PDF 点比例、输出小节到原片段/局部小节的映射。测试确认原图摘要不变、裁剪矩阵互逆、输入 PDF 页面几何匹配。

最终 v7 PDF 已用 Poppler 栅格化并逐页检查，标题和三组钢琴谱表可见，未出现空白页或页边裁断；页面留白较多，后续可增加纸型与版式选择。页图保存在 `output/fragment-validation/Orange-page-1.png`、`Orange-page-2.png`。

合并阶段比较所选原始 `<note>` 元素与结果元素，确保选区未改写音符；MXL 包含同一份权威 MusicXML。music21 解析音符数必须一致。MuseScore 回导后进一步比较音高/谱表和每个音符的起始时刻/时值；变化时整版不发布。这些检查证明处理链没有继续丢改已识别音符，**不能检出识谱引擎本身的所有漏音、错音或记号遗漏**。

本样本已知限制：

- 第 5、6 小节存在超出拍号的时值，界面保留明确警告；未自动删改音符来凑拍。
- 原图可见八度线，而导出的识谱 MusicXML 中 `<octave-shift>` 数量为 0。八度线语义未完整恢复，相关音高需要对照原图人工检查；不能据 193 个音符往返一致声称八度正确。
- 跨切点延音线、连奏线、踏板等未自动修复；裁取范围时增加边界审核提示。重复片段由用户选择完整小节排除，尚无自动去重或缺失小节补全。
- 首版只接受一个钢琴 Part、至多两个谱表。分页边界不匹配或其他乐器布局拒绝自动拼接。
- 网页 OSMD 预览与 MuseScore PDF 的分页可不同；正式分页以保存版本 PDF 为准。没有对所有装饰音、连线、反复等符号做逐项视觉真值验收。

下一步优先做八度线与节奏疑点的校对能力，建立这 11 小节的人工真值，再扩展重复段建议和跨边界记号处理。

## 自动检查与复跑

本轮检查通过：61 项 Python 测试、40 项领域测试、7 项基础设施非集成测试；前端 18 个 OpenAPI 操作契约、生成 SDK 可复现检查、布局规则、TypeScript、ESLint，以及 Nuxt 构建。API / Worker 本地编译通过。前端构建仍有现有 Verovio 外部模块与大块提示；依赖审计遗留项未由本轮关闭，不代表生产发布验收。

新增用例与工具：

- `tests/python/test_fragments.py`：黑边与裁剪、小节区间、分页拒绝、上下文传递、原 XML 保留、刻谱器音高/谱表变化检查。
- `tests/MusicRecognition.Domain.Tests/FragmentPlanTests.cs`：来源索引、范围、裁剪值约束。
- `tests/e2e/fragments-browser-check.js`：通过真实网页上传两张截图、确认导出、制造非法区间并修复；仅用于专用验证项目，会创建任务和修订。
- `tests/acceptance/verify-fragment-export.py`：独立验证原件、几何、所选音符、MXL 与刻谱器往返。

```powershell
pwsh -File tests/run.ps1
.tools/poc-python/Scripts/python.exe tests/acceptance/verify-fragment-export.py --job output/backend/465a82e474d64e0a96ba6ef1c1a10a68 --version 7 --output output/fragment-validation/export-verification.json
```

本地 Aspire API、Worker 与 Web 已恢复运行；不使用生产数据或远程发布作为本次验证条件。
