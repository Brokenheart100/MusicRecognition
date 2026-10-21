# 钢琴五线谱简谱辅助标注：设计方案

日期：2026-10-21。状态：设计基线；实现状态以阶段报告为准。

## 1. 产品目标与边界

用户上传钢琴五线谱 PDF / 图片，系统在原谱音符附近的空白处添加微小的简谱音高提示，支持校对、移动和导出 PDF。目标是帮助初学者读谱；节奏、奏法继续由五线谱表达。

首版支持清晰印刷体钢琴谱及 MusicXML / MXL；手写谱、严重倾斜照片、任意复杂谱面的全自动正确识别不属于首版承诺。识别结果必须能校对。

两种模式必须在界面与产物中明确区分：

| 模式 | 行为 | 约束 |
| --- | --- | --- |
| 原谱叠加（主要目标） | 保留原页面，只叠加辅助数字 | 需要音符语义与原页面坐标可靠关联 |
| 重新排版（可选） | MusicXML 经渲染器生成新谱，再加数字 | 可以增大间距，但不保证原分页和版式 |

禁止把重新排版的结果作为原谱叠加通过的证据。禁止把 MusicXML 的排版位置直接当作原 PDF 像素位置。

## 2. 技术栈与复用原则

| 能力 | 选型 | 说明 |
| --- | --- | --- |
| 前端 | Nuxt 4 + TypeScript | Nuxt 4 的页面、组件在 app/；server/ 保留在项目根目录 |
| UI | @nuxt/ui | 复用上传、表单、面板、提示、进度组件 |
| 编辑状态 | @pinia/nuxt | 选中音符、未保存修改、撤销重做 |
| 交互工具 | @vueuse/nuxt | 尺寸监听、快捷键、设置；按需引入 |
| 业务 API | ASP.NET Core 10 | 任务、乐谱、校对与权威简谱规则 |
| 编排 | Aspire | 编排 Nuxt、API、Worker、数据库和存储，统一可观测性 |
| OMR | Audiveris（隔离适配器） | 复用印刷乐谱识别与批处理，不训练自有模型 |
| 交换格式 | MusicXML / MXL | 音符语义交换；同时保留原文件和 .omr |
| 原谱预览 | PDF.js + SVG 标注层 | 图片输入也使用明确的页面坐标系 |
| 重新排版 | 优先验证 Verovio | MusicXML 转 MEI / SVG；音符 ID 映射需要适配验证 |
| PDF 加注 | 前端评估 pdf-lib | PoC 允许使用 Python PDF 工具独立验证几何链路 |
| 元数据 | PostgreSQL + EF Core | 任务、版本、修订、标注，不把大文件塞入业务表 |
| 文件 | 存储接口；本地目录起步 | 后续替换对象存储 |
| 持久后台任务 | 评估 Hangfire + PostgreSQL 扩展 | PostgreSQL provider 属于社区扩展；落实版本兼容与恢复验证 |

不因为 AppHost 已引用 Redis 就把它列为必须运行的依赖；按实际缓存需求引入。先做单业务系统与独立 Worker，不提前拆分身份、乐谱、转换等微服务。

PoC 是可替换的验证工具，不代表正式产品改用 Python；正式 C# 领域模型不依赖 PoC 工具。

## 3. 用户与处理流程

```mermaid
flowchart LR
  A[上传 PDF / 图片 / MusicXML] --> B[创建任务与保存原件]
  B --> C{输入类型}
  C -->|PDF / 图片| D[Audiveris 识别]
  C -->|MusicXML| E[语义解析]
  D --> E
  D --> F[原谱几何与符号关联]
  E --> G[简谱音高计算]
  F --> H[候选位置与避让]
  G --> H
  H --> I[预览与校对]
  I --> J[保存修订并导出]
```

识别为长任务：提交后返回任务 ID，浏览器首版轮询状态即可；确有需要再加实时推送。状态至少包含 Queued / Recognizing / Mapping / ReadyForReview / Exporting / Completed / Failed / Cancelled，阶段百分比不能伪装成总体准确进度。

样式变更和位置拖动只重新布局，不能重跑 OMR。音高纠错生成修订并更新受影响的标注。

## 4. 整洁架构

建议在现有 AppHost 旁逐步增加以下项目，不一次生成空壳：

