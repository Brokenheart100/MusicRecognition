[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$BackupDestination,
      [Parameter(Mandatory)][string]$Output)
. "$PSScriptRoot/../../scripts/ops/common.ps1"
Initialize-Compose $ComposeFile $EnvFile $Project
if ((Resolve-Path -LiteralPath $ComposeFile).Path -notlike '*output*p5-compose*') { throw '只允许专用 P5 验证部署。' }
$id=[Guid]::NewGuid();$hex=$id.ToString('N');$db=Get-Container 'recognition-postgres'
$sql=@"
INSERT INTO "Jobs" ("Id","TokenHash","RequestHash","FileHash","FileName","Extension","State","Attempts","CurrentRevision","CancelRequested","CreatedAt","UpdatedAt","Preprocessing","PageNumber","PageCount","PagesPrepared")
VALUES ('$id',repeat('0',64),'$hex',repeat('0',64),'P5-retention-fixture.png','.png','Ready',0,0,false,now()-interval '40 days',now()-interval '40 days',0,1,1,true);
"@
$sql | & docker exec -i $db.Id sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d recognitiondb -v ON_ERROR_STOP=1 -At' | Out-Null
if ($LASTEXITCODE) { throw '不能建立专用清理夹具。' }
Invoke-Compose @('exec','-T','api','/opt/recognition/.tools/poc-python/bin/python','-c',"from pathlib import Path;p=Path('/data/$hex');p.mkdir();(p/'source.png').write_text('P5 expired fixture')") | Out-Null
$ops=Join-Path $PSScriptRoot '../../scripts/ops'
$preview=& "$ops/retention.ps1" -ComposeFile $ComposeFile -EnvFile $EnvFile -Project $Project -Days 30 | ConvertFrom-Json
if ($preview.count -ne 1 -or $preview.jobIds[0] -ne $id.ToString()) { throw '预览候选不等于专用夹具，拒绝应用清理。' }
& "$ops/backup.ps1" -ComposeFile $ComposeFile -EnvFile $EnvFile -Project $Project -Destination $BackupDestination | Out-Null
$applied=& "$ops/retention.ps1" -ComposeFile $ComposeFile -EnvFile $EnvFile -Project $Project -Days 30 -Apply -BackupDirectory $BackupDestination | ConvertFrom-Json
if ($applied.deletedJobs -ne 1) { throw '清理未删除预期的一个过期夹具。' }
$after=& "$ops/retention.ps1" -ComposeFile $ComposeFile -EnvFile $EnvFile -Project $Project -Days 30 | ConvertFrom-Json
if ($after.count -ne 0) { throw '清理后候选未归零。' }
$result=@{fixtureId=$id; previewCount=$preview.count; deletedJobs=$applied.deletedJobs; remainingExpired=$after.count; backup=$BackupDestination; productionDataTouched=$false}
$result | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
$result | ConvertTo-Json
