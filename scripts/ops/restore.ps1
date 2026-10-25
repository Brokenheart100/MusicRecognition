[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$BackupDirectory)
. "$PSScriptRoot/common.ps1"
# Restore drills always use a NEW namespace. There is no overwrite/--clean mode.
if ($Project -notmatch '^musicrecognition-restore-[a-z0-9-]+$') { throw '恢复仅允许新的 musicrecognition-restore-* 隔离项目。' }
Initialize-Compose $ComposeFile $EnvFile $Project
$manifest = Test-Backup $BackupDirectory
$directory = (Resolve-Path -LiteralPath $BackupDirectory).Path
if (@(Invoke-Compose @('ps','--all','--quiet')).Count -or @(Invoke-Docker @('volume','ls','--filter',"label=com.docker.compose.project=$Project",'--quiet')).Count) { throw '目标已有容器或卷，拒绝覆盖。' }
$configuration=Invoke-Compose @('config','--format','json') | ConvertFrom-Json
$existingVolumes=@(Invoke-Docker @('volume','ls','--format','{{.Name}}'))
foreach($definition in $configuration.volumes.PSObject.Properties) {
    if ($definition.Value.external -or $definition.Value.name -in $existingVolumes) { throw '恢复要求新的独立命名卷，不能复用已有或外部卷。' }
}
$databaseMount=@($configuration.services.'recognition-postgres'.volumes | Where-Object {$_.target -like '/var/lib/postgresql*'})
if ($databaseMount.Count -ne 1 -or $databaseMount[0].type -ne 'volume') { throw '恢复数据库必须使用新的命名卷，不能绑定现有主机目录。' }
Invoke-Compose @('up','--detach','--wait','--wait-timeout','90','recognition-postgres') | Out-Null
Invoke-Compose @('create','api') | Out-Null
$db = Get-Container 'recognition-postgres'; $api = Get-Container 'api'; $volume = Get-ScoreVolume
Invoke-Docker @('cp',(Join-Path $directory 'database.dump'),"$($db.Id):/tmp/musicrecognition-restore.dump") | Out-Null
Invoke-Docker @('exec',$db.Id,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -h 127.0.0.1 -U "$POSTGRES_USER" --dbname=postgres --create --exit-on-error /tmp/musicrecognition-restore.dump') | Out-Null
Invoke-Docker @('run','--rm','--entrypoint','/opt/recognition/.tools/poc-python/bin/python','--mount',"type=volume,source=$volume,target=/data",$api.Config.Image,'-c',"from pathlib import Path; assert not any(Path('/data').iterdir()), 'Restore score volume must be empty'") | Out-Null
Invoke-Docker @('run','--rm','--user','0','--entrypoint','/bin/tar','--mount',"type=volume,source=$volume,target=/data",'--mount',"type=bind,source=$directory,target=/backup,readonly",$api.Config.Image,'-xzf','/backup/scores.tar.gz','-C','/data') | Out-Null
# Leave API/Worker stopped: inspect the restored DB/artifacts before exposing it.
@{ project=$Project; sourceProject=$manifest.project; restoredAt=[DateTimeOffset]::UtcNow.ToString('O'); apiStarted=$false; workerStarted=$false } | ConvertTo-Json
