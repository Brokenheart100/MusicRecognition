# P4：重新排版与质量扩展

2026-10-23，状态：**已启动；首批功能受限可用，整阶段尚未通过验收**。

后续布局已升级为下方优先，重排固定字号按页面宽度换算；最新结果见 [下方优先验证](below-first-layout.md)。下面保留首次 P4 验证记录。

字号现已默认自适应，重排的和弦标注支持根据留白逐档缩小，最低 3 pt；固定字号不缩小。见 [自适应字号验证](adaptive-font-sizing.md)。复杂谱的导入覆盖与整阶段未验收结论保持不变。

## 本次交付

工作台新增「重排辅助谱」，使用固定版本 `verovio@6.3.0`（LGPL-3.0-or-later，完整保留包许可），通过官方 WASM / ESM 接口按需加载。原有 OSMD 识谱预览与原谱标注仍可切换。重排功能支持标准 / 宽松间距、简谱显示开关、缩放、点击音符回原谱校对，以及当前原始页对应的重排 HTML 下载。HTML 包含重新分页的 SVG，可独立打开或通过浏览器打印，不属于原 PDF 叠加产物。

原谱、OSMD 初次识谱预览、Verovio 保存修订重排是三个不同视图。重排视图展示所选**已保存修订**；未保存草稿有明确提示，保存后的音高、编号、逐音颜色 / 字号和隐藏状态才参与重排。原谱手动位置不会搬到新 SVG。重排视图隐藏原谱 PDF 下载按钮，防止把两种产物混淆。

## 架构与映射

1. `ReflowService` 调用现有授权 / 修订用例，要求单页任务及 Ready 修订；API `GET /api/scores/{id}/reflow?version=N` 返回类型化 `ReflowView`。
2. Infrastructure 调用已有工具运行器，以独立工作目录运行 `scripts/runtime/reflow.py`。复用原音高、声部和符头锚点的一对一匹配器，不按 XML 音符顺序与 C# Notes 凑配。
3. 匹配后按业务 NoteId 和已验证的 XML 出现次数生成 `mr-<NoteId>-<occurrence>`。源 `score.mxl` 和原谱不改写；仅生成当前修订的辅助 XML，缓存于 `revisions/N/reflow-v1.json`。
4. XML 中写入 C# 修订的权威音高；编号仍由 C# 生成。八度线统一展开为实际音高，并显示提示，避免引擎八度写法差异造成二次移调。隐藏操作仅隐藏辅助数字，保留谱面音符。
5. Verovio 将 MusicXML ID 保留为 MEI `xml:id` 与 SVG 音符 ID。前端逐个核对这些 ID；只有没有导入错误且所有 XML 音符 ID 都存在时才允许下载。遗漏的、没有节奏声部的源符头另列，不伪造 XML 音符。
6. 辅助数字在**新的 SVG 坐标系**内寻找候选空白，以谱面图形包围盒和其他标注包围盒避让。找不到位置则计为待放置，不缩小字号。当前不提供重排标注的手动拖动；点击音符回原谱校对。

前端仍使用生成的 hey-api SDK / TypeScript / Zod 与 TanStack Query；OpenAPI 已更新并复现检查通过。无需引入 music21 或训练模型。准备 XML 是不重跑 OMR 的短工具操作；缓存与诊断目录的回收仍属于 P5。

## 真实样本与准入结果

三组共 10 页。表中“XML 关联”是独立导出一致性，“SVG 缺 ID”是渲染导入覆盖，均不是识谱正确率。

| 样本 / 页 | 原符头 | XML 关联 | SVG 缺 ID | 重排下载准入 |
| --- | ---: | ---: | ---: | --- |
| Mutopia 小步舞曲 | 201 | 201 | 0 | 通过 |
| My Heart Will Go On 1 | 304 | 295 | 84 | 不通过 |
| My Heart Will Go On 2 | 310 | 295 | 59 | 不通过 |
| My Heart Will Go On 3 | 213 | 213 | 17 | 不通过 |
| ninelie 1 | 291 | 285 | 10 | 不通过 |
| ninelie 2 | 399 | 399 | 109 | 不通过 |
| ninelie 3 | 551 | 551 | 259 | 不通过 |
| ninelie 4 | 520 | 520 | 208 | 不通过 |
| ninelie 5 | 625 | 622 | 256 | 不通过 |
| ninelie 6 | 541 | 541 | 9 | 不通过 |

Verovio 对复杂 Audiveris MusicXML 出现 `Adding 'beam' to a 'chord'`、`Chord starting point has not been found` 等导入问题。以 My Heart Will Go On 第 1 页为例，295 个源 XML 音符中仅 211 个 ID 出现在 MEI / SVG。这是渲染器与引擎输出之间的兼容性问题，不能归到 OMR 漏检分母，也不能把有图像等同于成功。对应页面展示不完整诊断并禁用下载；没有修改 Verovio 源码、删掉问题音符或放松准入。

