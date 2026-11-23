# homr 可选引擎接入与真实样本 A/B 验证

日期：2026-10-31。Audiveris 保持默认；homr 为实验可选项。

## 设计与实施范围

1. 原谱上传增加任务级 `Engine=Audiveris/Homr`，默认兼容历史任务。保存到数据库、逐页任务继承，引擎参与幂等和识谱缓存匹配。切换引擎需创建新任务，不修改历史识别。
2. homr 使用独立 Python 3.12.16、固定源码提交、CPU ONNX 模型与独立依赖清单。安装和模型下载仅在准备脚本执行，运行中只核验本地文件。C# 继续通过既有 CliWrap、超时/取消和中文日志管理整个工具进程树。
3. 封装固定版本公开源码中的检测、逆坐标映射、序列识别和 MusicXML 写出流程，不修改上游源码。保留模型 XML、注意力点和分割符头；保守的一对一近邻关联仅使用无歧义候选，未可靠关联的音符不自动放置简谱，允许手动放置。
4. MusicXML 按原始 part/measure/音符序号、音高、声部、谱表校验，坐标独立校验。该校验只证明适配器没有改写模型结果，不证明原谱音高真值。八度线不作猜测修复。
5. A/B 使用小步舞曲、Flower Dance、原六页 PDF、两张 Orange 截图，保存原件哈希、逐页状态、音符与谱表数量、音高差异、坐标关联率、耗时及失败原因。没有人工真值时不把两引擎的一致率称为准确率。

阶段顺序：固定依赖与模型 → 适配器和回归测试 → 后端契约/迁移与工作台选择 → 真实样本 A/B 与导出 → 报告。

## 准备

`pwsh -File scripts/setup/setup-homr.ps1`。可通过 `Recognition:HomrPython` 指定独立解释器；默认 `.tools/homr/venv`。固定版本与模型摘要见 `scripts/setup/homr.lock.json`、完整 Python 依赖见 `scripts/setup/requirements-homr.txt`。未准备 homr 时，Audiveris 任务继续工作；选择 homr 的任务返回专门诊断，不在请求期间安装依赖。

