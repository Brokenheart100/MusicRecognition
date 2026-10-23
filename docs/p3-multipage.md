# P3：多页原谱工作台与六页真实验证

日期：2026-10-22。原六页 PDF 已完成上传、6/6 页识别、页级排版、整份导出和浏览器下载。导出链路验收通过；自动识谱尚不等于整谱人工校对完成，仍有 335 个标注待手工放置及原谱漏检风险。

## 设计与实现

复用 Hangfire、EF Core、Audiveris 5.11.0、PDF.js、ReportLab、pypdf 和 OpenCV，没有引入 pdfme 或替换识谱引擎。原件为父记录，每页为独立的持久任务和修订；页级草稿隔离。整份导出固定有序的“页号、子任务、修订版本”快照，不跳过失败页，不改变已生成导出的内容。

上传上限 20 MiB、最多 30 页。PDF 按 216 / 180 / 144 / 108 / 72 DPI 依次选择不超过 2,000 万像素的档位；本次样本使用 180 DPI。拒绝加密和非默认 UserUnit；保留原页序、MediaBox、CropBox、旋转和背景绘图。调性与小节编号仍按页校对。

## 本次修复

1. **八度线范围与谱表。** Audiveris 多处只识别了“8”并分配默认短线；部分符号关联到了相邻谱表。OpenCV 在原栅格中检查已识别数字旁的虚线和结束钩，按谱表几何及钩方向恢复范围，支持本样本中的上移、下移及每行显式写出的延续记号。使用引擎原始音符头及其书写音高计算实际音高，坐标不变。发现 16 段有证据的八度线，208 个已识别音符应用了八度修正。数字候选没有虚线证据时不猜测范围，保留诊断记录。
2. **音高一致性。** 原始 MusicXML 只导出已经分配声部/时隙的音符，不能用“全部 HeadInter 数量等于 MusicXML”判断识别是否完整。新版按乐器、小节导出编号、谱表、横坐标、声部、音名、变化音及八度逐音符匹配，并对引擎已关联的原生八度范围作明确归一化。和弦使用一对一匹配，不能重复消费同一个音符头。无法解释的音高、声部、位置差异及已分配声部的音符缺失仍会拒绝发布。最终实际八度还须与恢复范围一致。
3. **保留未定节奏音符。** 本次保留 9 个未分配节奏声部的音符头，原始锚点不丢弃，不给它们虚构声部。校对面板显示重点复核提示。全部 2,927 个已识别音符均有对应的标注记录。
4. **导出编码。** 首次正向合并暴露出 Windows 默认 GBK 读取 UTF-8 标注 JSON 的错误。合并器现显式使用 UTF-8；Unicode 升降号、原页序合并和错误页序拒绝均加入了回归测试。

原始 `.omr`、MusicXML 和 `engine-notes-1.json` 保留。`octave-repairs.json`、最终音符 JSON、页级 metrics 记录修复证据。八度修复需要已安装 OpenCV；没有八度候选时不加载它。光照增强仍由 `Preprocessing=None/Adaptive` 独立控制，本次为 `None`。

## 同一真实样本与运行记录

原件：`output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf`，4,550,783 字节，6 页。

原件 SHA-256：`23d4b3a203992fe7e30e25d29c1fc1669c3340e9377a71ce56746e462f301324`。

此前父任务 `eac00933-f465-42e0-9cb0-bfc24717a6f0` 在 144 DPI 下有 4 页识别失败、2 页成功，整份导出返回 `409 pages-not-ready`。历史证据没有覆盖或伪装成成功。

本次新建父任务 `da11eab1-4a3f-448c-954e-bf92e2677e3b`，六页识别及页级导出全部 Ready。整份导出 v1 因 UTF-8 读取问题失败；修复后直接复用六页不可变 v1 修订，整份导出 v2 Ready，无需重跑识谱。

| 页 | 已识别音符 | XML 匹配 | 节奏待定 | 恢复八度线 | 修正八度音符 | 黑色标注已放置 | 待手工放置 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 291 | 285 | 6 | 3 | 4 | 283 | 8 |
| 2 | 399 | 399 | 0 | 6 | 173 | 360 | 39 |
| 3 | 551 | 551 | 0 | 1 | 2 | 504 | 47 |
| 4 | 520 | 520 | 0 | 4 | 22 | 426 | 94 |
| 5 | 625 | 622 | 3 | 2 | 7 | 527 | 98 |
| 6 | 541 | 541 | 0 | 0 | 0 | 492 | 49 |
| 合计 | 2,927 | 2,918 | 9 | 16 | 208 | 2,592 | 335 |

## 整份导出与独立检查

最终文件：`output/pdf/p3-six-page-annotated.pdf`，6 页，5,409,409 字节。

SHA-256：`0313799683358aea53c55bfeaa18f75d046432a678b524e0e0098fbfd4063da9`。

- 六页原背景图像哈希、页序、MediaBox / CropBox / Rotate 和原始绘图指令均保持一致。
- Poppler 分别渲染每页原件与标注件，再比较实际像素；六页的标注区域外变化、覆盖原谱深色墨迹和标注间重叠均为 0；已放置标注无不可见项。
- 新增墨迹通过纯黑混合检查，存在实黑像素；PDF 文字基线最大误差为 0.000429 pt。
- 查看了最终六页渲染总览，页面没有错序或裁切。预览：`output/p3-validation-v2/contact.png`。
- 浏览器依次切换六页，画布音符数 / 可见标注数与上表一致，页面错误为 0。通过“下载整份 PDF”按钮得到的文件与 API 下载 SHA-256 相同。

