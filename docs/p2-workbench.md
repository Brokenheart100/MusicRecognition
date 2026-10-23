# P2：谱间 · 一页钢琴谱工作台

日期：2026-10-22。Nuxt 4 工作台已接入真实 P1 后端；设计与本地验证记录如下。多页、重新排版和生产部署仍按后续阶段执行。

后续补充：已修复重启后误用系统 Python 缺包的问题，并增加默认关闭的 OpenCV 光照增强入口。真实样本六组 A/B 为四组成功、两组增强扫描失败，未证明精度提升；详见 [Python 修复与 OpenCV 验证](opencv-validation.md)。以下保留原 P2 验收记录。

输入反馈补充：6 页 PDF 原先只返回通用 ToolFailure，现增加上传前页数检查及后台明确错误码；单页流程已回归验证。[问题与验证记录](pdf-input-validation.md)。

## 视觉与交互

以现代 iOS 风格为方向：浅雾白背景、通透侧栏、细边框、柔和阴影、克制的蓝紫色操作区和宽松留白。原谱使用独立白纸承载，不给谱面加彩色滤镜。黑色标注继续采用当前 5 pt 左右字号，并允许对单个或框选的一组标注单独改字号与颜色；蓝色选择框与框选矩形只属于校对界面，不写入 PDF。

左侧收纳本机乐谱记录，中间是原谱，右侧是音符与调性面板。手机使用可收起的侧栏和校对面板，缩放后的溢出限制在谱面容器内部。空状态提供真实《小步舞曲》示例，示例按钮走同一套上传、识谱、修订和导出流程，没有预设假的完成进度或准确率。

- 上传一页 PDF / JPG / PNG，显示实际排队、识别、排版、完成、失败、取消状态。
- PDF.js 绘制原 PDF，SVG 放置黑色辅助音符及可选择的符头区域；扫描图直接作为底图。
- 点击音符，校正音名、变化音和八度。音高业务规则仍由 C# 计算，未保存音高的字形会在保存、排版完成后更新。
- 拖动数字、点击空白放置、方向键微调、隐藏 / 显示、恢复自动布局。
- 在谱面空白处按住左键框选一组标注；Shift / Ctrl 追加选择，单击空白取消。右侧“标注样式”随之只作用于选中项，可改字号与颜色，或“恢复跟随全页样式”。
- 支持撤销 / 重做、50 步编辑历史、修订保存、版本查看。历史版本只读，回到最新版后继续编辑。
- 谱表与声部筛选用于校对视图；导出仍包含全部谱表，单独隐藏的标注随修订保存。
- 保存版本与待提交草稿分开管理。保存后刷新可恢复版本，未保存草稿也保存在本机，刷新前有提示。

快捷键：J / K 前后选择音符；方向键移动一个识别像素，Shift 移动十个；Delete 切换标注隐藏；Ctrl / Cmd S 保存；Ctrl / Cmd Z 撤销，Shift Ctrl / Cmd Z 或 Ctrl Y 重做；Ctrl / Cmd K 打开指南；Escape 取消选择。表单与弹层的键盘操作不会被音符移动快捷键劫持。

## 实现边界

`MusicRecognition.Web/app/` 为 Nuxt 4 页面、组件与 Pinia 状态；`shared/types/` 对应公开 API 契约；`server/api/` 为同源 Nitro 转发层。Nuxt UI 提供按钮、选择框、输入框、提示与对话框，VueUse 提供容器尺寸测量。前端依赖统一在 package.json 声明固定版本，并提交 package-lock.json；NuGet 继续沿用根目录 Directory.Packages.props。

浏览器 → Nuxt 同源 `/api/scores` → ASP.NET Core → PostgreSQL / Worker。BFF 只允许固定乐谱路由，后端地址来自私有 runtimeConfig，经 Aspire EndpointReference 注入；浏览器不能提供代理目标。任务访问使用现有 `X-Score-Token`，不用 query 携带凭据，Cookie / Authorization 不转发给后端。浏览器本机记录包含访问能力凭据；清除浏览器数据后当前没有凭据找回入口。

