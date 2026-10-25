# Aspire AI 代理支持与运行检查

初次检查日期：2026-10-22。下文记录初始化时的历史状态；当时仅诊断，未修改运行环境。随后已按用户要求修复 Python 环境并恢复识谱，当前结果见 [修复与 OpenCV 验证](opencv-validation.md)。下列故障记录保留作为修复前证据。

## 初始化结果

在项目根目录执行成功：

```powershell
aspire agent init --workspace-root D:/Code/CSharp/MusicRecognition --skill-locations standard --non-interactive
```

CLI 安装了 7 个技能：aspire、aspire-deployment、aspire-init、aspire-monitoring、aspire-orchestration、aspire-project-v2-migration、aspireify。项目副本位于 `.agents/skills/`；CLI 同时报告更新了用户级 `~/.agents/skills/`。后续操作优先遵循项目副本。

本次使用 CLI 的默认技能配置，没有传 `--mcp`，因此不能把这次成功初始化称为已建立 MCP 连接。当前 CLI 的 `aspire agent init --help` 提供可选 `--mcp` 参数。

## 运行状态

所有诊断命令均明确指定 `MusicRecognition.AppHost/MusicRecognition.AppHost.csproj`，避免查询其他项目。

| 资源 | 当前状态 | 说明 |
| --- | --- | --- |
| web | Running / Healthy | Nuxt 启动完成；本次端点 http://localhost:59145 |
| api | Running / Healthy | 数据库迁移已是最新 |
| worker | Running / Healthy | Hangfire 已启动，但存在下述识谱任务故障 |
| recognition-postgres / recognitiondb | Running / Healthy | PostgreSQL 已完成启动恢复，当前可连接 |
| web-installer | NotStarted | AppHost 设置 `WithNpm(install: false)`，未自动安装 npm 依赖；web 已正常运行 |

URL 为运行实例的动态端点，下次启动应重新通过 `aspire describe` 获取。

## 日志发现

结构化 Warning 及以上日志返回两条识谱失败，均属于任务 `326a6ef2-63c4-4424-89d5-0a3fccef74aa`。Worker 控制台与该任务受保护的工具日志交叉确认：

```text
File "scripts/runtime/pipeline.py", line 29, in <module>
    from PIL import Image
ModuleNotFoundError: No module named 'PIL'
```

当前 AppHost 注入的 `Recognition__Python` 为 `python`；该运行环境缺少 Pillow。故障发生在 Python 导入阶段，尚未进入识谱引擎，不能归因为样谱识别质量问题。证据保存在 `output/backend/326a6ef263c4442489d50a3fccef74aa/recognize-*.log`。

健康检查通过仅说明服务与数据库连接健康，不代表识谱依赖齐全。本次没有安装 Python 包、更换解释器、重启 AppHost或重试任务。修复时应先选择一个项目专用且安装了 `scripts/setup/requirements.txt` 的 Python 环境，用 `scripts/setup/prepare.ps1 -Python <绝对路径>` 检查，再按 [P1 启动说明](p1-backend.md) 配置解释器并重新验收真实任务。

API 控制台没有业务错误。PostgreSQL 控制台出现启动恢复期间的连接失败及 `recognitiondb already exists`，后续已进入可接受连接状态，当前健康检查正常。错误 trace 查询返回 `No traces found`，这不抵消已观察到的任务失败日志。

## 复查命令

```powershell
aspire describe --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire otel logs --severity Warning --limit 30 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire logs worker --tail 65 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire logs api --tail 55 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire logs web --tail 55 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire logs recognition-postgres --tail 25 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire otel traces --has-error --limit 10 --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
```

分享诊断结果时只保留状态、端点与必要日志；不要直接公开 `describe --format Json` 的完整环境变量，其中可能包含连接串和遥测认证信息。
