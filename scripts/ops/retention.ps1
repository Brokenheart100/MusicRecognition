# 清理过期的识别任务及其文件（数据库记录 + /data 下的任务目录）。
# 默认只做试算：列出会被删的任务，不真删；加 -Apply 才执行。
# 真删除前强制要求 15 分钟内、同一项目的备份，防止误删无法找回。
# 判定按任务家族整体进行：多页任务的所有分页和修订一起保留或一起删除。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[ValidateRange(1,36500)][int]$Days=30,
      [switch]$Apply,[string]$BackupDirectory='')
. "$PSScriptRoot/common.ps1"
# 初始化 compose 项目，并按服务名找到数据库/API 容器和乐谱卷。
Initialize-Compose $ComposeFile $EnvFile $Project
$db=Get-Container 'recognition-postgres'; $api=Get-Container 'api'; $volume=Get-ScoreVolume
# 用 psql 执行 SQL；-At 让结果每行一个值、无表头，方便程序解析。
function Invoke-Psql([string]$Sql) {
    $result = $Sql | & docker exec -i $db.Id sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d recognitiondb -v ON_ERROR_STOP=1 -At'
    if ($LASTEXITCODE) { throw '清理数据库操作失败。' }; $result
}
# 整个清理过程放在 try 里，保证最后把停掉的服务恢复。
$running=@()
try {
    if ($Apply) {
        # 真删之前必须有备份目录，否则拒绝执行。
        if (!$BackupDirectory) { throw '应用清理前必须提供最近的完整备份。' }
        # 备份必须是本项目的，且是最近 15 分钟内生成的（说明刚备份过）。
        $backup=Test-Backup $BackupDirectory
        if ($backup.project -ne $Project -or [DateTimeOffset]::Parse($backup.createdAt) -lt [DateTimeOffset]::UtcNow.AddMinutes(-15)) { throw '备份须属于该项目且在最近 15 分钟内完成。' }
        # 记录运行中的业务服务并暂停，避免删除过程中还有任务在写。
        $running=@(Invoke-Compose @('ps','--status','running','--services') | Where-Object { $_ -in @('api','worker','web') })
        if ($running.Count) { Invoke-Compose (@('stop','--timeout','40')+$running) | Out-Null }
    }
    # Multi-page parents keep a preparation state; eligibility is decided for
    # their ENTIRE family and all revisions, never one page independently.
    # 下面这段 SQL 找出整族都过期的父任务：最近没更新、没有排队/处理中的子任务或修订。
    $sql=@"
# eligible：满足删除条件的父任务集合。
WITH eligible AS (
 SELECT r."Id" FROM "Jobs" r WHERE r."ParentId" IS NULL
 AND NOT EXISTS (SELECT 1 FROM "Jobs" j WHERE (j."Id"=r."Id" OR j."ParentId"=r."Id") AND (
 j."UpdatedAt">=now()-make_interval(days=>$Days) OR
 ((j."PageCount"<=1 OR NOT j."PagesPrepared") AND j."State" IN ('Queued','Processing','Cancelling'))))
 AND NOT EXISTS (SELECT 1 FROM "Revisions" v JOIN "Jobs" j ON j."Id"=v."JobId"
 WHERE (j."Id"=r."Id" OR j."ParentId"=r."Id") AND v."State" IN ('Queued','Processing'))
) SELECT COALESCE(json_agg(j."Id"::text),'[]'::json) FROM "Jobs" j
WHERE j."Id" IN (SELECT "Id" FROM eligible) OR j."ParentId" IN (SELECT "Id" FROM eligible);
"@
    # 执行查询，把结果当作 JSON 数组转成任务 ID 列表。
    $ids=@((Invoke-Psql $sql | ConvertFrom-Json))
    # 每个 ID 必须是标准 UUID，防止后续拼 SQL 时混入脏数据。
    foreach($id in $ids) { if($id -notmatch '^[0-9a-f-]{36}$') { throw '无效清理标识。' } }
    # 试算模式或没有可删任务：只输出清单并结束。
    if (!$Apply -or !$ids.Count) { @{apply=[bool]$Apply; days=$Days; jobIds=$ids; count=$ids.Count} | ConvertTo-Json -Depth 4; return }
    # 把 ID 拼成 SQL 的 uuid 值列表，供批量删除使用。
    $values=($ids | ForEach-Object { "'$_'::uuid" }) -join ','
    # 先删修订、再删子任务、最后删父任务，遵守外键顺序。
    Invoke-Psql "BEGIN; DELETE FROM `"Revisions`" WHERE `"JobId`" IN ($values); DELETE FROM `"Jobs`" WHERE `"Id`" IN ($values) AND `"ParentId`" IS NOT NULL; DELETE FROM `"Jobs`" WHERE `"Id`" IN ($values); COMMIT;" | Out-Null
    # Metadata is removed first. A filesystem failure can leave an orphan, but
    # cannot leave a surviving task whose original file was deleted.
    # 在容器里执行 Python 删除 /data 下的任务目录；目录名是任务 ID 去掉短横线的十六进制。
    # Python 里会拒绝符号链接和越界路径，只删 /data 下的一级目录。
    $python=@'
import json,pathlib,shutil,sys,uuid
root=pathlib.Path('/data').resolve()
for value in json.loads(sys.argv[1]):
    path=root/uuid.UUID(value).hex
    if path.is_symlink() or path.resolve().parent!=root: raise ValueError('Unsafe cleanup path')
    if path.exists(): shutil.rmtree(path)
'@
    # 用与 API 相同的镜像跑 Python，保证乐谱卷挂载路径一致。
    Invoke-Docker @('run','--rm','--entrypoint','/opt/recognition/.tools/poc-python/bin/python','--mount',"type=volume,source=$volume,target=/data",$api.Config.Image,'-c',$python,($ids | ConvertTo-Json -AsArray -Compress)) | Out-Null
    # 输出本次清理摘要，包含删除数量和所用备份。
    @{apply=$true; days=$Days; deletedJobs=$ids.Count; backup=$BackupDirectory} | ConvertTo-Json
} finally { if ($running.Count) { Invoke-Compose (@('start')+$running) | Out-Null } }
