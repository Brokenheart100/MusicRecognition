# Python 运行环境修复与可选 OpenCV A/B 验证

日期：2026-10-22。范围：恢复一页原谱识别 / 修订 / 导出，引入可选光照预处理，并用真实输入执行端到端对照。**pdfme 未引入。**

## 结论

Python 依赖故障已修复，原有真实 PDF 闭环恢复。OpenCV 已接入为默认关闭的实验选项，**本轮没有证明它提升音高识别精度，也不建议把当前 Adaptive 预设默认启用**：清晰 PDF 两组均成功；真实 Bach 扫描及其受控阴影变体的增强组均触发音高一致性失败。失败结果没有发布 raw 或标注 PDF，没有移除校验、自动切换模式或把失败计为成功。

## 运行环境修复

原 Worker 使用 PATH 中的 Python 3.14.7，缺少 Pillow，在 `pipeline.py` 导入 `PIL` 时失败。现在使用项目独立的 `.tools/poc-python` 虚拟环境，固定依赖集中于 `scripts/setup/requirements.txt`。AppHost 的选择顺序为：`Recognition:Python` 配置 → `MUSICRECOGNITION_PYTHON` → 项目虚拟环境。默认环境不存在时给出准备命令，不再悄悄回退到系统 Python。

新增 `scripts/setup/setup.ps1` 创建环境、安装依赖并调用 prepare 检查。基础依赖与可选 OpenCV 分开声明；`requirements-opencv.txt` 引用基础清单，并固定 `opencv-python-headless==4.13.0.92`，不重复安装 GUI / contrib / OpenCvSharp / OpenCV.js。实际导入 `cv2.__version__` 为 4.13.0，NumPy 继续使用已固定的 2.3.5。`pip check` 通过。

在引入 OpenCV **之前**，已完成真实 PDF 的 HTTP 上传、201 个音符、纠错、新修订、下载、旧版本不可变及并发版本冲突检查。任务 `9d0375cd-f3d0-4ef4-96f5-b8fa96b77f4f` 的 Revision 2 经 Poppler 独立渲染：10,460 个变化像素，标注区域外变化、原墨迹覆盖、黑色混合异常、标注重叠均为 0。

## 适配器与数据契约

- C# Application 定义 `PreprocessingMode.None / Adaptive`；导入任务时持久保存。EF 增量迁移 `AddPreprocessing` 仅新增整数列，旧任务默认 None。没有改变音高规则或用 Python 生成业务简谱数字。
- 上传 multipart 增加可选字段 `preprocessing=None|Adaptive`，省略即 None。未知模式返回 400；同一幂等键换文件或换模式返回 409。JobView 返回实际选择，重试继续使用同一模式；改变模式需重新导入。
- Worker 将任务选项传给隔离 Python 管线。`scripts/runtime/preprocess.py` 在 Infrastructure 工具适配路径执行，领域层不引用 OpenCV。
- `raw/recognition.png` 是原件对应的正向识别栅格；`engine-input.png` 是交给 Audiveris 的识别副本。预览、避让的可见墨迹检查和 PDF 底图继续使用原件。原 PDF 不因为 OpenCV 被栅格化后替换。
- Adaptive 使用灰度化及 Gaussian 自适应阈值：最大 blockSize 51、C=15、二值输出。没有加入去五线谱、腐蚀、裁剪、缩放、倾斜或透视校正，避免未经验证地破坏细小符号和原坐标。
- `preprocessing.json` 记录模式、库版本、参数、输入 / 输出哈希、尺寸、耗时、正逆矩阵。本阶段两个矩阵均为单位矩阵，图像尺寸不变；管线校验该约束，拒绝不支持的几何变化。将来加 deskew / perspective 必须另做逆映射验收。
- 原件哈希与导入模式进入 manifest；复用识别产物时检查它们，防止用另一模式的缓存。缺少可选 cv2 时明确失败为 `preprocessing-unavailable`，不会静默回退。None 路径不导入 cv2。
- 网页增加默认未勾选的“光照增强 · 实验”选项，任务打开后显示本次使用增强。上传模式进入浏览器幂等身份；刷新、重新打开不会改写已有任务模式。

