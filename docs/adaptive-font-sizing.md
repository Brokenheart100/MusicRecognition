# 自适应字号与密集和弦验证

更新于 2026-11-20。原谱标注和网页重排使用 `adaptive-readability-v3`。

## 使用与范围

新任务默认「自适应 · 随谱面留白」、黑色。已有乐谱的显式固定字号保留；请在右侧「标注样式 → 字号」选择自适应，然后保存修改。若有框选，设置只作用于所选音符；清除选择后可设置全页。纯样式保存复用识谱结果，不重新运行 OMR。

单音目标字号按谱线或符头大小取 7–10 pt。双音、三音、四音目标分别约为单音的 93%、86%、81%。较大的和弦保留整列和从上到下的音高顺序。

已有自适应页面可以直接点击「重新计算标注」，生成新修订并应用最新规则，不需要重新识谱。此按钮在清除音符选择、没有未保存修改时可用。

## 2026-11-20 可读字号排版

1. 每组先用约 4.5 pt 找一个可容纳完整标注的空位。极密位置可试 4、3.5、3 pt，原谱按识谱栅格取整。若仍无空位，整组提示待放置。
2. 优先放大和弦，每档字号依次尝试下方、上方、右侧、左侧。放大时保留邻组的占位，整组放大成功才更新位置。数字、升降号和八度点一同参与碰撞检查。
3. 固定字号按指定值检查。手动位置保留原坐标，自适应字形可在该处缩小以适应空位。

### 真实 PDF 对比

在 25 个已识别的真实谱页上复用同一份音符、符头坐标与简谱标签，比较自适应布局。

| 指标 | 旧版 | 新版 |
| --- | ---: | ---: |
| 已放置标注 / 识别符头 | 9,947 / 10,114 | 9,950 / 10,114 |
| 小于 5 pt 的已放置和弦标注 | 3,955 | 630 |
| 已放置在下方 | 8,158 | 6,278 |
| 已放置在上方 | 1,705 | 3,382 |
| 《Snow Halation》第 2 页和弦字号中位数 | 4.67 pt | 7.33 pt |
| 同页三音和弦字号中位数 | 4.67 pt | 7.33 pt |
| 同页四音和弦字号中位数 | 4 pt | 5.83 pt |

《Snow Halation》新版七页样本按原页序合并，共有 3,798 个标注和 140 个待放置。新旧版各 7 页以及真实《小步舞曲》各 1 页通过 Poppler 独立栅格检查。标注框重叠、框外变更、原深色墨迹覆盖、黑色混合异常及已放置标注缺失均为零。检查对象为布局和导出，音高沿用原识谱结果。

- [整份新版示例 PDF](../output/validation/chord-readability/snow-halation/annotated-v2.pdf)
- [旧版局部](../output/validation/chord-readability/chords-before.png) 与 [新版局部](../output/validation/chord-readability/chords-after.png)
- [布局对比基线](../output/validation/chord-readability/baseline.json) 与 [新版逐页数据](../output/validation/chord-readability/final-pages.json)
- [密集第 2 页独立导出检查](../output/validation/chord-readability/snow-halation/page-2/revisions/2/verification.json)

76 项 Python 测试及前端布局、类型和变更文件的 ESLint 检查通过。实际 AppHost 和 Edge 浏览器中，上传真实《小步舞曲》后用「重新计算标注」生成 v2。曲谱数据一致，201 个标注全部放置，下载 PDF 与 Worker 产物的 SHA-256 一致。浏览器另用一音至八音的 SVG 用例验证字号递减、整组共列、无重叠及八度点完整。

- [浏览器下载的 PDF](../output/validation/chord-readability/minuet-browser-v2.pdf)
- [重新计算后的工作台](../output/playwright/chord-readability-workbench.png)

## 2026-10-24：按音符数量分级

初始字号乘以 `1 / (1 + 0.18 × (音符数 − 1))`。以同一谱表基准、留白足够为前提：

| 可见音符数 | 相对单音基准 | 单音基准为 8 pt 时 |
| --- | ---: | ---: |
| 1 | 100% | 8 pt |
| 2 | 84.75% | 6.78 pt |
| 3 | 73.53% | 5.88 pt |
| 4 | 64.94% | 5.19 pt |
| 5 | 58.14% | 4.65 pt |
| 6 | 52.63% | 4.21 pt |

数字、升降号和八度点共同缩小，同组自动字号一致。隐藏标注不计入堆叠数量，固定字号/手动位置成员仍计入可见和弦；显式固定字号本身不参与缩放。达到 3 pt 下限后不再缩小，像素取整也可能使相邻档位相同。单音在狭小空间仍可继续缩小，所以这里描述的是初始优先级，不保证不同留白位置的最终字号严格按数量排序。

