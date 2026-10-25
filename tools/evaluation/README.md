# 质量评估工具

此目录保留需真实样本、真实任务和独立证据的评估工具，不在默认单元测试中运行。

| 工具 | 用途 |
| --- | --- |
| `validate-opencv.py` | 真实 PDF / 扫描图的 OpenCV A/B 上传、识谱和导出 |
| `analyze-opencv.py` | 分析 A/B 空间差异及拒绝结果 |
| `probe-p3.py` | 对真实 PDF 逐页执行隔离识谱，保留失败现场 |
| `p4-quality-matrix.py` | 多曲目逐页 XML 关联 / 渲染器覆盖矩阵 |
| `validate-layout.py` | 已保存真实任务的下方优先布局、独立 PDF 栅格验收与合并审阅文件 |

从仓库根目录用项目 Python 执行，各工具支持 `--help`。真实 API 评估先确认 Aspire 资源健康，使用专用任务及忽略目录中的访问凭据。部分评估依赖阶段验收生成的样本 / 记录，输入要求见对应 `docs/` 报告。结果不等同于经人工真值验证的准确率。

依赖 Verovio npm 包的导入检查和最小 ID 探针放在 `MusicRecognition.Web/tools/evaluation/`，在前端目录运行，继续复用同一份 npm 依赖。