## 真实样本与方法

两份独立原件的来源、许可和 SHA-256 见 [样谱记录](../samples/README.md)：Mutopia 单页矢量《小步舞曲》和 Audiveris 公开扫描 `BachInvention5.jpg`。第三个案例来自同一真实扫描的**受控光照变体**：每个 RGB 通道乘水平 0.65…1.00 的线性亮度，不改变几何。它不是第三份独立乐谱，也不代表真实相机拍摄验收。

阴影变体 SHA-256：`6cb0aebcd5749d5b9085f7316e36ba6e562c6ba1441a915904d76b60430e4567`。

每个输入分别以 None / Adaptive 上传，经过实际 Nuxt 所用的同一后端 API、PostgreSQL、Hangfire、Audiveris、C# labels 和原谱导出。记录真实状态；成功组下载原件及修订 PDF，执行独立 Poppler 像素验证。全程没有伪造识别结果或跳过 pitch-consistency 检查。

## A/B 结果

| 样本 | 模式 | 最终状态 | 符头 | 已放置 | 待放置 | 上传至识别结束等待 / 秒 |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 小步舞曲 PDF | None | Ready | 201 | 200 | 1 | 20.158 |
| 小步舞曲 PDF | Adaptive | Ready | 201 | 200 | 1 | 16.341 |
| Bach 真实扫描 | None | Ready | 395 | 393 | 2 | 41.613 |
| Bach 真实扫描 | Adaptive | Failed：pitch-consistency-failed | — | — | — | 29.555 |
| Bach 受控阴影 | None | Ready | 395 | 310 | 85 | 29.578 |
| Bach 受控阴影 | Adaptive | Failed：pitch-consistency-failed | — | — | — | 25.541 |

等待时间是单次端到端观测，包含队列等待，期间另有浏览器验证任务；不是隔离 CPU 基准，不能据此称增强更快。四组 Ready 均完成 Revision 1 导出。

失败诊断保留在未发布的尝试目录：真实扫描增强组有 395 个符头，但 MusicXML 仅 390 个有音高音符；阴影增强组为 393 / 392，而且音高多重集有 42 个符头侧差异和 41 个 MusicXML 侧差异。数量接近也不代表语义一致。真实扫描增强任务又显式重试一次，仍被相同校验拒绝，模式保持 Adaptive、CurrentRevision 保持 0。

### 音高与锚点证据的边界

现有首小节 15 音符记录来自此前助手视觉转录，**不是独立教师复核的整页人工真值**。本轮只在成功的两组 Bach None 结果中检查该范围：均找到 15 / 15 个锚点、音高拼写符合 15 / 15。不能推广为 395 音符的全页准确率。未提供全页真值，因此不报告全页漏检率、误检率或识别准确率。

在原件坐标中，以 18 px 半径、一对一最近邻比较两组引擎输出：

| 样本 | 配对符头 | 中位位移 / px | 最大位移 / px | 两组音高不同数 |
| --- | ---: | ---: | ---: | ---: |
| 小步舞曲 PDF | 201 | 0.5 | 1.5 | 0 |
| Bach 真实扫描 | 395 | 0.5 | 2.0616 | 6 |
| Bach 受控阴影 | 393 | 0.5 | 1.5811 | 118 |

后两行包含被拒绝的诊断输出，不是可用标注成果。位移是二值化改变符头轮廓后产生的检测差异，不是矩阵舍入误差；两组不一致也不能单独判定哪组音高正确。阴影组有 2 个 None 符头未配对，不擅自把它们标为漏检或误检。

### 原谱与导出

四个成功组分别有 10,411 / 10,398 / 29,216 / 22,852 个变化像素；每组均满足：预留标注区域外变化为 0、原墨迹覆盖为 0、黑色混合异常为 0、重叠为 0、不可见放置标注为 0。保留原绘图操作，六组原件下载 SHA-256 均与上传件相同。

阴影原图组的可安全放置数量降至 310，说明原底图上的深色区域会限制避让；不能用增强后的白底替代原件来制造“更多标注放置成功”。