P2 对后端增加了 `AnnotationPlacement(Hidden, X, Y)`、`Note.Style` 与 RevisionRequest 的可选 `StyleChanges` 列表（`noteId` + `style`，`style: null` 表示清除覆盖并回到全页样式）。纠错和布局都进入不可变修订快照；旧 JSON 缺少 Placement 时仍按自动布局读取。没有改变数据库表结构或引入新队列；EF 检查无待迁移模型差异。

坐标仍是正向 CropBox 栅格像素。PDF 的 SVG viewBox 使用 CropBox 的**物理尺寸 × DPI**，而不是 ceil 后的栅格整数尺寸；旋转通过 SVG 矩阵映射，拖动通过 getScreenCTM 逆矩阵回到识别坐标，因此缩放和旋转不改变保存坐标。

后端用 FontTools 从当前 PDF 标注字体提取该页需要的数字 / 变化音轮廓。SVG 与 PDF 共用字形尺寸、基线偏移和八度点；前端无需依赖本机是否安装 Segoe UI Symbol。FontTools 已在 Python requirements.txt 固定为 4.67.0。PDF.js worker、标准字体及 WASM 从锁定的 npm 包复制到带版本的本地静态目录，开发和构建时自动准备，不使用远程 CDN，也不把 Vite HMR 客户端加载进 PDF worker。

手动位置先预留空间，再执行自动布局。手动标注越界、与原谱深色墨迹或其他标注冲突时，保持 `needs-manual-placement`，不绘制到 PDF；保存后的预览同样不画出这个冲突位置。隐藏标注使用独立 hidden 状态，不混入待放置数量。

## 启动

先按 [P1 文档](p1-backend.md) 配好 Python、Audiveris、JDK、Poppler 和字体。新增 Python 依赖需在同一识谱环境安装。

```powershell
& $env:MUSICRECOGNITION_PYTHON -m pip install -r scripts/poc/requirements.txt
npm ci --prefix MusicRecognition.Web
dotnet build MusicRecognition.slnx
aspire start --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire wait web --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire describe web --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
```

从 describe 获取 web 动态 URL，打开后选择文件或示例。前端 Node 使用本次已验证的 24.21.0 / npm 11.19.0；依赖安装和 build 会自动复制 PDF.js 静态资源。AppHost 的 web 使用 Nuxt dev；单独预览构建包时需设置 `NUXT_API_BASE` 为实际 API URL。

```powershell
npm run typecheck --prefix MusicRecognition.Web
npm run build --prefix MusicRecognition.Web
dotnet test tests/MusicRecognition.Domain.Tests
npm audit --prefix MusicRecognition.Web
```

## 本地验收结果

环境：Windows、Edge 实际浏览器、Nuxt 4.6.2 / Nuxt UI 4.12.0 / PDF.js 6.4.299；实际 PostgreSQL、ASP.NET Core 与 Audiveris。手机尺寸属于浏览器视口模拟，未实测 iPhone / Safari。

| 检查 | 结果 |
| --- | --- |
| 类型与构建 | Nuxt typecheck、Node server 生产构建通过；.NET 构建 0 warnings / 0 errors |
| 领域测试 | 25 / 25，通过既有音高 / 几何规则及新增位置边界检查 |
| 浏览器真实上传 | 示例从页面提交，识别出 201 个符头，200 个放置、1 个待放置 |
| 纠错与持久修订 | 从页面把一个 C5 改成 D5，拖动位置并隐藏另一个标注，保存 Revision 2，刷新后恢复，实际下载 PDF |
| 撤销与键盘 | UI 撤销 / 重做、方向键、键盘撤销 / 重做、选择框聚焦时 Ctrl S、快捷键指南已执行 |
| 草稿 | 未保存的方向键移动经刷新恢复，保存后草稿被清理 |
| 碰撞恢复 | Revision 3 故意把标注拖到墨迹上，成为待放置且预览不画；Revision 4 恢复自动布局后可见、可下载 |
| 布局筛选 | 谱表 / 声部选项从实际音符生成，浏览器完成筛选与恢复全部 |
| 响应式 | 1440 / 1024 / 768 / 390 px 均未发生整页横向溢出 |
| 逐音符样式 | 框选 / Shift 多选在 store 与画布间同步；`Note.Style` 经 C# labels 传入 annotate：实测 3 个音符覆盖后分别为 10 pt 红、10 pt 红、5 pt 蓝，其余 210 个仍为默认 15 px 黑；Python 29 / 29 通过 |
| 刷新与水合 | 刷新恢复保存版本；修复后的冷启动页面与完整流程无浏览器 console error / hydration warning |