可复核记录：`output/p3-validation-v2/job.json`、`export.json`、`metrics.json`、`verification.json` 及每页验证日志。匿名访问能力只保存在被忽略的临时目录，没有写入报告或公开验证数据。

## 自动化检查与复现

- Python 测试：20/20 通过，包含八度恢复、音高/声部/锚点错误拒绝、和弦匹配、无 OpenCV 行为、DPI 上限、UTF-8 合并及页序保护。
- .NET Domain 测试：25/25；Infrastructure 测试：1/1 通过（使用项目指定的 Python 和测试根目录环境变量）。
- Nuxt `typecheck`、ESLint 和生产构建均通过。
- Aspire API、worker、web 健康，以上上传、逐页任务、修订导出、合并和下载均使用真实运行栈。

```powershell
.tools/poc-python/Scripts/python.exe -m unittest discover -s tests/python
# URL 从 aspire describe 获取；下列命令会创建真实识别任务。
.tools/poc-python/Scripts/python.exe scripts/validate-p3-live.py --url http://localhost:<api-port> --source output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf
.tools/poc-python/Scripts/python.exe scripts/verify-p3-export.py --source output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf
```

## 验收范围与后续校对

本次通过的是六页处理和整份导出闭环。335 个标注没有找到安全留白，仍按既有规则保留为待放置，没有强行压在谱线上；它们尚未绘入 PDF。9 个节奏待定音符的音高仍需要复核。

原谱人工全页真值尚未建立。逐音符校验保证已识别音符的来源和转换一致，不能证明识谱器没有漏掉原谱音符；例如第 1 页前几行低音声部的部分全音符仍有漏检。八度恢复也以引擎识别出的数字候选为前提，不能宣称所有漏检记号都已自动补齐。因此该 PDF 可用于继续校对，尚不能作为全谱音高完全正确的验收结论。

实现依据：[Audiveris Octave Shift 模型](https://audiveris.github.io/audiveris/_pages/guides/specific/octave_shift/)、[PartwiseBuilder 音符与声部导出](https://github.com/Audiveris/audiveris/blob/5.11.0/app/src/main/java/org/audiveris/omr/score/PartwiseBuilder.java)。实际 API 同时以本机固定版本的类签名和运行结果核对。

## 前端基础能力复用补充（2026-10-22）

将手写撤销 / 重做栈替换为 VueUse `useRefHistory`，保留 50 步上限；加载另一页、另一修订及保存成功后建立新的历史基线。最近记录、当前乐谱与逐页草稿改用显式导入的 VueUse `useStorage`（避免与 Nuxt/Nitro 同名函数混淆）；上传重试凭据改用 `useSessionStorage`。旧 Storage key 和 JSON 格式兼容，原有 Zod 校验继续保留。Pinia 的 `skipHydrate` 防止服务端空列表覆盖浏览器记录。

全局快捷键改用 `onKeyStroke`，焦点通过 `useActiveElement` 跟踪，事件生命周期交给 VueUse；保留输入框、可编辑区和弹窗避让，忽略输入法组合事件。页面离开监听也使用 `useEventListener` 自动清理。

浏览器回归脚本：`scripts/vueuse-browser-check.js`。使用受控的两页 API 响应与真实 Minuet PDF 预览，不更改真实识别任务。已验证旧记录恢复、撤销 / 重做、新编辑清空重做、50 步上限、跨页历史隔离、跨页及刷新恢复草稿、保存后移除旧草稿、修订历史隔离、损坏草稿容错、表单焦点保护、帮助与保存快捷键；页面错误为 0。此回归验证前端状态管理，不替代上述六页真实样本的识谱 / 导出验收。

API 依据：[useRefHistory](https://vueuse.org/core/userefhistory/)、[useStorage](https://vueuse.org/core/usestorage/)、[onKeyStroke](https://vueuse.org/core/onkeystroke/)。


## OpenAPI 与 Query 集成复验（2026-10-22）

已完成后端契约补齐、hey-api SDK / TypeScript / Zod 生成，以及 TanStack Query 的任务轮询、修订、布局与导出状态管理。Pinia 保留本机草稿和撤销历史。使用同一六页输入重新创建任务 `325a8974-c946-46ed-a7cc-03927c53f3e2`，真实浏览器逐页打开、第 1 页保存 v2、合并 `[2,1,1,1,1,1]` 并下载成功；黑色标注和原谱保护的六页独立检查通过。完整证据、依赖审计剩余问题与复跑步骤见 [OpenAPI / Query 验证报告](p3-api-query.md)。识谱完整性局限不因集成通过而解除。

## Poppler 与两页样本修复（2026-10-22）

新增项目内 Poppler 固定路径和 Worker 启动依赖检查，修复从其他终端启动时 `pdftoppm` 不在 PATH 导致的识别前失败。用用户上传的两页《夜的钢琴曲一》重新验证，处理 Audiveris 同一符头被多个声部时隙重复导出的有证据情形，保留严格音高、锚点及重复次数验证。两页共 282 个黑色标注，整份 PDF 导出及独立原谱保护检查通过；第一页 3 个节奏声部待确认。完整故障记录与复跑步骤见 [Poppler 运行时验证报告](poppler-runtime-validation.md)。

## 三页样本的导出一致性修复（2026-10-22）

最新《MY HEART WILL GO ON》三页样本已修复无时间偏移渐强符号导致的 MusicXML 整小节丢失，以及无效调号造成的候选音高上下文不一致。全新上传任务完成逐页识谱和整份黑色标注导出：827 个符头全部保留，739 个安全放置，88 个待人工放置，24 个节奏声部待确认。六页 ninelie 保存 OMR 的适配器回归也通过；完整证据、转调处人工校对范围及复跑步骤见 [音高一致性修复报告](pitch-consistency-fix.md)。
