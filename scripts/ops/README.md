# 运维入口

先由 Aspire 生成和构建部署资源，命令和实际结果见 [P5 报告](../../docs/p5-operations.md)。本目录使用 PowerShell 7 和 Docker CLI，不读取或打印容器密码，不使用全局 PostgreSQL 客户端。

```powershell
pwsh scripts/ops/backup.ps1 -ComposeFile output/p5-compose/docker-compose.yaml -EnvFile output/p5-compose/.env.p5 -Project musicrecognition-p5 -Destination output/p5-backups/<新备份目录>
pwsh scripts/ops/restore.ps1 -ComposeFile output/p5-compose/docker-compose.yaml -EnvFile output/p5-compose/.env.p5 -Project musicrecognition-restore-<新的演练名> -BackupDirectory output/p5-backups/<备份目录>
# 默认只预览整个文档家族的过期候选，不删除
pwsh scripts/ops/retention.ps1 -ComposeFile output/p5-compose/docker-compose.yaml -EnvFile output/p5-compose/.env.p5 -Project musicrecognition-p5 -Days 30
# 应用须显式 -Apply，并提供同项目最近 15 分钟内完成的完整备份
```

备份会短暂停止指定部署原本运行的 Web / API / Worker，在 `finally` 恢复它们；数据库和谱面卷在同一停写窗口成对备份，包含所有成功/失败任务及版本，文件有 SHA256 清单。备份目录含私人乐谱与数据库，应转存到有访问控制的独立介质。

恢复只允许新的 `musicrecognition-restore-*` 项目，没有覆盖现有卷或数据库的选项。数据库与文件恢复后，API / Worker 保持未启动，先核对产物，再决定切换服务和端口。

即使目标项目名是新的，也会拒绝复用已有/外部卷、数据库主机绑定目录和非空谱面目标卷，防止不同项目名意外指向同一份数据。

清理以整个原文档及其子页为单位：全部任务和修订均不活动，且整组最后更新时间超过保留期才入选。`-Apply` 在停写窗口重新计算候选；先删元数据再清文件，文件删除失败可能留下孤立目录，不能造成存续任务的原件丢失。默认不运行自动清理；本轮不会对开发任务应用清理。
