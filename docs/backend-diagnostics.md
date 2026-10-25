# 后端中文诊断日志

2026-10-23：项目自身的上传、调度、拆页、识谱、标注导出、整份合并、重排、依赖检查与失败日志改为中文。

任务消息以 `文件名.pdf - 第1页` 开头；拆页前或整份合并显示 `文件名.pdf - 整份文档`。保留 UUID 作为追踪依据，结构化属性包括 `ScoreName`、`FileName`、`DocumentId`、`ScoreId`、`PageNumber`，按操作附带修订、错误码及耗时。运行中仍每 20 秒记录一次进度。

旧拆页任务即使文件名为 `Page 1.pdf`，也通过父任务读取原 PDF 文件名，不需要重新上传或修改数据库。新拆页任务保留原文件名，页码由独立字段表示。文件名去除路径和控制字符，防止日志换行污染。

底层 Audiveris / Python 的原始输出、异常堆栈、错误码和第三方框架日志保留原文；中文日志说明处理阶段，原始诊断用于定位问题。访问凭据不进入日志。

在 Aspire 的 worker 结构化日志中搜索文件名即可关联各页，也可按属性中的任务 ID / 原文档 ID 追踪：

```powershell
aspire otel logs worker --search '小步舞曲.pdf' --limit 50 --non-interactive
aspire otel logs api --search '小步舞曲.pdf' --limit 50 --non-interactive
```

12 项 Infrastructure 测试通过，涵盖原父文件名恢复、整份 / 单页命名、日志控制字符处理、真实 Python 失败诊断与依赖缺失检查；实际 Aspire 验证证据位于 `output/diagnostic-log-validation/`。中文文件名小步舞曲单页任务识别 201 个符头，标注导出与重排准备成功。

双页验证使用同一公开小步舞曲页复制成两页，以 `中文日志验证-双页小步舞曲.pdf` 上传。两页均识别及标注导出完成；Aspire 实际日志分别显示“第1页”和“第2页”，结构化属性中的原文件名、共同的原文档 ID 和不同的页面任务 ID 均已核对，见 `summary.json`、`worker-multipage.json`。API / Worker 已重新构建，均通过健康检查。