```text
MusicRecognition.Domain          音高、调性、简谱规则、修订等纯领域模型
MusicRecognition.Application     用例与识别 / 存储 / 调度接口
MusicRecognition.Infrastructure  Audiveris、MusicXML、EF Core、文件与任务适配
MusicRecognition.Api             HTTP 接口、校验、错误响应与组合根
MusicRecognition.Worker          耗时识别与文件处理
MusicRecognition.Web             Nuxt 4 工作台
MusicRecognition.AppHost         资源编排
MusicRecognition.ServiceDefaults 健康检查与遥测
```

依赖：Application -> Domain；Infrastructure -> Application；Api / Worker 在组合根注册具体实现。Domain 不依赖 EF Core、Nuxt、OMR、PDF 库。用功能用例组织 Application，避免通用仓储、无必要消息总线和重复封装框架。

Nuxt 负责显示与交互；必要的 server 路由只做同源代理，业务校验与简谱计算不复制到 Nitro。几何布局可做纯 TypeScript 模块，保持预览与导出共用同一份布局结果。

## 5. 数据与音高规则

核心数据分离为：原文件、识别版本、规范化音符、原谱几何、辅助标注、人工修订、导出版本。每个音符分配稳定的业务 NoteId，另存 Audiveris InterId / MusicXML 定位等来源标识；引擎重跑后的 ID 不默认稳定。

音符至少保留 Step / Alter / Octave、乐谱版本、小节、谱表、声部、拍位置、时值、和弦关系、延音关系。不能只存 MIDI 值，必须保留升降音拼写。MusicXML pitch/alter 与实际显示的 accidental 含义不同，不能重复应用调号或临时记号。

首版标注组成是数字 1..7、变化音、高低八度点。数字不是指法；界面与图例必须说明。

2026-10-21 用户明确要求黑色：PDF 与 SVG 的附加数字、变化音、八度点统一使用纯黑 `#000000`，JSON 记录该颜色。诊断用符头框与界面链接不属于导出标注。

字号按用户反馈增大一档：默认 `max(15, round(interline))` 原识别图像素，当前 PDF 样谱由 4 pt 改为 5 pt，扫描样谱约由 4.30 pt 改为 5.23 pt。升降号、八度点与数字一起调整；不能为了提高自动放置率偷偷缩小字号。

| 模式 | 规则 |
| --- | --- |
| 首调 | 根据明确的主音计算音级，显示如 1=G；需要明确小调约定与段落转调 |
| 固定 C 对照 | C=1，D=2，E=3，F=4，G=5，A=6，B=7；变化音单独表示 |

产品默认首调，但调性不明确时要求选择；PoC 可以明确使用固定 C 模式，避免把调性推断与坐标验证混为一谈。C4 为固定 C 模式的无八度点基准。验证集必须覆盖双谱表、和弦、变化音、八度、跨谱表和多声部，未覆盖能力逐项标出。

## 6. 原谱坐标与标注布局

原谱叠加必须保存 PDF MediaBox / CropBox / rotation、识别用图尺寸、预处理变换和逆变换。识别图像素、PDF points、浏览器 CSS pixels 是三个不同的坐标系。

PoC 初始只支持原扫描图。目前已扩展为单页 PDF、默认 UserUnit=1、CropBox 位于 MediaBox 内、`/Rotate` 为 0/90/180/270 的输入，真实样谱矩阵验证见 P0 报告。源图制作 PDF 与直接在真实原 PDF 叠加仍须分别报告。

真实 PDF 先保存原件，临时副本只去掉页面旋转，以 Poppler 固定 216 DPI 渲染 CropBox 供 OMR 使用。设裁剪框为 `(L,B,R,T)`，识别图左上角为 `(0,0)`，则 PDF 原始用户空间坐标为 `X=L+x*72/DPI, Y=T-y*72/DPI`。栅格宽高向上取整产生边缘补齐，不能据此反推缩放率。导出只向原页增加矢量标注，保留原 `/Rotate`、MediaBox、CropBox 和原绘图指令；显示坐标另按页面旋转映射。

当前没有验证内容流内部任意角度旋转、照片方向自动纠正、裁穿符号后的识别、多页和非默认 UserUnit。超出条件要拒绝或明确标为未验收。

原件 -> 识别图 -> 引擎坐标 -> 页面坐标的每一步都要记录，尤其验证引擎是否做了 deskew / scaling。MusicXML 与 .omr 的关联必须来自可检查的标识或结构；靠音符顺序凑对不能作为可靠证据。