homr 的 AGPL 和模型训练数据限制见 [官方许可说明](https://github.com/liebharc/homr/blob/2d7e8c3b500ab9af10e579bebab8c0d9e551eb71/LICENSE_FAQ.md)。本次独立接入和验证不构成商业许可确认。

本次固定源码为 `2d7e8c3b500ab9af10e579bebab8c0d9e551eb71`，安装版本 `0.7.0.post110+2d7e8c3`。uv 0.13.6 管理项目内解释器和缓存，三个 CPU ONNX 模型均验证下载归档和解压文件的 SHA256；重复执行准备脚本通过依赖和模型检查。关闭 GPU / CoreML、标题 OCR、调试缓存，避免在任务期间下载其他模型。本次仅验证 Windows 本机环境；现有 Compose 镜像仍只预置 Audiveris，没有把本机 venv 复制进 Linux 镜像。

自定义 `Recognition:HomrPython` 时仍需保留 `homr/venv` 与同级 `homr/runtime.json` 的准备布局。配置值为服务端路径，不接受客户端上传解释器路径。

## 接入边界与兼容

- Application 声明引擎枚举和任务参数；Infrastructure 管理独立解释器、进程生命周期和存储；Domain 不依赖 homr、Python 或 ONNX。新增 `Jobs.Engine` 迁移，默认值 0 对应 Audiveris，历史任务无需重识别。
- 原谱导入入口可选择引擎；引擎保存后不可变，六页 PDF 子任务继承父任务。前端通过 OpenAPI 生成 SDK / 类型 / Zod，没有维护另一套 API 数据结构。
- `homr_runner.py` 仅在独立环境中导入固定版本 homr；主环境的 `homr_adapter.py` 读取产物，不安装 homr。适配器使用上游逆坐标映射，将检测框与 XML `imgpos` 注意力点对应原始输入图。
- 只有独占注意力点、足够近且与次近候选有间隔的符头才一对一关联。关联只是几何启发式，不能证明音高正确。重复注意力点和歧义候选保留为近似坐标，自动标注状态为 `needs-manual-placement / anchor-review`；显式手动放置仍通过原有碰撞检查。
- PDF 继续使用已有 Poppler、旋转 / CropBox 坐标和原始绘图叠加流程；homr 不替代 PDF 层。默认仍为黑色、自适应字号。未安装独立环境时，homr 任务返回专门的准备提示，不影响默认 Audiveris 启动。

## 复跑与证据

1. 运行准备脚本，启动已有 Aspire 项目并 `aspire wait api`、`aspire wait worker`。
2. 准备 `output/homr-ab/samples.json`，每项为 `name/path`；不要更改真实样本内容。
3. `python tests/e2e/validate-homr-ab.py --url <API地址> --samples output/homr-ab/samples.json --output output/homr-ab/live`，创建专用任务并等待识别、逐页导出、整份合并。失败页面也进入结果，程序不把失败样本排除掉。
4. `python tests/acceptance/verify-homr-ab.py --results output/homr-ab/live/results.json --samples output/homr-ab/samples.json`，复用独立 Poppler 渲染检查，核验 C# 业务标注、黑色新增像素、原谱墨迹、碰撞及合并页序。

所有访问凭据、模型、运行日志和产物放在被忽略的 `.tools/`、`output/` 中；不要提交 access.json。本次最终 A/B 结果为 `output/homr-ab/live-v2/results.json`，逐页产物在同目录的 `样本/引擎/页号/`，整份 PDF 为 `样本/引擎/annotated-document.pdf`。`live/` 保留首轮记录；修正适配器后重新创建全部 homr 任务，Audiveris 复用同一批已完成的基线任务。

## 验证结果

### 输入与实测范围

Windows 本机 Aspire API / Worker、PostgreSQL，CPU 为 i5-12600KF（10 核 / 16 线程）。原环境保持 Python 3.14.7 / NumPy 2.3.5；homr 独立环境为 Python 3.12.16 / NumPy 2.5.4 / ONNX Runtime 1.31.1。上游源码检出仍为固定提交且无修改。

| 样本 | 页数 | 原件 SHA256 |
| --- | ---: | --- |
| `MusicRecognition.Web/public/samples/minuet.pdf` | 1 | `5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7` |
| `docs/Flower_Dance.pdf` | 3 | `429e1e1d908e7cbf0fa4eb3371a71fb01947e9744b131bdbfd7308a57514c587` |
| 之前的六页 PDF，原件 `output/backend/bf1b0214d36046e0b95d62992e71ee4c/source.pdf` | 6 | `23d4b3a203992fe7e30e25d29c1fc1669c3340e9377a71ce56746e462f301324` |
| Orange `00-00-10-484.png` 原截图 | 1 | `35dbbaadf5d378edf19118f735b5d2336b63017fcc78cf0fb51857342efb58cf` |
| Orange `00-00-16-572.png` 原截图 | 1 | `074c999d5f5f68076973fa47d3653b9a73e52b5d723a8af4d88507fc40cb44d6` |

主 A/B 共 12 对输入页、24 个引擎页结果。均使用 `preprocessing=None`，两种引擎共享既有 PDF 栅格化策略；不会为某个引擎单独放大或裁剪输入。

### 最终识别与标注

`XML / 锚点` 分别表示 pitched MusicXML 音符数与原谱标注条目数，二者都不是原谱真值。时间为各页“输入准备 + 引擎 + 适配校验”耗时之和，不含队列等待、标注渲染和整份合并。本机同时运行开发服务和验证工具，未做受控性能基准。

| 样本 | 引擎 | 识别成功页 | XML / 锚点 | 自动放置 | 待人工放置 | 处理秒数 | PDF 导出 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 小步舞曲 | Audiveris | 1/1 | 201 / 201 | 201 | 0 | 23.5 | 1 页 |
| 小步舞曲 | homr | 1/1 | 201 / 201 | 124 | 77 | 21.1 | 1 页 |
| Flower Dance | Audiveris | 0/3 | — | — | — | — | 未生成 |
| Flower Dance | homr | 3/3 | 2363 / 2363 | 790 | 1573 | 130.9 | 3 页整份合并 |
| 原六页 PDF | Audiveris | 6/6 | 2918 / 2927 | 2927 | 0 | 418.2 | 6 页整份合并 |
| 原六页 PDF | homr | 6/6 | 2969 / 2969 | 924 | 2045 | 188.9 | 6 页整份合并 |
| Orange 第 1 张 | Audiveris | 1/1 | 47 / 47 | 47 | 0 | 10.7 | 1 页 |
| Orange 第 1 张 | homr | 1/1 | 47 / 47 | 24 | 23 | 5.7 | 1 页 |
| Orange 第 2 张 | Audiveris | 1/1 | 149 / 149 | 144 | 5 | 13.0 | 1 页 |
| Orange 第 2 张 | homr | 1/1 | 145 / 145 | 33 | 112 | 9.3 | 1 页 |

Flower Dance 的 Audiveris 日志三页均为 `With a too low interline value of 10 pixels`，随后 `StepException: Sheet ignored`，退出码 1；这是引擎对输入栅格的谱线间距判定，不能仅凭它断言 PDF 文件自身的 DPI 很低。未修改 Audiveris 源码或阈值。homr 对同样页面有产出，但前两页有大量近似坐标，不能据此称为可直接打印练习的准确谱。

六页 homr 的 XML 音符数逐页为 `277, 418, 556, 526, 646, 546`；待人工放置为 `153, 277, 392, 374, 444, 405`。Audiveris 保留原有 9 个未被 XML 可靠绑定的节奏符头，因此锚点比 XML 多 9；原有八度证据修复流程保留。homr 未对缺失八度线擅自补偿。

homr 总近似坐标数为 3788；Flower Dance 另外有 42 个可靠关联的符头因留白 / 碰撞约束未能放置，因此总待放置数不等于近似坐标数。待放置标注没有写入 PDF，所有识别条目仍保留在编辑模型中。

### 两引擎差异与适用结论

以 `(step, alter, octave)` 多重集合交集除以较大音符数衡量**输出一致性**：小步舞曲、Orange 第 1 张均为 100%，Orange 第 2 张为 94.6%；六页依次为 `87.4%, 85.2%, 98.6%, 93.5%, 95.2%, 95.6%`。该指标忽略顺序、时刻、时值和声部，不能表示识谱准确率；即使 100% 也可能两边同时出错。Flower Dance 没有 Audiveris 成功结果，不能计算对应比较。

结论：homr 值得保留为扫描谱 / 截图片段的实验备选，在本次 Audiveris 拒绝的 Flower Dance 上可生成 MusicXML；但其原谱符头坐标关联覆盖明显较低，八度线也未解决，当前不足以替换 Audiveris 默认引擎。较适合先获取可编辑 MusicXML，再人工核对；原谱自动标注仍优先使用 Audiveris。

另外在该专用网页任务中，把一个近似锚点显式手动放置到测试留白区，保存为修订 2，放置数量由 124 增至 125，PDF 下载成功；独立 Poppler 检查通过，业务 NoteId 和 C# 标签保持一致。此操作只验证编辑与导出通路，不代表该测试位置是最终建议位置。证据为 `output/playwright/homr-review-audit.log`、`output/homr-ab/manual-review-verification.log`。

### 接入问题修复与回归

首轮 homr 识别完成，但标注导出因谱表边界被表示为标量而失败。原因是 homr 的钢琴上下谱表已合并，现有布局需要独立五线谱及上下边界的两个端点。已使用固定版本的 `Staff.split()` 拆开布局几何，保留原系统供模型识别；修正实际线距、边界端点、和弦分组，并在发布 raw 前校验格式。新增真实布局路径的无模型回归测试，随后重建全部 homr 任务。首轮失败产物保留在 `output/homr-ab/live/`，最终结果在 `live-v2/`。

领域测试 40/40、基础设施测试 25/25（包含真实超时 / 依赖检查）、Python 测试 73/73；前端 typecheck、契约、SDK 可复现检查、ESLint 和生产构建通过。模型安装重复恢复和摘要检查通过。当前 GitHub Actions 尚未推送或远程运行本次改动，不能把以前的绿色 CI 当成本次验收。

独立导出核验全部通过，证据为 `output/homr-ab/verification.json` 和 `output/homr-ab/verification-run.log`：

- 12 对输入页的 `engine-input.png` SHA256 完全相同，包括 Audiveris 失败的 Flower Dance，保证输入一致。
- 21 个成功识别页逐页独立渲染；标注区域外改变像素 0、原谱深色墨迹被覆盖像素 0、标注重叠对 0、黑色混合不一致像素 0，每个已放置标签均可见。
- 新增标注为黑色；每条导出标签匹配对应 C# 标签和业务 NoteId。
- Audiveris 六页、homr 六页、homr Flower Dance 三页共 3 份合并 PDF，页数、页序、MediaBox / CropBox / Rotation、背景图像摘要及原始绘图操作保持一致；合并页绘图与逐页修订导出相同。
- 上述核验验证导出完整性与适配一致性，没有人工全曲音高真值，不建立识谱准确率结论。

可直接查看本机最终产物：

- [homr 六页原谱标注 PDF](../output/homr-ab/live-v2/six-page/homr/annotated-document.pdf)
- [Audiveris 六页原谱标注 PDF](../output/homr-ab/live-v2/six-page/audiveris/annotated-document.pdf)
- [homr Flower Dance 三页原谱标注 PDF](../output/homr-ab/live-v2/flower-dance/homr/annotated-document.pdf)
- [homr 小步舞曲标注 PDF](../output/homr-ab/live-v2/minuet/homr/1/annotated.pdf)
