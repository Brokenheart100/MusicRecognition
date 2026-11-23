# MusicRecognition 代理指引

本项目面向钢琴初学者，将识别出的音高以简谱标在原五线谱的留白处，并支持逐页校对和 PDF 导出。图片片段拼谱有独立入口，可导出 MusicXML、MXL 和重新排版的 PDF。功能设计和现有报告见 [README](README.md)。

## 代码入口

- `MusicRecognition.Domain` 放音高、编号和标注规则，`MusicRecognition.Application` 放用例和接口。保持依赖从外层指向内层。EF Core、Hangfire、文件存储和识谱工具调用位于 `MusicRecognition.Infrastructure`。
- `MusicRecognition.Api` 提供 HTTP 和 OpenAPI 契约，`MusicRecognition.Worker` 执行识谱与导出，`MusicRecognition.AppHost` 编排 API、Worker、Nuxt 和 PostgreSQL。沿用 `ToolEngine` 与 CliWrap 的工具调用、超时、取消和日志机制。
- `MusicRecognition.Web` 是 Nuxt 4 工作台。通过生成的 hey-api SDK 与 Zod 校验访问 API，TanStack Query 管理任务轮询和服务端状态，Pinia 管理本机草稿和编辑历史。
- `scripts/runtime/` 是正式运行脚本，`scripts/setup/` 准备依赖，`scripts/ops/` 管理备份和恢复。测试与验收放 `tests/`，识谱质量评估放 `tools/evaluation/`。

## .NET 目录规范

下表的路径相对于各层项目目录。按文件职责和现有实现选用，目录随代码创建。相关的小型记录可以同文件归档。源码命名空间通常与目录对应，持久化类型和框架生成代码按兼容性要求处理。

### Domain

| 目录 | 职责 |
| --- | --- |
| `Aggregates/` | 聚合根及其内部实体，聚合行为负责保护业务不变量。 |
| `Entities/` | 有独立业务标识的领域实体，例如 `Note`。聚合内部实体随聚合归档。 |
| `ValueObjects/` | 由属性表示的业务值与快照，例如音高、标注位置和片段计划。 |
| `Enums/` | 多个领域对象共用的业务枚举。 |
| `Events/` | 领域内已经发生的业务事实。 |
| `Services/` | 需要多个领域对象参与的规则和计算，例如音高到简谱的转换。 |
| `Specifications/` | 可复用的业务判定条件。 |
| `Repositories/` | 领域本身需要的聚合仓储接口。用例需要的存储端口归 `Application/Interfaces/`。 |
| `Exceptions/` | 业务不变量或领域规则被违反时的领域异常。 |
| `Abstractions/` | 领域对象共用的纯 C# 抽象。 |

### Application

| 目录或文件 | 职责 |
| --- | --- |
| `Commands/` | 写操作的输入，例如保存原谱和片段修订。 |
| `Queries/` | 需要独立建模的查询输入。 |
| `Handlers/` | 如 `ScoreService` 和 `FragmentService` 这样同时协调同一功能读写的用例服务。 |
| `Handlers/Commands/`、`Handlers/Queries/` | 分别执行写用例及读用例，沿用直接调用与现有 DI。 |
| `Handlers/Events/Domain/` | 处理本领域的领域事件。 |
| `Handlers/Events/Integration/` | 执行由外部集成消息触发的用例。 |
| `Validators/` | 独立的应用输入验证，复用领域对象的业务规则。 |
| `DTOs/` | 应用用例的输入输出、查询投影及跨端口数据。 |
| `Interfaces/` | 用例需要的仓储、查询、文件和识谱等能力端口。 |
| `Authorization/` | 按任务凭据、操作者和资源范围检查用例访问权限。 |
| `Mappings/` | 已独立复用的领域对象、应用 DTO 和用例结果映射。 |
| `Exceptions/` | 应用用例错误，由 API 映射为 HTTP 响应。 |
| `Models/`、`Enums/` | 应用层拥有的任务、修订状态记录及识谱用例选项。 |
| `Serialization/` | 共享的任务快照与工具产物序列化约定。 |
| `DependencyInjection.cs` | 应用组件有独立注册需求时集中配置 Microsoft DI。 |

### Infrastructure

| 目录或文件 | 职责 |
| --- | --- |
| `Persistence/Contexts/` | EF Core `DbContext` 和集合入口。 |
| `Persistence/Configurations/` | 实体和值对象映射、索引与约束。 |
| `Persistence/Repositories/` | 仓储端口的实现。 |
| `Persistence/Queries/` | 独立的读取端口实现及 DTO 投影。 |
| `Persistence/Interceptors/` | EF Core 持久化拦截器。 |
| `Persistence/Migrations/` | 数据库迁移与模型快照。 |
| `Persistence/DesignTime/` | EF Core 设计时工厂。 |
| `Messaging/Configuration/` | 消息基础设施、Inbox 和 Outbox 配置。 |
| `Messaging/Contracts/`、`Messaging/Mappings/` | 传输类型，以及领域事件到集成事件的映射。 |
| `Messaging/Handlers/`、`Messaging/Sagas/` | 消息收发、事务适配与持久化流程协调。 |
| `Integrations/` | 外部系统的 HTTP、gRPC 或其他客户端。 |
| `Caching/`、`Identity/` | 缓存与会话适配、用户或租户身份上下文。 |
| `Storage/` | 原稿、任务目录和产物的文件存储实现。 |
| `Configuration/` | 基础设施 Options 和配置校验。 |
| `BackgroundJobs/` | Hangfire 调度与识谱、导出任务执行。 |
| `Recognition/` | 工具进程、识谱与拼谱引擎适配、运行依赖检查。 |
| `HealthChecks/`、`Observability/` | 依赖健康检查及任务诊断、指标和追踪。 |
| `DependencyInjection.cs` | 后端基础设施与 Worker 组件的注册入口。 |

