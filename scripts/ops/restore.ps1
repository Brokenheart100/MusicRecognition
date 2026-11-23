# 在全新的隔离项目名里演练从备份恢复：先起数据库、灌入备份，再解包乐谱文件。
# 只允许 musicrecognition-restore-* 项目名，绝不碰现有环境；没有覆盖模式。
# 恢复完成后 API/Worker 保持停止，等人确认数据无误后再手动启动。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$BackupDirectory)
. "$PSScriptRoot/common.ps1"
# Restore drills always use a NEW namespace. There is no overwrite/--clean mode.
# 强制隔离命名，避免手误把恢复数据灌进正式项目。
if ($Project -notmatch '^musicrecognition-restore-[a-z0-9-]+$') { throw '恢复仅允许新的 musicrecognition-restore-* 隔离项目。' }
Initialize-Compose $ComposeFile $EnvFile $Project
# 先校验备份清单和哈希，坏备份不允许进入恢复流程。
$manifest = Test-Backup $BackupDirectory
$directory = (Resolve-Path -LiteralPath $BackupDirectory).Path
# 目标项目下已有容器或目录卷就直接拒绝，确保每次恢复都是干净环境。
if (@(Invoke-Compose @('ps','--all','--quiet')).Count -or @(Invoke-Docker @('volume','ls','--filter',"label=com.docker.compose.project=$Project",'--quiet')).Count) { throw '目标已有容器或卷，拒绝覆盖。' }
# 读出 Compose 配置，检查数据卷定义是否可安全使用。
$configuration=Invoke-Compose @('config','--format','json') | ConvertFrom-Json
$existingVolumes=@(Invoke-Docker @('volume','ls','--format','{{.Name}}'))
# 所有卷都必须是新建的命名卷，不能是 external 或已存在的卷。
foreach($definition in $configuration.volumes.PSObject.Properties) {
    if ($definition.Value.external -or $definition.Value.name -in $existingVolumes) { throw '恢复要求新的独立命名卷，不能复用已有或外部卷。' }
}
# PostgreSQL 数据目录必须挂命名卷；绑定宿主机目录会污染现有数据。
$databaseMount=@($configuration.services.'recognition-postgres'.volumes | Where-Object {$_.target -like '/var/lib/postgresql*'})
if ($databaseMount.Count -ne 1 -or $databaseMount[0].type -ne 'volume') { throw '恢复数据库必须使用新的命名卷，不能绑定现有主机目录。' }
# 只启动数据库并等它就绪，先不启动 API/Worker。
Invoke-Compose @('up','--detach','--wait','--wait-timeout','90','recognition-postgres') | Out-Null
# 只创建 API 容器（不启动），借它的镜像和卷定义来操作数据卷。
Invoke-Compose @('create','api') | Out-Null
$db = Get-Container 'recognition-postgres'; $api = Get-Container 'api'; $volume = Get-ScoreVolume
Invoke-Docker @('cp',(Join-Path $directory 'database.dump'),"$($db.Id):/tmp/musicrecognition-restore.dump") | Out-Null
# pg_restore 按备份里的建库语句重建数据库，出错立即停止。
Invoke-Docker @('exec',$db.Id,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -h 127.0.0.1 -U "$POSTGRES_USER" --dbname=postgres --create --exit-on-error /tmp/musicrecognition-restore.dump') | Out-Null
# 解包前确认乐谱卷为空，避免和已有文件混在一起。
Invoke-Docker @('run','--rm','--entrypoint','/opt/recognition/.tools/poc-python/bin/python','--mount',"type=volume,source=$volume,target=/data",$api.Config.Image,'-c',"from pathlib import Path; assert not any(Path('/data').iterdir()), 'Restore score volume must be empty'") | Out-Null
Invoke-Docker @('run','--rm','--user','0','--entrypoint','/bin/tar','--mount',"type=volume,source=$volume,target=/data",'--mount',"type=bind,source=$directory,target=/backup,readonly",$api.Config.Image,'-xzf','/backup/scores.tar.gz','-C','/data') | Out-Null
# Leave API/Worker stopped: inspect the restored DB/artifacts before exposing it.
# 输出恢复摘要，明确记录 API/Worker 未被启动。
@{ project=$Project; sourceProject=$manifest.project; restoredAt=[DateTimeOffset]::UtcNow.ToString('O'); apiStarted=$false; workerStarted=$false } | ConvertTo-Json
