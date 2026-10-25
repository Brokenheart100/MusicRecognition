# CliWrap 接入与验证

日期：2026-10-25。

## 范围

根目录 `Directory.Packages.props` 固定 CliWrap 3.10.5，只有 Infrastructure 添加无版本的 PackageReference。`ToolEngine` 和 `RecognitionRuntime` 用 CliWrap 配置可执行文件、参数数组、工作目录、环境变量以及 UTF-8 标准输出 / 标准错误管道。保留既有业务接口、调度与错误码；不改识谱和标注规则。

CliWrap 简化进程调用，不安装 Poppler、Python、Java 或 MuseScore，也不替代 Recognition 路径配置和启动依赖检查。

## 行为约束

- 长任务继续流式读取输出，不使用全量内存缓冲。日志最多保存 10,000 行、每行最多 4,000 字符；达到上限后继续排空管道，独立保留最后 8 行标准错误用于失败诊断。
- 启动检查的两个输出缓冲区各最多保留 16,000 字符，每行最多 4,000 字符，避免原先无上限的 ReadToEnd。
- 非零退出码由本项目显式检查，并继续读取受保护的 `error.json`，记录中文文件名 / 页码、错误原因和日志路径；客户端只收到现有安全错误消息。为此使用 `CommandResultValidation.None`，不把第三方命令参数异常直接返回客户端。
- 保留每 20 秒进度、耗时、运行结果和 Python 根进程内存采样；内存指标不代表整个 Java / MuseScore 进程树。
- 长任务超时维持 Recognition:TimeoutSeconds，启动检查维持 60 秒；Windows 清理允许额外最多 10 秒，随后由 CliWrap 强制终止兜底。

## Windows 进程树兼容

测试发现 Windows Python 虚拟环境启动器下，单独调用 .NET 10 的 `Process.Kill(entireProcessTree: true)` 会终止启动器和 Python 根进程，但可能遗漏后代。使用同一 Python 夹具对旧式调用直接复现了此问题，因此不能只根据根进程已退出就认定清理成功。

CliWrap 3.10.5 的强制取消也使用该 .NET API。`ToolCommand` 在 Windows 上先通过 CliWrap 调用系统目录的 `taskkill.exe /PID <本次启动PID> /T /F`，再触发 CliWrap 的强制取消并等待命令结束。只按本任务的 PID 处理，不按 python / java 映像名称批量终止。清理失败或超时记录中文警告并执行强制终止兜底；此类异常情况下不能保证所有后代都已退出。

Linux 直接使用 CliWrap 的强制取消。这里没有新增自定义进程枚举器、Win32 Job Object 封装或额外任务调度框架。

依据：[CliWrap 官方用法](https://github.com/Tyrrrz/CliWrap/tree/3.10.5)、[固定版本的取消实现](https://github.com/Tyrrrz/CliWrap/blob/3.10.5/CliWrap/Utils/Extensions/ProcessExtensions.cs)、[.NET Python 启动器已知问题](https://github.com/dotnet/runtime/issues/107992)、[Microsoft taskkill 参数](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/taskkill)。

## 验证结果

Windows：Infrastructure **23 / 23**、Domain **40 / 40** 通过。其中新增 6 个进程集成用例覆盖路径 / 参数 / 环境变量、中文输出、有界日志及末尾错误、超时清理、主动取消清理、启动检查取消及有界错误诊断；进程树用例确认实际 Python 根进程、子进程和孙进程均退出。原有真实 Audiveris 10 秒超时和缺失 Poppler 检查仍通过。

Linux：在隔离的 `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` 容器中安装系统 Python 3，以 `--init` 运行上述 6 个进程用例和 4 个失败诊断用例，**10 / 10** 通过。测试直接运行本次编译的 AnyCPU 程序集及同一 CliWrap 包，确认 Linux 分支的参数、输出、失败转换和进程树清理行为。此处没有重建 / 发布完整 Compose 识谱镜像，也没有重新进行 Linux 的真实 Audiveris 验收。

NuGet 对 Infrastructure 及其传递依赖的本次审计未报告已知漏洞。此结论不覆盖前端依赖。

API 和 Worker 通过 Aspire 资源 rebuild 更新，构建均为 0 warnings / 0 errors，随后 `aspire wait` 确认 Healthy；启动依赖检查通过。

真实 `MusicRecognition.Web/public/samples/minuet.pdf` 通过新运行时完成上传、识谱、初次导出和改音高后的第二次导出，识别 **201** 个音符。幂等上传、错误凭据 404、旧修订 409、修订不变性检查均通过。独立 Poppler 栅格检查结果：标注区域外变化 **0** 像素、原谱深色笔画变化 **0**、标注相互重叠 **0**、新增非灰度像素 **0**；导出标签与 C# 标签逐项一致。这证明进程迁移未破坏该样本闭环，不代表所有谱面识别准确率。

证据位于忽略目录 `output/validation/cliwrap/`：`results/windows.trx`、`results/linux.trx`、`results/domain.trx`、`nuget-audit.json`、`summary.json`、`real-flow.json` 和其导出子目录。`real-flow.json` 含验证任务访问凭据，仅留本机，不应提交。