### Presentation（`MusicRecognition.Api`）

| 目录或文件 | 职责 |
| --- | --- |
| `Controllers/` | MVC Controller，接收 HTTP 请求并调用应用用例。 |
| `Endpoints/` | 按功能组织当前 Minimal API 路由组、请求绑定与用例分发。 |
| `Contracts/Requests/`、`Contracts/Responses/` | HTTP 专属请求、响应模型。可直接复用的应用 DTO 随用例归档。 |
| `Authentication/`、`Authorization/` | HTTP 入口认证配置、授权策略及适配器。 |
| `Filters/` | MVC 或端点的过滤器。 |
| `Middleware/` | HTTP 管线扩展，例如响应头和异常响应。 |
| `OpenApi/` | OpenAPI 操作、请求与响应 Schema 定制。 |
| `Extensions/` | API 的 DI 和管线注册扩展，例如限流配置。 |
| `Properties/` | 本地开发启动配置。 |
| `Program.cs` | 应用入口与组合根，按顺序注册组件、管线和路由。 |

### 迁移与任务兼容

- `ScoreProcessor` 的完整类型名 `MusicRecognition.Infrastructure.ScoreProcessor` 和 `Recognize`、`Render` 方法签名供已入队的 Hangfire 任务恢复使用。
- `ScoreDb`、`ScoreJob`、`ScoreRevision` 和已生成的 EF 迁移使用原完整类型名。整理目录时保留迁移 ID 与内容，并检查 `dotnet ef migrations has-pending-model-changes`。新增迁移时使用 `--output-dir Persistence/Migrations --namespace MusicRecognition.Infrastructure.Migrations`。
- 文件落位后复查命名空间引用、已存在的运行数据与接口契约。ASP.NET Core 路由、`operationId`、请求参数和响应模型以实时 OpenAPI 与仓库快照对照验证。

## 恢复依赖与运行

- SDK 版本看 `global.json`，NuGet 版本集中在 `Directory.Packages.props`。前端从 `MusicRecognition.Web` 运行 `npm ci`，依赖以仓库中的 lockfile 为准。Python 及外部工具使用 [环境准备脚本](scripts/README.md) 中的清单和锁定文件。
- Windows 首次准备识谱工具链可在根目录运行 `pwsh -File scripts/setup/setup.ps1`。Audiveris 是默认引擎，由项目内的 `.tools/` 提供。需要 homr 时按环境准备说明恢复独立环境和模型。工具与模型的安装在准备阶段完成。
- 本机开发通过 Aspire AppHost 启动，先确认 Docker 可用于运行 PostgreSQL。启动后用 `aspire describe` 查当前端点，等 API 和 Worker 就绪再跑识谱流程。运行时与日志操作见 [项目 Aspire 技能](.agents/skills/aspire/SKILL.md) 和 [日志查询说明](docs/aspire-agent-support.md#复查命令)。

```powershell
$appHost = 'MusicRecognition.AppHost/MusicRecognition.AppHost.csproj'
aspire start --apphost $appHost --non-interactive
aspire describe --apphost $appHost --non-interactive
aspire logs worker --apphost $appHost --tail 50 --non-interactive
```

- 自托管容器使用 [Docker Compose 部署说明](deploy/README.md)。核对镜像实际安装的工具与模型，用容器环境单独验证识谱和导出。

## 修改契约与识谱流程

- 后端 OpenAPI 是前端契约的来源。从 `MusicRecognition.Web` 依次运行 `npm run api:pull -- <开发 API 根地址>`、`npm run api:generate` 和 `npm run api:check` 来更新快照与 SDK。由生成器维护 `shared/api/generated/`，遵守 `.gitattributes` 的换行约定。详见 [OpenAPI 与前端集成](docs/p3-api-query.md)。
- 修改原谱标注时检查音高、声部和原图符头坐标的对应关系。Audiveris 坐标从 `.omr` 通过 `ExportNotes.java` 提取。homr 的近似锚点交由用户确认与放置。验证导出保留原谱绘图、页序、旋转与裁剪几何，默认标注为黑色。
- 复现任务错误时，先用文件名、页码和任务 ID 查 Worker 日志，再查看任务目录中的受保护工具日志。访问令牌、数据库连接串、上传源文件和诊断内容应留在本地。提交前检查变更，避免纳入 `.tools/`、`output/`、`tmp/` 中的本机数据。

## 验证改动

- 从仓库根目录运行 `dotnet build MusicRecognition.slnx` 验证构建。已有服务运行时，可用 `--artifacts-path` 指定 `output/validation/` 下的独立构建目录。
- 恢复依赖后，从根目录运行 `pwsh -File tests/run.ps1`。脚本执行 Python、.NET、前端契约、布局、SDK 生成一致性、类型和 Lint 检查。需要实际 Python 工具和超时终止覆盖时，按 [测试说明](tests/README.md) 使用 `-Integration`。
- 改动识谱、坐标或导出时，按 [验收说明](tests/acceptance/README.md) 使用专用真实样本任务复跑。根据影响范围核查逐页识别、人工校对、整份标注导出或拼谱导出。识谱准确率需要人工真值，导出完整性用原谱与产物对照验证。
- GitHub Actions 以 [.github/workflows/ci.yml](.github/workflows/ci.yml) 为准。在报告中说明实际通过的是本地检查、真实样本验收还是远端运行结果。参见 [CI 说明](docs/ci.md)。