## 浏览器、构建与恢复

- Nuxt typecheck 与生产构建通过；.NET 全解决方案构建 0 warnings / 0 errors；领域测试 25 / 25；Python 预处理契约测试 5 / 5；真实工具 10 秒超时 / 子进程树终止集成测试 1 / 1。Python 测试含 None 无需 cv2、缺失 cv2 明确失败、原件保留、坐标不变及固定线条 / 光照样本。
- Edge 实际从页面勾选增强、上传示例、等待识别 / 导出并下载。浏览器任务 `6b2710d5-b834-4b4b-a6cf-67f16d0c68ab` 为 Adaptive，201 个音符、200 放置、1 待放置。浏览器 PDF 下载与服务器文件一致。
- 页面 100% / 150% / 200% 与 PDF.js 独立矩阵对照最大误差约 0.0054 / 0.0081 / 0.0108 CSS px；这是叠加显示误差，不是识谱锚点准确率。
- 1440 / 390 px 检查无整页横向溢出，手机刷新后恢复增强任务。浏览器 console error / warning 为 0。尚未实测真实 iPhone / Safari。
- API 资源重启后六组的状态、None / Adaptive 选项及原件下载保持一致；本次只重启当前项目 API 进行持久化验收。
- A/B 的非法模式 / 幂等冲突负向检查会被现有 ExceptionHandler 记录为 API error；不要把这些预期 400 / 409 日志与 Worker 的两组真实识别失败混为一谈。

## 复跑与证据

```powershell
# 基础环境；需要重新选择解释器时传 -Python <bootstrap-python>
./scripts/setup/setup.ps1
# 安装并检查可选 OpenCV
./scripts/setup/setup.ps1 -WithOpenCV
aspire start --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire wait worker --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire describe --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
# 用 describe 返回的 API 地址替换示例变量；样谱先按 P0 说明准备。
.tools/poc-python/Scripts/python.exe tools/evaluation/validate-opencv.py --url $apiUrl
.tools/poc-python/Scripts/python.exe tools/evaluation/analyze-opencv.py
.tools/poc-python/Scripts/python.exe tests/e2e/check-preprocessing-api.py --url $apiUrl
.tools/poc-python/Scripts/python.exe -m unittest discover -s tests/python -v
```

`validate-opencv.py` 会在忽略的 output 下保存任务凭据，以便断线后继续；要创建全新对照应传新的 `--output` 目录。复用任务时标记 resumedEvidence，不把立即返回的耗时当成新识别耗时。`--retry` 的检查脚本只重试 A/B 创建的失败任务，不操作用户任务。

- `output/opencv-validation/ab-results.json`：六组结果、哈希、像素核对、局部抽查与组间锚点比较，不含能力凭据。
- `output/opencv-validation/python-recovery.json`：修复后最初的完整闭环证据；此文件含本机测试凭据，不公开分享。
- `output/opencv-validation/retry-settings.json`、`persisted-settings.json`：真实重试及 API 重启后的模式保持。
- `output/validation/opencv-browser-geometry.json`、`opencv-ui-download.json`：增强模式浏览器几何与下载证据。
- [增强模式桌面工作台](../output/playwright/opencv-editor-1440.png)、[手机工作台](../output/playwright/opencv-editor-390.png)、[浏览器下载 PDF](../output/pdf/opencv-ui-minuet.pdf)。

官方依据：[OpenCV 自适应阈值](https://docs.opencv.org/4.13.0/d7/d4d/tutorial_py_thresholding.html)、[OpenCV 几何变换](https://docs.opencv.org/4.13.0/da/d6e/tutorial_py_geometric_transformations.html)、[固定 headless 包](https://pypi.org/project/opencv-python-headless/4.13.0.92/)。

后续优先增加经独立复核的音高真值与真实拍照样本，再评估更保守的背景归一化 / 对比度方案。当前不扩展到多页、不引入 pdfme、不宣称生产就绪；P2 已记录的依赖审计与真实 iOS 验收缺口仍有效。