主任务 `4948386b-f396-41c4-8e8f-33aa6942fb83`。Revision 2 的手动位置为 `(1704,291)`，Revision 3 墨迹冲突时：198 placed + 2 pending + 1 hidden；Revision 4：199 placed + 1 pending + 1 hidden，总数始终 201。这里的放置数量不是音高准确率。

独立几何核对使用 **PDF.js 实际 viewport.transform** 作为参照，与 SVG 的屏幕矩阵比较全部 201 个符头中心，而不是只检查 SVG 自身缩放。最大误差为 CSS 像素：

| 实际 PDF | 100% | 150% | 200% |
| --- | ---: | ---: | ---: |
| 原始小步舞曲 | 0.0031 | 0.0081 | 0.0061 |
| 裁剪 + 非零 MediaBox 起点 + 270° | 0.0045 | 0.0109 | 0.0083 |

组合样本任务 `433533cb-d66a-434a-b5cc-71e7876afb46`，CropBox `[49.25,53.5,620.75,806.25]`。没有把 P0 的全部九个案例重新宣称为 P2 浏览器验收。

Revision 2 的实际 PDF 经 Poppler 独立渲染复核：10,384 个变化像素，预留区域外变化 / 原墨迹覆盖 / 重叠标注 / 不可见放置标注均为 0，黑色混合不一致为 0。最终 Revision 4 同样通过，变化像素 10,381；故意冲突的 Revision 3 也确认未覆盖原墨迹。数字、变化音、八度点和 NoteId 与 C# labels 相符。原绘图操作与页面几何保留，浏览器下载的最终 PDF 字节与服务端对应修订一致。

可检查证据：

- `output/validation/p2-full-geometry.json`、`p2-crop-geometry.json`、`p2-responsive.json`。
- `output/validation/p2-summary.json` 汇总实际文件检查与浏览器几何数据。
- `output/backend/4948386bf39641c48e8f33aa6942fb83/revisions/2/verification.json`，以及各修订 annotations / metrics。
- [桌面入口](../output/playwright/p2-welcome-desktop.png)、[桌面校对](../output/playwright/p2-editor-1440.png)、[手机校对](../output/playwright/p2-editor-390.png)。
- [页面下载的 Revision 4 PDF](../output/pdf/p2-minuet-reviewed-r4.pdf)。此样本含人为测试改音，不能当作教学校订版。

复跑浏览器使用 Playwright CLI，而非另建测试框架：`scripts/p2-browser-acceptance.js` 从新示例 Revision 1 开始；`p2-editor-check.js` 接着检查 Revision 2；`p2-geometry-check.js` 检查当前 PDF；`p2-responsive-check.js` 检查尺寸。用 `playwright-cli run-code --filename ...` 执行；本机没有 Chrome，验证使用 `--browser msedge`。脚本依赖真实后端和工具，运行会创建真实任务 / 修订。

## 审计与未覆盖项

新前端运行 `npm audit` 后，有兼容修复的低危 esbuild 问题已消除；仍报告 11 个 high 条目，根源为两类上游问题及其依赖传播：`braces` 的 [GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm)、`node-forge` 的 [GHSA-86w9-cpqp-85rv](https://github.com/advisories/GHSA-86w9-cpqp-85rv)。当前依赖链没有兼容的 audit 自动修复，建议的 force 操作会换成 Nuxt 4.1.3；本项目没有执行这种回退或隐藏审计结果。该状态须在生产发布前重新核查和解决，不把构建通过当作依赖审计通过。

未执行真实 iOS / Safari、触屏拖动、全浏览器可访问性审计、整页音高人工真值、多页、重新排版、容量、强杀恢复、账号 / TTL、部署与备份恢复验收。P1 复杂《圣母颂》的一致性失败仍保持失败。P3 继续多页、导出与布局质量扩展。