当前结果不支持将 Verovio 作为复杂曲目的默认替代。原 OSMD 预览仍可用于对照，但其音符 ID 映射没有作为本次合格重排产物的证据。后续应评估规范化 MusicXML 或其他渲染路径，并对相同样本重新测量，不直接扩大上线范围。

证据：`output/p4-validation/matrix/report.json` 及逐页 `*-import.json`；脚本 `tools/evaluation/p4-quality-matrix.py`。该矩阵固定于 Minuet v1；后面的修订试验为独立 v2。

## 小步舞曲的可用路径

独立真实上传任务 `90fb845a-c5fa-4863-b4f4-e84da78b25f6`，来源为仓库示例 PDF，源许可与哈希见 `samples/README.md`。

- v1：201 / 201 个 XML 音符 ID 保留，201 个黑色数字全部放置，没有待放置或隐藏项。独立浏览器包围盒检查，辅助数字之间的重叠对数为 0；这不是原墨迹逐像素验证或全页人工音高真值。
- 标准间距生成 1 个重排页，宽松间距生成 4 个重排页。源文件始终是一页，重排分页单独计数。
- 1440×1050 桌面与 390×844 小屏可查看；缩放和点击数字回原谱对应音符已验证。
- 下载的 `output/p4-validation/minuet-reflow.html` 为 v1 的黑色重排辅助谱。截图 `output/playwright/p4-reflow-desktop.png` / `p4-reflow-mobile.png`。
- v2：在专用验证任务中将一个音符改为 E♯5，并设为红色 7 pt，另隐藏一个标注。重排 XML、C# 数字 3 / 变化音 1、SVG 颜色 / 字号及隐藏状态一致；200 个放置、1 个隐藏。v1 的黑色配置不变。此为修订传递试验，不是对原曲音高的纠错结论。

脚本：`tests/e2e/p4-browser-check.js`、`tests/e2e/p4-revision-browser-check.js`、`tests/e2e/p4-incomplete-browser-check.js`。保存修订试验仅作用于专用验证任务。

## 质量真值与阶段边界

已建立 [独立质量集约定](../samples/quality/README.md)，要求源哈希、坐标系、独立 truthId、完整页标记和审核者。当前没有完整、独立、经人审核的真值，报告中的音高准确率、关联准确率和检出召回率均为 null。C# 修订值、引擎输出与 MusicXML 的一致性不能充当真值，AI 生成或构造测试答案也不标记为人工真值。

仍需完成：复杂谱导入覆盖、人工真值标注与统计、重排手动放置、完整曲目的重排合并与 PDF 导出验收、实际 iOS / Safari。P4 的整阶段验收不通过；本次可试用简单谱的修订重排辅助视图。

## 检查与复跑

- 34 项 Python 测试通过，含 5 项重排测试：修订音高 / 样式 / ID、重复 XML 来源、未分配声部计数与缺失拒绝、八度只展开一次、错误修订身份拒绝。原有严格校验保留。
- API 构建 0 警告 / 0 错误并经 Aspire 健康检查；授权重排 200、错误凭据 404、父文档 409、不可用修订 409，与既有修订读取约定一致。
- 前端类型检查、ESLint、SDK 可复现检查通过，Nuxt 生产构建通过。WASM 浏览器兼容性由本地 Edge 验证；生产构建提示 Node 分支的 `node:module` 被浏览器外部化，不宣称生产运行和其他浏览器已验收。
- Verovio WASM 模块约 7.3 MB，动态加载，只有打开重排视图才读取。依赖审计仍有原 Nuxt/Nitro 链的 11 项 high，见此前报告；未强制升级或豁免。

```powershell
# 重新识谱并准备单页参考样本：创建新的专用任务
.tools/poc-python/Scripts/python.exe tests/e2e/validate-p4.py --url <API 地址> --source MusicRecognition.Web/public/samples/minuet.pdf
# 对已保存的三组真实任务做覆盖矩阵；凭据从忽略目录读取
.tools/poc-python/Scripts/python.exe tools/evaluation/p4-quality-matrix.py --url <API 地址>
```

Verovio 官方依据：[ESM/WASM](https://book.verovio.org/installing-or-building-from-sources/javascript-and-webassembly.html)、[布局与间距](https://book.verovio.org/advanced-topics/layout-options.html)、[工具选项](https://book.verovio.org/toolkit-reference/toolkit-options.html)。本地安装包的 API 与最小自定义音符 ID 样本均已实际核对。
