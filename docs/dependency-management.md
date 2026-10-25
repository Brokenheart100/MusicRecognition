# 依赖版本集中管理

日期：2026-10-22。本次统一版本声明位置，保留现有版本及依赖关系。

## NuGet 包

根目录 [Directory.Packages.props](../Directory.Packages.props) 使用 NuGet Central Package Management：

- 所有显式 NuGet 包版本只在这里声明，项目仅保留无版本的 `PackageReference`。
- `ManagePackageVersionsCentrally=true`；`CentralPackageVersionOverrideEnabled=false`，防止个别项目用 VersionOverride 绕过统一版本。
- EF Core、OpenTelemetry、Aspire integration 各使用共享版本属性。同一个包在测试项目和服务项目之间也共用版本。
- `PrivateAssets` 等使用方式仍归各项目管理，集中声明版本不会把包注入所有项目；Domain 没有因此引入 EF Core 或任务框架。
- 暂不启用全局 transitive pinning。保留显式 EF Core / Relational 和 Newtonsoft.Json 引用，分别避免先前出现的 EF 版本冲突及旧 JSON 包漏洞。

新增或升级包时，修改此文件并在需要的项目添加引用；还原、构建、测试和传递依赖审计后再记录结论。集中管理统一声明位置，不意味着所有组件必须使用同一版本数字。

2026-10-25：在此集中固定 `CliWrap 3.10.5`，只由 Infrastructure 引用，用于 Python 运行和启动依赖检查；Domain / Application 不依赖进程库。接入范围、Windows 进程树兼容处理及验证见 [CliWrap 接入报告](cliwrap-validation.md)。工具的安装、路径及版本仍由原有准备脚本和 Recognition 配置管理。

## SDK 与工具

- [global.json](../global.json) 的 `msbuild-sdks` 集中固定 Aspire.AppHost.Sdk；AppHost 的 Project Sdk 只保留名称。SDK 在项目求值早期解析，不放进 PackageVersion。
- Aspire SDK 和 Aspire integration 包目前都为 13.6.2，升级时同时核对这两个位置。
- [dotnet-tools.json](../dotnet-tools.json) 是 .NET 官方本地工具清单。dotnet-ef 继续固定 10.0.13，与集中声明的 EF Core 10.0.13 一起核对，使用 `dotnet tool restore`，不另造工具版本解析机制。
- Python / Audiveris 工具仍按 [P0 说明](../tests/acceptance/README.md) 准备；NuGet CPM 不负责其他生态的依赖。P2 的 Nuxt 依赖集中在 MusicRecognition.Web/package.json，并提交 package-lock.json；用 npm ci 还原。Python 新增 FontTools 也在既有 requirements.txt 固定版本。

## 本次验证

2026-10-22 补充：Python 默认采用项目 `.tools/poc-python` 虚拟环境；`scripts/setup/setup.ps1` 安装基础固定依赖，`-WithOpenCV` 使用 `scripts/setup/requirements-opencv.txt` 引用基础清单并增加 `opencv-python-headless==4.13.0.92`。不把 OpenCV 重复装进 NuGet / npm，也未引入 pdfme。`pip check` 与真实闭环 / A/B 结果见 [专项报告](opencv-validation.md)。以下保留此前 NuGet 集中管理迁移时的验证记录。

9 个项目、20 处显式 PackageReference 合并为 17 个集中 PackageVersion。迁移前后全部项目的已解析 NuGet 包 ID / 版本集合一致；比较结果见 `output/validation/cpm/comparison.json`。AppHost 通过 global.json 正确解析相同版本 SDK。

全解决方案使用独立产物目录构建，避免覆盖运行中服务的程序集：

```powershell
dotnet restore MusicRecognition.slnx --force-evaluate
dotnet build MusicRecognition.slnx --artifacts-path output/validation/cpm/artifacts
dotnet test tests/MusicRecognition.Domain.Tests --artifacts-path output/validation/cpm/artifacts --no-build --no-restore
dotnet list MusicRecognition.slnx package --vulnerable --include-transitive
```

本次结果：全量构建 0 warnings / 0 errors，领域测试 18 / 18 通过；9 个项目含传递依赖的当前 NuGet 漏洞审计均未报告已知漏洞。MSBuild 求值确认集中管理开启且 VersionOverride 禁用。未新增测试用例，也未重跑 Audiveris 集成验收；识谱规则和运行时调度逻辑没有因本次依赖声明迁移而改动，P1 的真实工具恢复记录保留原结论。

官方依据：[NuGet CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)、[MSBuild SDK 集中版本](https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-use-project-sdk#how-project-sdks-are-resolved)。


## P3 前端生成与查询依赖

前端统一在 `MusicRecognition.Web/package.json` 固定直接依赖，`package-lock.json` 锁定完整依赖树；使用 npm ci 还原。新增 hey-api 0.99.0（开发时生成）、TanStack Vue Query 5.104.5、显式 Vue 3.5.44、tsx 4.23.15；Zod 4.6.7 和 @nuxt/eslint 1.17.0 沿用。使用官方 Fetch 客户端，不采用面向 Nuxt 3 的 beta 客户端。`overrides.js-yaml = 4.3.2` 修复生成器传递依赖审计问题，不屏蔽漏洞。剩余 11 个 high 来自现有 Nuxt 依赖链，详见 [P3 集成报告](p3-api-query.md)。

后端 OpenAPI 是 API 类型来源；`api:pull` 更新快照，`api:generate` 生成 SDK / 类型 / Zod，`api:check` 检查生成漂移，`test:contract` 检查关键契约和运行时校验。生成产物应纳入版本控制，不手工修改，也不重复维护另一套 API 类型。
