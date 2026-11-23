# 脚本与测试目录整理

2026-10-23。按运行、环境准备、测试、验收和评估分开，迁移 42 个现有文件；不变更识谱领域规则、接口或依赖版本。

```text
scripts/
  runtime/                  后端正式调用的 Python / Java 工具和共享模块
  setup/                    环境安装、固定依赖、工具准备和编译检查
tests/
  MusicRecognition.*.Tests/  .NET 测试
  python/                   Python 自动化测试
  e2e/                      HTTP 真实流程与 Playwright CLI 浏览器片段
  acceptance/               PDF 几何、栅格、音高适配器和导出验收
  run.ps1                   默认检查与可选基础设施集成测试入口
tools/evaluation/           OpenCV A/B、真实谱探测与逐页质量矩阵
MusicRecognition.Web/
  scripts/                  PDF.js 准备、OpenAPI 拉取与 SDK 可复现检查
  tests/contract/           SDK / Zod 契约
  tests/e2e/                真实 SDK 流程
```

## 迁移关系

| 原位置 | 新位置 |
| --- | --- |
| `scripts/poc/annotate.py`、`pdf_geometry.py`、`pitch_validation.py`、`ExportNotes.java` | `scripts/runtime/` |
| `scripts/runtime/setup.ps1`、`prepare.ps1`，`scripts/poc/requirements*.txt` | `scripts/setup/` |
| `scripts/*browser*.js`、P2–P4 浏览器片段、真实 API 流程 | `tests/e2e/` |
| `scripts/verify-*.py`、`scripts/poc/verify.py`、`pdf_matrix.py` | `tests/acceptance/`；`verify.py` 改名为 `verify-raster.py` |
| `scripts/poc/run.ps1`、`run-pdf-matrix.ps1` | `tests/acceptance/run-poc.ps1`、`run-pdf-matrix.ps1` |
| `scripts/validate-opencv.py`、`analyze-opencv.py`、`probe-p3.py`、`p4-quality-matrix.py` | `tools/evaluation/` |
| 前端 `scripts/check-contract.ts`、`check-live-sdk.ts` | 前端 `tests/contract/`、`tests/e2e/` |

表中旧位置仅用于迁移对照，不是可执行路径。维护文档中的复跑命令、Python 导入、PowerShell 子进程、前端 npm 入口与编辑器配置均改用新位置；忽略目录中的既有输出和历史证据不批量改写。

`.tools/poc-python`、`.tools/poc-classes` 和 Java 包名 `musicrecognition.poc` 保留兼容，不重命名已准备的运行环境或引擎入口。正式运行不再依赖 `scripts/poc` 或验收代码。前端检查放在自身 npm 依赖范围，避免增加一套根目录 npm 依赖。

## 执行入口

- 环境准备：[scripts/README.md](../scripts/README.md)。便携工具下载与哈希校验单独归入 `scripts/setup/provision-tools.ps1`。
- 自动化检查：`pwsh -File tests/run.ps1`；完整基础设施集成测试使用 `-Integration`，前提见 [tests/README.md](../tests/README.md)。
- 原谱独立验收：[tests/acceptance/README.md](../tests/acceptance/README.md)。
- 质量评估：[tools/evaluation/README.md](../tools/evaluation/README.md)。
- 前端 npm 入口：`test:contract`、`test:sdk-live`。

复跑发现旧 P0 独立入口没有同步后续八度线恢复步骤。已复用现有 `octaves.recover` / `apply_to_notes` 补齐，继续执行严格校验；Java 适配器在工作副本上运行，避免覆盖供复用的原始 OMR。未放宽音高、声部或坐标一致性规则。

## 验证

- 统一入口 `tests/run.ps1 -Integration`：34 项 Python、37 项领域、13 项基础设施测试通过；10 个 OpenAPI 操作的契约、SDK 可复现、前端类型及 ESLint 通过。
- 新 `scripts/setup/prepare.ps1 -WithOpenCV` 编译 / 依赖 / PDF 烟雾检查通过，API 与 Worker 重建后健康。
- 新 `tests/acceptance/verify-p1-export.py` 对上述实际导出 PDF 验收：标注外变化像素 0、原墨迹变化 0、标注框重叠 0，所有数字与权威 C# 标签一致。
- 新 `tests/acceptance/run-poc.ps1` 的独立真实 PDF 流程通过，证据为 `output/script-layout-poc-v2/`；默认黑色 10 pt，201 个符头保留，193 个自动放置、8 个需人工放置，未将待放置计为成功。
- 15 个 Python 验收 / 流程 / 评估入口的 `--help` 与导入通过；迁移后的 PowerShell 和浏览器片段语法检查通过。本次没有逐个重跑所有历史浏览器业务场景。

旧 `scripts/poc/` 中只剩被 Git 忽略的 Python 缓存；清理命令被自动审批拒绝，因此保留。源码与正式运行不依赖此目录。
