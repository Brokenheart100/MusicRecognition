# 生成一份一致性备份：数据库导出 + 乐谱文件归档 + 校验清单，三件套缺一不可。
# 备份前暂停会写数据的服务，备份后自动恢复，保证数据库和目录卷在同一时刻。
# 这是运维脚本，执行者需传入 Compose 文件、环境文件和项目名。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComposeFile,[Parameter(Mandatory)][string]$EnvFile,
      [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$Destination)
# 引入公共函数（docker compose 封装、容器/目录卷查找、备份校验）。
. "$PSScriptRoot/common.ps1"
Initialize-Compose $ComposeFile $EnvFile $Project
# 目标目录必须是全新目录，禁止覆盖已有备份。
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw '备份必须写入新的目录，不能覆盖旧备份。' }
$db = Get-Container 'recognition-postgres'
$api = Get-Container 'api'; $volume = Get-ScoreVolume
# 记录当前正在运行的业务服务，稍后按原样恢复。
$running = @(Invoke-Compose @('ps','--status','running','--services') | Where-Object { $_ -in @('api','worker','web') })
New-Item -ItemType Directory -Path $destinationPath | Out-Null
# 从这里开始先停写、再备份；finally 里保证服务一定被重新拉起。
try {
    # 停掉 api/worker/web，避免备份过程中还有写入。
    if ($running.Count) { Invoke-Compose (@('stop','--timeout','40') + $running) | Out-Null }
    # pg_dump 导出整个库（含建库语句），先写到数据库容器内的临时文件。
    Invoke-Docker @('exec',$db.Id,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h 127.0.0.1 -U "$POSTGRES_USER" --format=custom --create --file=/tmp/musicrecognition-backup.dump recognitiondb') | Out-Null
    Invoke-Docker @('cp',"$($db.Id):/tmp/musicrecognition-backup.dump",(Join-Path $destinationPath 'database.dump')) | Out-Null
    # 起一个临时容器，把 /data 目录卷只读挂载后用 tar 打包。
    Invoke-Docker @('run','--rm','--user','0','--entrypoint','/bin/tar','--mount',"type=volume,source=$volume,target=/data,readonly",'--mount',"type=bind,source=$destinationPath,target=/backup",$api.Config.Image,'-czf','/backup/scores.tar.gz','-C','/data','.') | Out-Null
    # 计算两个产物的 SHA256，写进清单供恢复前校验。
    $hashes = @{}; foreach ($name in @('database.dump','scores.tar.gz')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $destinationPath $name)).Hash }
    # manifest 记录格式版本、项目名、时间、已停写标记和哈希。
    @{ format=1; project=$Project; createdAt=[DateTimeOffset]::UtcNow.ToString('O'); quiesced=$true; hashes=$hashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationPath 'manifest.json') -Encoding utf8
    Write-Output "一致性备份已完成：$destinationPath。请将整个目录转存至独立备份介质。"
# 无论成功失败都恢复之前停掉的服务。
} finally {
    if ($running.Count) { Invoke-Compose (@('start') + $running) | Out-Null }
}
