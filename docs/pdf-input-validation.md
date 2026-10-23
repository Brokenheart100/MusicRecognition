# 六页 PDF 被单页管线拒绝：原因与输入提示修复

日期：2026-10-22。用户报告任务 `bf1b0214-d360-46e0-b95d-62992e71ee4c` 出现通用 `ToolFailure`。

## 原因

实际原件未加密、共 6 页。失败发生在 `pdf_geometry.read_page` 的单页限制检查，在 Audiveris 启动之前；不是 Pillow 缺失，也不是 OpenCV 预处理或音高一致性问题。原日志将“加密或不是单页”合并为一个 ValueError，管线归为 invalid-source，导致用户只看到“consult the protected server logs”。

P2 仍只支持一次处理一页；本次修复输入反馈，不把多页 PDF 截成第一页当作整份处理成功。多页任务、分页校对与整份导出仍属于 P3。

## 修复

- 浏览器复用现有 PDF.js，在提交前检查页数，6 页文件明确显示“这份 PDF 有 6 页。目前一次支持 1 页，请先拆分出需要标注的一页再上传。”并停止上传。加密 / 无法解析的文件也有对应提示，PDF worker 在结束后释放。
- 后台继续独立验证原文件，分别返回 `unsupported-page-count` 与 `encrypted-pdf`。C# 只映射已知、安全的用户说明，不把任意工具日志或绝对路径返回客户端。
- 新的失败任务显示对应说明与“选择单页乐谱”，不再对这些输入显示无效的重复识别操作。
- 当前编辑任务与轮询在新文件校验失败时保持原状态。没有修改原失败任务的原件、历史状态和日志。

## 验证

- 对实际 6 页文件执行浏览器检查：提示页数为 6，上传 POST 次数为 **0**。
- 绕过前端直接提交同一原件，验证新任务 `44b3ca05-f70a-4fd7-8068-99ea76d3afc0` 为 Failed、CurrentRevision=0，错误码 `unsupported-page-count`，说明明确要求单页，无 raw / 修订 PDF 发布。
- 从页面重新打开单页《小步舞曲》，实际识别 / 导出就绪，201 个音符；任务 `8a8b0f8b-cb44-4110-ae35-1d3b35fdecf7`。浏览器 console error / warning 为 0。
- Python 测试 8 / 8：新增页数区分、加密文件先行拒绝、真实单页回归，保留既有 5 个预处理契约测试。Nuxt typecheck、生产构建、Worker 资源重新构建通过；Worker 构建 0 warnings / 0 errors。

证据：`output/pdf-input-validation/browser-result.json`、`backend-result.json`、`single-page-browser-result.json`。目录中的 `six-pages.json` 含本机测试能力凭据，不公开分享。原任务旧日志保持原有错误；重新提交会按新校验处理。

[上传前的明确提示](../output/playwright/six-page-pdf-warning.png)
