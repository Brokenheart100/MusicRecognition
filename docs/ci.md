# GitHub Actions 持续集成

2026-10-25 接入，配置位于 [.github/workflows/ci.yml](../.github/workflows/ci.yml)。

## 触发与检查

推送到 `main`、创建 / 更新 PR 和 Actions 页手动运行时触发 `CI`。同一 PR 或分支的新运行会取消前一次未完成运行。

| 检查 | 环境 | 内容 |
| --- | --- | --- |
| Backend | Ubuntu 24.04 / Windows 2025 | 还原并 Release 构建完整解决方案；领域、基础设施单元测试及真实 Python 进程夹具；Linux 另外验证 API / Worker 发布 |
| Python algorithms | Ubuntu 24.04 / Python 3.14 | 安装固定版本的基础 / OpenCV 依赖，`pip check`，运行 Python 算法、PDF 合并与导出规则测试 |
| Nuxt checks and build | Ubuntu 24.04 / Node.js 24 | 按 packageManager 安装 npm，`npm ci`，SDK 生成漂移、API 契约、布局规则、类型检查、ESLint 和生产 Node 服务构建 |
| All checks | Ubuntu 24.04 | 只有所有前置检查成功才通过；失败或跳过的检查不能误报通过 |

后端测试只操作临时夹具，不连接开发 PostgreSQL，不启动 Aspire，不接触已有上传任务。可在缺少完整识谱工具链的 GitHub 托管 runner 上运行。

以下验收不在默认 CI 中：真实 Audiveris 识谱、缺失 Poppler 启动检查、整份真实 PDF 流程、浏览器 / Safari、完整 Docker 工具链镜像、Compose 启动和线上发布。这些仍按 [测试说明](../tests/README.md) 和 [P5 报告](p5-operations.md) 单独执行。Python 用例中的模拟 PDF 处理不等于完整识谱验收。

## 依赖与权限

- `.NET SDK` 在根目录 `global.json` 统一固定 10.0.402，允许同一 SDK 功能带内更新补丁，不接受预览版；Aspire SDK 继续由同文件的 msbuild-sdks 管理。
- NuGet 版本沿用 `Directory.Packages.props`；Python 包沿用 `scripts/setup/requirements*.txt`；npm 版本沿用前端 packageManager，完整依赖树沿用 package-lock.json。
- `.gitattributes` 固定 SDK 生成文件和工作流 YAML 使用 LF，避免 Windows checkout / archive 的 CRLF 转换导致逐字节生成校验误报；没有放宽生成结果比较规则。
- Actions 固定到官方仓库已核对的完整提交 SHA，并在注释注明对应版本；升级时同时核对 SHA 和版本。
- 工作流只有 `contents: read`，checkout 不保留 Git 凭据。不使用 `pull_request_target`，不传入生产 secret，不发布镜像或部署应用。
- 缓存 pip / npm 下载，不缓存 `.env`、任务目录、数据库或整个工作区。NuGet 暂未启用需要 packages.lock.json 的 setup-dotnet 缓存。
- .NET TRX 测试报告保留 7 天，即使测试失败也尝试上传。不会上传现有 `output/`、访问凭据、部署参数或用户谱面。

这是一套 CI 构建与测试检查，不代表已通过依赖安全审计；已有依赖漏洞仍按相关报告处理。

## 使用

将这些文件正常提交并推送后，在 GitHub 仓库 **Actions → CI** 查看各任务日志、失败步骤和测试报告。默认分支存在工作流后可使用 **Run workflow**。

建议在仓库规则中把 **All checks** 设为合并必需状态检查。首次成功运行后按 GitHub 实际显示的检查名称选择。本次仅提供工作流，没有修改远端分支保护或 Actions 设置。

后续 CD 可在 CI 成功后增加镜像发布、目标服务器更新、健康检查与回滚；需要单独配置目标和凭据。目前没有推送即部署生产的行为。

依据：[GitHub Actions 安全使用](https://docs.github.com/en/actions/reference/security/secure-use)、[setup-dotnet](https://github.com/actions/setup-dotnet)、[setup-python](https://github.com/actions/setup-python)、[setup-node](https://github.com/actions/setup-node)。

## 本次验证

- actionlint 1.7.12 校验通过；工具下载摘要已核对。
- Windows 本地按 CI 命令构建完整 Release 解决方案，0 warnings / 0 errors；领域 40 / 40、基础设施及进程夹具 21 / 21 通过。
- Linux 使用从 Git 提取的干净源码及独立 .NET 10.0.402 / Ubuntu 24.04 容器，完整 Release 构建、同样的 40 + 21 项测试、API / Worker publish 均通过；此本地容器的进程夹具使用系统 Python 3.12，GitHub 托管 runner 将通过 setup-python 使用 3.14。
- 独立 Windows Python 3.14 虚拟环境及 Linux Python 3.14 容器均从固定清单重新恢复依赖，`pip check` 和各自 63 项 Python 测试通过。现用识谱环境中的 pip 文件不完整，本次未修改它，CI 使用全新环境。
- 本地前端 SDK 漂移、契约、类型检查、ESLint 和 Node 生产构建均通过。构建仍有既有大 chunk 警告；本次没有更改前端业务代码或消除这些警告。
- Linux Node 24.21.0 容器从干净源码执行项目指定 npm 11.19.0 和首次 `npm ci`；修复 Git 换行属性后，SDK 生成文件逐字节校验、10 个 API 操作契约、类型检查、ESLint 和生产构建全部通过。`npm ci` 仍提示现有 7 high / 1 low 漏洞，没有添加忽略规则或自动升级依赖来消除提示。

本地结果和 TRX 位于忽略目录 `output/validation/github-actions/`。这些结果验证检查命令和跨平台构建，不能替代 GitHub 托管 runner 的执行结果。仓库已启用 Actions，远端结果以 [CI 运行记录](https://github.com/Brokenheart100/MusicRecognition/actions/workflows/ci.yml) 中对应提交的检查为准。