Python 布局与前端重排使用相同曲线；原谱工作台继续显示服务器返回的实际字号。新修订记录规则版本，旧 PDF 产物不覆盖。

### 新一轮真实样本与验证

同一 Ninelie 第1页重新导出：481 个已放置、6 个待放置、56 / 58 组和弦完整放置，与上一版保持一致。布局数量相同时，字号层次有了明确区别：

| 和弦音符数 | 上一版已放置标注平均字号 | 新版已放置标注平均字号 | 新版最大字号 |
| --- | ---: | ---: | ---: |
| 1 | 4.626 pt | 4.626 pt | 4.67 pt |
| 2 | 4.643 pt | 3.984 pt | 4 pt |
| 3 | 4.481 pt | 3.333 pt | 3.33 pt |
| 4 | 4.167 pt | 3 pt | 3 pt |

独立 PDF 验证仍为原墨迹覆盖 0、标注区域外变化 0、标注重叠 0、不可见标注 0，音符与权威标签保持一致。已查看真实导出局部，结果属于布局验证，不代表识谱准确率。

55 项 Python 测试通过；新增验证充足留白下单音到八音逐级变小、隐藏成员不压缩单音，原有固定字号、手动位置和碰撞规则继续通过。前端布局规则、typecheck、ESLint 通过。真实 Edge 中，受控 SVG 的一音到八音全部放置、逐级递减、保持共列和八度点，未发生组内重叠。

真实小步舞曲专用任务 `9f3781cf-b60c-4ba8-a24f-4ec793d83f80` 验证「重新计算标注」：无草稿修改时由 v1 生成 v2，保存的完整 ScoreSnapshot 一致，新版规则为 `adaptive-chord-count-v2`，201 个标注全部放置。最初上传被过期 Java 编译产物拦截；执行现有 `scripts/setup/prepare.ps1 -WithOpenCV` 重新编译适配器、通过依赖检查后，同一任务重试成功。本次未修改 Java 源码。

- [新版真实 PDF](../output/chord-count-validation/ninelie-page1/revisions/2/annotated.pdf)
- [真实和弦局部](../output/chord-count-validation/ninelie-chords-detail.png)
- [字号分布对比](../output/chord-count-validation/ninelie-page1/chord-size-comparison.json)
- [独立导出检查](../output/chord-count-validation/ninelie-page1/revisions/2/verification.json)
- [浏览器分级验证](../output/chord-count-validation/browser-report.json)
- [重新计算入口验证](../output/chord-count-validation/recalculate-report.json) / [工作台截图](../output/playwright/chord-count-workbench.png)
- 浏览器复跑：`tests/e2e/chord-count-browser-check.js`；重新计算入口复跑：`tests/e2e/recalculate-layout-browser-check.js`。

以下保留 2026-10-23 第一版自适应实现与验收记录。

和弦作为整体保留同列与高低顺序，数字、升降号、八度点一起缩放。每档重测字形包围盒，检查原谱墨迹、其他标注、页面和谱表边界；无法完整容纳时先放宽搜索范围再试一次，仍无法容纳才标为待人工放置。用户指定的固定字号不会被缩小，手动位置也不会自动移动。

3 pt 属于密集区域的空间折中，不承诺所有情况下纸上都易读；需要较大字号时可手动指定，或使用宽松重排。重排对复杂 MusicXML 的既有导入限制仍保留。

## 实现

- C# `AnnotationStyle.Default` 与前端草稿默认值改为 `fontSizePoints: null`，接口仍使用现有可空字段，无新增依赖或数据库迁移。
- `scripts/runtime/annotate.py` 按真实 PDF 点/识谱像素换算初始大小及下限，按和弦整组搜索可行字号；显式的逐音自动设置可覆盖全页固定字号。
- 原谱工作台使用后端已经拟合的实际字号、字形框及八度点，避免保存后重新按谱线放大。草稿切换先预览初始大小，完整留白拟合在保存后进行。
- 重排在新 SVG 坐标系内缩放整组字形，保留最小物理字号、固定字号和避让约束；下载的 HTML 包含相同的缩放变换。
- 旧 PDF 修订保持不变，新的修订才使用新算法。没有修改 Audiveris 源码。

## 真实 Ninelie 第1页对比

复用任务 `5cc14e66-8795-4fb7-b991-2ff592c21852` 已验证的识谱结果和权威 C# 标签，在独立目录重新渲染两种样式，没有修改用户任务、音高、原谱或旧修订。

| 指标 | 固定 10 pt | 自适应 |
| --- | ---: | ---: |
| 已识别符头 | 487 | 487 |
| 已放置标注 | 341 | 481 |
| 待人工放置 | 146 | 6 |
| 完整放置和弦 / 已识别和弦 | 14 / 58 | 56 / 58 |
| 实际字号范围 | 10 pt | 3–4.67 pt |

