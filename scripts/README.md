# 工程脚本

此目录只放正式运行工具与环境准备。测试 / 验收入口见 [tests/README.md](../tests/README.md)，质量评估见 [tools/evaluation/README.md](../tools/evaluation/README.md)。

| 目录 | 用途 |
| --- | --- |
| `runtime/` | Worker / API 调用的 Python 工具、PDF 坐标和标注模块、音高校验、Audiveris Java 适配器源码 |
| `setup/` | 项目 Python 环境、固定依赖、便携 Audiveris / JDK 准备、适配器编译和启动前检查 |
| `ops/` | Compose 停写备份、隔离恢复和默认只预览的过期数据清理 |

```powershell
# 初次准备；可复用自己的便携 Poppler
pwsh -File scripts/setup/setup.ps1 -PopplerDirectory C:/tools/poppler/Library
# 可选 OpenCV
pwsh -File scripts/setup/setup.ps1 -WithOpenCV
# 可选 homr：独立 Python 3.12、固定源码与模型，不修改原 Python 环境
pwsh -File scripts/setup/setup-homr.ps1
# 已装好依赖，只编译适配器并检查环境
pwsh -File scripts/setup/prepare.ps1
```

NuGet 依赖仍使用根目录集中管理；Python 基础 / 可选 OpenCV 清单分别为 `setup/requirements.txt`、`setup/requirements-opencv.txt`。`.tools/poc-python`、`.tools/poc-classes` 和 Java 包名 `musicrecognition.poc` 保留兼容，目录名称不代表测试依赖。处理上传文件时不下载或安装工具。

Windows 下，`prepare.ps1` 找不到项目副本和 PATH 中的 Poppler 时，会调用 `provision-poppler.ps1`，下载并验证固定 26.07.0 发布包（约 42 MB），恢复完整 bin / DLL / etc / share 到 `.tools/poppler`。也可直接运行 `pwsh -File scripts/setup/provision-poppler.ps1` 修复部分缺失的安装。此动作仅发生在显式环境准备期间，Worker 启动检查不自动下载工具。
