# 一页原谱叠加 PoC

Windows / PowerShell 7。首次运行下载约 85 MB Audiveris 发布包及 141 MB 便携 JDK，只解压到 `.tools/`，不进行全局安装。原扫描图 / 识别 / 标注输出默认位于 `output/poc/`。

## 运行

安装 Python 依赖（建议虚拟环境）：

```powershell
python -m venv .tools/poc-python
.tools/poc-python/Scripts/python.exe -m pip install -r scripts/setup/requirements.txt
pwsh -File tests/acceptance/run-poc.ps1 -Python .tools/poc-python/Scripts/python.exe
```

或者传入已具备这些依赖的 Python 路径。Poppler 的 `pdftoppm` 在 PATH 上时会自动渲染 PDF 并执行像素校验；否则明确报告未执行渲染验证。

重新做布局而不重跑 OMR：

```powershell
pwsh -File tests/acceptance/run-poc.ps1 -Python .tools/poc-python/Scripts/python.exe -ReuseRecognition
```

`-InputImage` 支持单页 JPG / PNG。此路径的 `original.pdf` 是原扫描图等比嵌入的新 PDF。`-InputPdf` 则使用真实原 PDF 的绘图内容进行矢量叠加，两参数互斥。替换样本时请同时换 `-OutputDirectory` 保留证据。

## 真实 PDF 与旋转 / 裁剪矩阵

要求 Poppler `pdftoppm` 在 PATH 上。单页原 PDF：

```powershell
pwsh -File tests/acceptance/run-poc.ps1 -InputPdf C:/scores/one-page.pdf -OutputDirectory output/my-pdf -Python .tools/poc-python/Scripts/python.exe
```

一键下载带哈希验证的 Mutopia 真实矢量谱，识别完整页和裁剪页，再运行 9 个几何 / 导出案例：

```powershell
pwsh -File tests/acceptance/run-pdf-matrix.ps1 -Python .tools/poc-python/Scripts/python.exe
pwsh -File tests/acceptance/run-pdf-matrix.ps1 -Python .tools/poc-python/Scripts/python.exe -ReuseRecognition
```

支持条件：单页、未加密、UserUnit=1、CropBox 在 MediaBox 内、页面 `/Rotate` 为 90 度的整数倍。这里验证的是页面元数据旋转，不是自动纠正内容流里画歪的乐谱。渲染固定 216 DPI，不将补齐后的整数像素尺寸当成实际页面尺寸。

每个不同 CropBox 运行一次 OMR。其余旋转与平移变体只有在临时正向识别图像素完全一致时复用结果。独立对照从已标注的 0 度 PDF 经 PDF 页面操作旋转 / 平移后渲染，避免用同一个坐标函数生成预期图。

默认输出 `output/poc-pdf/`：`matrix-results.json`、逐案例的 `matrix-result.json` / `verification.json`、完整及裁剪标注 PDF，以及 9 页 `rotation-crop-validation.pdf`。每个标注的 `pdfAnchor` / `pdfTextBaseline` 可追踪到原 PDF 用户空间；直接解析实际 PDF `Tm` 指令检查文字基线误差。

## 结果

- `preview.html`：可离线打开；开关标注、符头框、缩放、查看音高来源。
- `original.pdf` / `annotated.pdf`：未加注底图与微小矢量标注结果。
- `notes-1.json`：直接从同一引擎音符对象导出的语义与几何。
- `annotations.json`：固定 C 标注、八度点、位置、需手动放置的音符。
- `metrics.json`：集合一致性、数量与背景未变化检查。
- `verification.json`：实际 PDF 栅格变化是否局限于标注框、碰撞与坐标校验。
- `audiveris.log` / `export-notes.log`：真实引擎与适配器日志。
- `omr/*.omr` / `omr/*.mxl`：原始识别产物。

此入口保留 P0 独立样本验收流程，不启动 Nuxt 编辑器或后端任务系统；完整业务入口见 [测试说明](../README.md)。布局仍为候选搜索，待放置状态不会偷偷变成成功。当前默认字体为黑色 10 pt；历史 5 pt 结果见原 P0 报告，不作为本次复跑预期。PDF 诊断 HTML 展示的是临时正向识别视图，页面实际旋转请检查原 PDF / 标注 PDF 及矩阵渲染图。
