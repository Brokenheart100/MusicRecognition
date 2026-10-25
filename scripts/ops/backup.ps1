[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$Destination)
. "$PSScriptRoot/common.ps1"
Initialize-Compose $ComposeFile $EnvFile $Project
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw '备份必须写入新的目录，不能覆盖旧备份。' }
$db = Get-Container 'recognition-postgres'
$api = Get-Container 'api'; $volume = Get-ScoreVolume
$running = @(Invoke-Compose @('ps','--status','running','--services') | Where-Object { $_ -in @('api','worker','web') })
New-Item -ItemType Directory -Path $destinationPath | Out-Null
try {
    if ($running.Count) { Invoke-Compose (@('stop','--timeout','40') + $running) | Out-Null }
    Invoke-Docker @('exec',$db.Id,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h 127.0.0.1 -U "$POSTGRES_USER" --format=custom --create --file=/tmp/musicrecognition-backup.dump recognitiondb') | Out-Null
    Invoke-Docker @('cp',"$($db.Id):/tmp/musicrecognition-backup.dump",(Join-Path $destinationPath 'database.dump')) | Out-Null
    Invoke-Docker @('run','--rm','--user','0','--entrypoint','/bin/tar','--mount',"type=volume,source=$volume,target=/data,readonly",'--mount',"type=bind,source=$destinationPath,target=/backup",$api.Config.Image,'-czf','/backup/scores.tar.gz','-C','/data','.') | Out-Null
    $hashes = @{}; foreach ($name in @('database.dump','scores.tar.gz')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $destinationPath $name)).Hash }
    @{ format=1; project=$Project; createdAt=[DateTimeOffset]::UtcNow.ToString('O'); quiesced=$true; hashes=$hashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationPath 'manifest.json') -Encoding utf8
    Write-Output "一致性备份已完成：$destinationPath。请将整个目录转存至独立备份介质。"
} finally {
    if ($running.Count) { Invoke-Compose (@('start') + $running) | Out-Null }
}