布局采用少量候选位置与评分：距符头近、避免遮挡印刷符号、避免与其他标注碰撞、避免越过谱表造成归属歧义。优先使用实际墨迹或符号边界检测。和弦按音高成组布局；确无空白则标记需人工调整，不能无限缩小字体。

保存的是页面坐标或锚点相对偏移；缩放重新投影。导出共用同一份最终标注几何，包含数字、升降号与八度点，不能仅导出一个替代字符串。

## 7. 工作台与文件处理

页面采用缩略图 / 乐谱 / 设置三栏；支持原谱对照、逐音纠错、拖动、隐藏、撤销重做。高低谱表与声部应按乐谱结构选择，不能简单当成左右手的恒等映射。

上传校验类型、文件大小、页数与解压体积；识别进程使用隔离临时目录、参数数组、超时与并发限制。XML 禁用外部实体，MXL 解压限制路径与大小。任务重试幂等，识别产物保留版本。文件下载受资源归属约束；匿名试用可用短期任务凭据，不用可枚举文件路径。

## 8. 验证与证据分级

必须分别报告：能识别、能取坐标、能关联音高、能排布、能导出、音高正确率、人工可用性。坐标和数量一致不等于音乐语义正确。未提供人工 ground truth 时不能报告识别准确率。

每次 PoC 保存：输入来源与 SHA-256、工具版本、精确命令、stdout/stderr、.omr、.mxl、标注 JSON、原图 / 结果图、结果 PDF、校验指标及结论。

## 9. 官方资料

### OpenCV 的候选职责（尚未接入 / 未证明精度提升）

OpenCV 适合在 Infrastructure 的图像处理适配器中补充局部自适应阈值、去噪、细线保护区域、倾斜检测与透视校正。对光照不均、斜拍、背景噪声可做原图 / 预处理图 A/B 验证；不能仅凭加库便声称音高识别更准，也不能替代处理谱号、调号、和弦、连音等语义的 OMR 引擎。

若增加几何预处理，必须保存变换矩阵及逆矩阵，将预处理后符头锚点映回原扫描图 / 原 PDF；不能把变换后图像坐标直接叠到原件。对当前矢量 PDF，坐标变换误差已很小，OpenCV 不会直接改善 PDF 用户空间的数学映射。优先用它验证扫描 / 照片质量和标注避让，而不是重写识别引擎。

A/B 需要相同样本、独立音高真值、漏检 / 误检、原图锚点偏差、墨迹遮挡及运行耗时。自适应阈值可能误删细小记号，去五线谱操作也可能伤及符干，必须保留原图与回归样本。目前未安装 OpenCV，未更改识别链路。

- OpenCV 自适应阈值：https://docs.opencv.org/4.x/d7/d4d/tutorial_py_thresholding.html
- OpenCV 形态学：https://docs.opencv.org/4.x/d9/d61/tutorial_py_morphological_ops.html
- OpenCV 仿射 / 透视变换：https://docs.opencv.org/4.x/da/d6e/tutorial_py_geometric_transformations.html

### 现有技术资料

- Nuxt 4：https://nuxt.com/docs/4.x/getting-started/introduction
- Nuxt UI：https://nuxt.com/modules/ui
- Pinia：https://nuxt.com/modules/pinia
- VueUse：https://nuxt.com/modules/vueuse
- Aspire：https://aspire.dev/get-started/what-is-aspire/
- Aspire JavaScript / Nuxt：https://aspire.dev/deployment/javascript-apps/
- Audiveris handbook：https://audiveris.github.io/audiveris/_pages/handbook/
- Audiveris 导出有损性：https://audiveris.github.io/audiveris/_pages/tutorials/quick/export/
- Audiveris Book structure：https://github.com/Audiveris/audiveris/wiki/Book-Structure
- MusicXML pitch：https://www.w3.org/2021/06/musicxml40/musicxml-reference/elements/pitch/
- MusicXML accidental：https://www.w3.org/2021/06/musicxml40/musicxml-reference/elements/accidental/
- Verovio：https://book.verovio.org/advanced-topics/internal-structure.html
- PDF.js：https://mozilla.github.io/pdf.js/
- pdf-lib：https://pdf-lib.js.org/
- Hangfire 社区扩展：https://www.hangfire.io/extensions.html
