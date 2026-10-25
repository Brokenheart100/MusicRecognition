$ErrorActionPreference = 'Stop'
function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE) { throw "Docker 操作失败，退出码 $LASTEXITCODE。" }
    return $result
}
function Initialize-Compose([string]$ComposeFile, [string]$EnvFile, [string]$Project) {
    if ($Project -notmatch '^[a-z0-9][a-z0-9-]{2,60}$') { throw '请明确指定合法的 Compose 项目名。' }
    $script:ComposeArgs = @('compose','--project-name',$Project,'--env-file',(Resolve-Path -LiteralPath $EnvFile).Path,'-f',(Resolve-Path -LiteralPath $ComposeFile).Path)
}
function Invoke-Compose([string[]]$Arguments) { Invoke-Docker ($script:ComposeArgs + $Arguments) }
function Get-Container([string]$Service) {
    $id = Invoke-Compose @('ps','--all','--quiet',$Service)
    if (!$id -or @($id).Count -ne 1) { throw "服务 $Service 必须恰有一个容器。" }
    (Invoke-Docker @('inspect',$id) | ConvertFrom-Json)[0]
}
function Get-ScoreVolume {
    $mount = (Get-Container 'api').Mounts | Where-Object { $_.Destination -eq '/data' -and $_.Type -eq 'volume' }
    if (@($mount).Count -ne 1) { throw 'API 必须使用唯一的 /data 命名卷。' }
    $mount.Name
}
function Test-Backup([string]$BackupDirectory) {
    $directory = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or !$manifest.quiesced) { throw '不支持的备份格式或未停写的备份。' }
    foreach ($name in @('database.dump','scores.tar.gz')) {
        if ((Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash -ne $manifest.hashes.$name) { throw "备份摘要不匹配：$name" }
    }
    $manifest
}
