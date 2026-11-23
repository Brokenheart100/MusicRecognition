# MusicXML 网页渲染试用

日期：2026-10-23。

引入 `opensheetmusicdisplay@2.2.1`（BSD-3-Clause），使用其 SVG 渲染后端。在已识别的乐谱中点击「MusicXML 预览」，可查看当前页的 MusicXML 五线谱；支持逐页切换和 50%–200% 缩放，小屏使用横向滚动。点击「原谱标注」回到 PDF 校对界面。

## 数据流程

Audiveris 已经产生逐页 `raw/score.mxl`。新增 `GET /api/scores/{id}/musicxml` 资源，通过原有 X-Score-Token 访问验证，并要求页任务 Ready；文档父任务返回 409，要求选择具体页。Nuxt 代理同步增加这个明确路径，继续拒绝任意文件路径。原始工具日志和 OMR 工作文件不通过该接口开放。

前端使用现有 hey-api SDK 请求 Blob，TanStack Query 管理读取状态；OSMD 在客户端按需动态导入。关闭预览会清理渲染对象并取消、清除该预览的缓存。ResizeObserver 随组件释放，避免重复切换后积累窗口事件。

官方接口参考：[OSMD load/render](https://opensheetmusicdisplay.github.io/classdoc/classes/OpenSheetMusicDisplay.html)；具体配置和 Blob 输入已核对安装包声明文件。

## 当前范围

预览展示的是识谱时生成的 MusicXML，尚未把 C# 修订、浏览器未保存草稿、简谱数字或标注样式写回 XML。它不是所选历史修订的重新渲染版本；原 PDF 下载继续使用原谱叠加流程。界面显示这项限制，MusicXML 视图隐藏原谱的拖动、谱表筛选和音符编辑面板。

MusicXML 的音符数量受 OMR 导出限制。例如这份三页样本保存 827 个符头，其中 803 个能匹配 MusicXML，24 个节奏未确定的符头仅保留在原谱校对模型中。MusicXML 排版不会补回这些音符，也不代表识谱精度提高。

## 验证

使用已完成识谱的《My Heart Will Go On》三页真实结果，任务 `32f7df19-0dff-4aac-82c6-3ed971899a07`，没有为渲染重新执行 OMR。

- 浏览器验证三页逐页 SVG 渲染、切回 PDF、再次打开预览、缩放和 390×844 小屏布局，脚本 `tests/e2e/osmd-browser-check.js`，页面运行异常为 0。
- 桌面截图 `output/playwright/musicxml-desktop.png`；小屏截图 `output/playwright/musicxml-mobile.png`。识谱结果的分行、标题和声部会按 XML 重新渲染，原 PDF 版式不会被复制。
- 真实 API：授权页 200、错误访问令牌 404、父文档请求 409、未知产物名称 404。证据 `output/osmd-validation/api-checks.json`。
- 前端 TypeScript、ESLint 和 Nuxt 生产构建通过；API 通过 Aspire 启动和健康检查。
- OSMD 对本样本输出 `Not enough lines for SkyBottomLine calculation` 排版警告，已能显示乐谱，未把此结果称为无警告或全曲语义验收。

依赖审计仍报告 11 项 high，当前报告落在 Nuxt/Nitro 的依赖链，未报告 OSMD 漏洞；没有使用强制升级或豁免。本次完成的是本地渲染试用，生产发布仍应处理项目已有的审计问题。

下一步若认可渲染效果，再设计稳定音符 ID 与 C# 校对修订到 MusicXML 的映射。
