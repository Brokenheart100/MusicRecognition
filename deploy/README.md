# Docker Compose 部署

目标为 Docker Desktop / Docker Engine 的 **Linux amd64** 容器。Audiveris / MuseScore 官方发布包固定此架构；尚未验证 ARM64。使用 PowerShell 7、.NET 10、Aspire CLI 13.6 和 Docker Compose v2。

```powershell
aspire deploy --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --environment p5 --output-path output/p5-compose --non-interactive
```

首次构建需要访问 Microsoft、Docker Hub、Ubuntu、PyPI、NuGet 与 GitHub 官方发布源。所有运行时依赖在镜像构建期安装，上传识谱期间不下载工具。包版本仍由 `Directory.Packages.props`、前端 lockfile 和 Python 清单管理，Audiveris / MuseScore 下载摘要见 `toolchain.lock.json`。

AppHost 在发布模式生成以下资源，开发模式继续使用本机项目和现有开发卷：

| 服务 | 边界 |
| --- | --- |
| Web | Nuxt Node 生产服务，`127.0.0.1:3080`；流式代理到 API，不先缓存完整上传 |
| API | 仅 Compose 内网 8080；共享 `/data` 卷，显式能力凭据，写入默认 20 次/分钟、4 个并发请求 |
| Worker | 仅内网 8080；Hangfire 默认 1 个工作线程，2 CPU / 4 GiB；识别工具单次最多 900 秒 |
| PostgreSQL | 独立持久卷，仅内网 5432；与开发数据库卷分离 |
| Aspire Dashboard | 仅 `127.0.0.1:18888`，用于部署遥测；浏览器令牌与读取 API key 由 secret 参数生成，不对公网开放。镜像按已验证 SHA256 固定 |

部署模型只公开本机回环地址，适合在主机上用现有 TLS 反向代理暴露 Web。远程域名、HTTPS 证书、管理员访问控制和防火墙需要按实际主机配置，本次本机部署不能替代这些验收。

API / Worker 以 UID 1654、Web 以 Node 用户运行，限制 CPU / 内存，丢弃额外 Linux capabilities，禁止提权，并限制日志轮转。Worker 使用经 SHA256 校验的 Linux Audiveris 和 MuseScore，后者通过隔离 Xvfb 转换，无需连接主机图形桌面。

生成的 `.env.p5` 含部署密码，整个 `output/` 被 Git 忽略；不要把它纳入源码、截图或报告。备份与恢复说明见 [ops](../scripts/ops/README.md)，真实验收状态见 [P5 报告](../docs/p5-operations.md)。不使用 `docker compose down -v` 或 `aspire destroy` 做日常停止，它们可能删除持久数据。

Compose 仪表板的登录令牌取自 `.env.p5` 中的 `DASHBOARD_API_KEY`，CLI 读取遥测时使用同值作为 `--api-key`。

限流是单 API 实例的全局写入预算；不会信任客户端自行声明的 IP 或 token 来划分额度。扩大为多 API / 多 Worker 副本前，需重新配置共享限额、存储与容量，不应直接横向扩容并沿用本机结果。

需要计算的 `/reflow` GET 也受相同预算限制，普通任务轮询和下载不受该预算影响。API / Worker / Web 使用只读根文件系统及私有临时目录；JNA / JNI 所在缓存需要可执行映射，因此临时挂载显式保留 `exec`。任务按目录与凭据隔离，共用同一 Worker 容器，不是每任务独立的操作系统沙箱。