两份 PDF 均通过独立 Poppler 栅格检查：标注区域外变化像素 0、原谱深色墨迹覆盖 0、标注重叠对 0、黑色混合异常 0、不可见的已放置标注 0；每个导出数字与权威标签一致。已查看两种导出的谱面局部。该比较衡量布局覆盖，不代表音高正确率或 OMR 召回率。

- [对比数据](../output/adaptive-font-validation/ninelie-page1/comparison.json)
- [自适应 PDF](../output/adaptive-font-validation/ninelie-page1/revisions/2/annotated.pdf)
- [自适应局部](../output/adaptive-font-validation/ninelie-page1/adaptive-detail.png) / [10 pt 基线局部](../output/adaptive-font-validation/ninelie-page1/fixed10-detail.png)
- [独立导出检查](../output/adaptive-font-validation/ninelie-page1/revisions/2/verification.json)

## 浏览器及回归

专用小步舞曲任务 `b6ae692d-ecb4-401a-ad1e-ae5e430d32fe` 在隔离 Edge 会话通过真实上传、默认自适应、固定 10 pt 保存、切回自适应保存、刷新恢复与 PDF 下载。最终 v7 的 201 个原谱标注尺寸与后端返回逐个一致，重排 201 个标注全部放置、全部在下方，浏览器页面错误 0。v7 导出的独立栅格保护、黑色及碰撞检查同样通过。初次测试的受控 SVG 留白过窄，连最低字号也容纳不下；修正夹具到可容纳缩小字形的范围后，完整浏览器脚本复跑通过。

受控的窄留白双音和弦 SVG 中，固定 10 pt 有 2 个待放置；自适应从 4 pt 缩为 3.2 pt，两个数字完整放在下方。这是布局回归，不是实际 OMR 准确率样本。

53 项 Python 测试、37 项领域测试通过。新增覆盖整组缩小、保留八度点、固定字号不缩、逐音自动覆盖、完全阻塞及手动位置。前端布局规则、契约、SDK 可复现、类型检查和 ESLint 通过。API / Worker 已重新构建并通过 Aspire 健康检查。

- [浏览器验证数据](../output/adaptive-font-validation/browser-report.json)
- [工作台截图](../output/playwright/adaptive-font-workbench.png)
- [浏览器下载的 PDF](../output/adaptive-font-validation/minuet-adaptive.pdf)
- 自动化入口：`tests/e2e/adaptive-font-browser-check.js`，仅对专用真实小步舞曲任务运行。

真实对比复跑（输出目录必须不存在）：

```powershell
.tools/poc-python/Scripts/python.exe tools/evaluation/adaptive-layout.py --job output/backend/5cc14e6687954fb7b9912ff592c21852 --output output/adaptive-font-validation/rerun
```

这次没有修复 Flower Dance 的 216 DPI 识谱入口失败，也没有重新识别整份 Ninelie；这两项不能计入字号调整的验收结果。

## 2026-11-23 二次搜索补排

首轮自适应排版结束后，对整组仍为 `needs-manual-placement` 的音符再做一次更宽松的组级搜索：上下搜索距离从 7 倍行距提高到 14 倍，左右从 2 倍提高到 6 倍，偏移档位从 2 档提高到 4 档，组内间距压到 1 像素，并允许在左右各 2 倍行距内放宽候选框。和弦仍整组移动，保持同列与高低顺序；不会拆开和弦，也不会跨到相邻谱表。成功时 `placementDirection` 记为 `fallback`，`metrics.json` 新增 `fallbackPlaced` 计数；`layoutPolicy` 仍为 `below-first-v1`。已放置标注、用户指定字号和手动位置不会被改动，二次搜索失败时仍保留待人工放置。

真实样本验证使用 `output/backend/296e1100daa64610a54dc6d62d84f0ec`（705 个音符，原 30 个待放置）。重新排版后 705 个标注全部放置，其中 2 个由二次搜索完成，标注重叠 0、出页 0，实际字号 7.333 pt。另两页样本分别把 235 个待放置降到 8 个、58 个降到 0 个。AppHost 实时上传同一页后，v1 与「重新计算标注」生成的 v2 都记录 `fallbackPlaced: 2`、`pending: 0`；v2 的 annotated PDF 用 Poppler 提取到 705 个文字，工作台显示 705 个音符且没有「处理未标注」入口。测试覆盖远端空白、和弦同列、不跨谱表和不移动已放置标注，Python 布局测试 26 项、全套 Python 测试 78 项通过。以上只验证布局覆盖，不代表 OMR 识别准确率。
