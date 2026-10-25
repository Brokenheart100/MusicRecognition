[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[ValidateRange(1,36500)][int]$Days=30,
      [switch]$Apply,[string]$BackupDirectory='')
. "$PSScriptRoot/common.ps1"
Initialize-Compose $ComposeFile $EnvFile $Project
$db=Get-Container 'recognition-postgres'; $api=Get-Container 'api'; $volume=Get-ScoreVolume
function Invoke-Psql([string]$Sql) {
    $result = $Sql | & docker exec -i $db.Id sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d recognitiondb -v ON_ERROR_STOP=1 -At'
    if ($LASTEXITCODE) { throw '清理数据库操作失败。' }; $result
}
$running=@()
try {
    if ($Apply) {
        if (!$BackupDirectory) { throw '应用清理前必须提供最近的完整备份。' }
        $backup=Test-Backup $BackupDirectory
        if ($backup.project -ne $Project -or [DateTimeOffset]::Parse($backup.createdAt) -lt [DateTimeOffset]::UtcNow.AddMinutes(-15)) { throw '备份须属于该项目且在最近 15 分钟内完成。' }
        $running=@(Invoke-Compose @('ps','--status','running','--services') | Where-Object { $_ -in @('api','worker','web') })
        if ($running.Count) { Invoke-Compose (@('stop','--timeout','40')+$running) | Out-Null }
    }
    # Multi-page parents keep a preparation state; eligibility is decided for
    # their ENTIRE family and all revisions, never one page independently.
    $sql=@"
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
    $ids=@((Invoke-Psql $sql | ConvertFrom-Json))
    foreach($id in $ids) { if($id -notmatch '^[0-9a-f-]{36}$') { throw '无效清理标识。' } }
    if (!$Apply -or !$ids.Count) { @{apply=[bool]$Apply; days=$Days; jobIds=$ids; count=$ids.Count} | ConvertTo-Json -Depth 4; return }
    $values=($ids | ForEach-Object { "'$_'::uuid" }) -join ','
    Invoke-Psql "BEGIN; DELETE FROM `"Revisions`" WHERE `"JobId`" IN ($values); DELETE FROM `"Jobs`" WHERE `"Id`" IN ($values) AND `"ParentId`" IS NOT NULL; DELETE FROM `"Jobs`" WHERE `"Id`" IN ($values); COMMIT;" | Out-Null
    # Metadata is removed first. A filesystem failure can leave an orphan, but
    # cannot leave a surviving task whose original file was deleted.
    $python=@'
import json,pathlib,shutil,sys,uuid
root=pathlib.Path('/data').resolve()
for value in json.loads(sys.argv[1]):
    path=root/uuid.UUID(value).hex
    if path.is_symlink() or path.resolve().parent!=root: raise ValueError('Unsafe cleanup path')
    if path.exists(): shutil.rmtree(path)
'@
    Invoke-Docker @('run','--rm','--entrypoint','/opt/recognition/.tools/poc-python/bin/python','--mount',"type=volume,source=$volume,target=/data",$api.Config.Image,'-c',$python,($ids | ConvertTo-Json -AsArray -Compress)) | Out-Null
    @{apply=$true; days=$Days; deletedJobs=$ids.Count; backup=$BackupDirectory} | ConvertTo-Json
} finally { if ($running.Count) { Invoke-Compose (@('start')+$running) | Out-Null } }
