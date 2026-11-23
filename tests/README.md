# 测试与验收

从仓库根目录执行，先按 [工程脚本说明](../scripts/README.md) 准备依赖，并在前端执行过 `npm ci`。

```powershell
pwsh -File tests/run.ps1
```

默认运行 Python 测试、.NET 领域 / 基础设施非集成测试、前端契约与布局规则、SDK 可复现检查、类型检查和 ESLint；不启动服务或修改真实任务。

`-Integration` 另外运行真实 Python 失败诊断、缺失依赖和超时终止测试，需要完整识谱依赖，以及 `output/poc-pdf/input/Minuet-xpose-a4.pdf` 样本。测试只操作专用目录，不替代真实业务验收。

`ToolProcessTests` 使用临时 Python 夹具检查 CliWrap 的中文 / 空格路径和引号参数、UTF-8 输出、日志上限、超时及主动取消后的子 / 孙进程清理；只需设置 `MUSICRECOGNITION_PYTHON` 为实际解释器路径。单独执行：`dotnet test tests/MusicRecognition.Infrastructure.Tests --filter FullyQualifiedName~ToolProcessTests`。Linux 容器运行时应启用 `--init`，及时回收被终止的后代进程。详情见 [接入报告](../docs/cliwrap-validation.md)。

| 位置 | 用途 / 执行方式 |
| --- | --- |
| `MusicRecognition.Domain.Tests/` | .NET 领域测试，`dotnet test` |
| `MusicRecognition.Infrastructure.Tests/` | 诊断、依赖和工具进程测试，`dotnet test` |
| `python/` | `python -m unittest discover -s tests/python` |
| `e2e/*.py` | HTTP 上传、识谱、逐页校对及导出流程，先 `aspire wait api` 和 `aspire wait worker`，各脚本支持 `--help` |
| `e2e/*.js` | 现有 Playwright CLI 回归片段，在指定会话 / 起始修订运行，`playwright-cli run-code --filename tests/e2e/文件名.js` |
| `acceptance/` | 真实 PDF 栅格、几何、音高适配器和导出验收，见 [运行说明](acceptance/README.md) |
| `../MusicRecognition.Web/tests/contract/` | 生成 SDK / Zod 契约，前端目录运行 `npm run test:contract` |
| `../MusicRecognition.Web/tests/e2e/` | 真实 SDK 调用验证，在前端 npm 依赖范围内运行 |

真实流程、浏览器脚本可能上传文件、保存修订或导出产物，仅对专用验证任务运行。部分阶段脚本要求指定初始修订、页数和样例；它们是可复现验收工具，不是默认测试套件。凭据继续放忽略的 `tmp/`，结果放 `output/`，不要提交。

常用示例：

homr 适配器的无模型测试包含在默认 Python 套件中。真实 A/B 使用 `e2e/validate-homr-ab.py --url <API> --samples <JSON清单> --output output/homr-ab/live`，清单格式为 `[{"name":"样本标识","path":"原文件路径"}]`，分别创建 Audiveris / Homr 专用任务，等待逐页导出及整份 PDF 合并；凭据和结果只写入忽略的输出目录。同目录再次执行可恢复等待，不能修改源样本。真实结果见 [专项报告](../docs/homr-validation.md)。

```powershell
.tools/poc-python/Scripts/python.exe tests/acceptance/verify-p3-export.py --source <原PDF> --pdf <整份标注PDF> --evidence <验收输出目录>
```

P5 的 `tests/e2e/validate-p5.py` 覆盖真实 PDF、Worker 中断、幂等、413/429、权限和有界读取。`tests/acceptance/validate-p5-retention.ps1` 会创建专用过期夹具、备份并应用清理，`verify-p5-restore.py` 在新的还原项目验证原凭据与 PDF 字节一致。它们需专用 Compose 部署，必须与停写备份串行执行，见 [P5 运维报告](../docs/p5-operations.md)。
