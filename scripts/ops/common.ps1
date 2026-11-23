# 备份/恢复/清理三个运维脚本共用的函数库，由其它脚本点号引入（不是独立入口）。
# 统一封装 docker compose 调用、容器查找、目录卷定位和备份校验。
# 约定：Compose 项目名必须由调用方显式传入，防止误操作到别人的环境。
$ErrorActionPreference = 'Stop'
# 执行一条 docker 命令；退出码非 0 直接抛错，调用方不用每条都判断。
function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE) { throw "Docker 操作失败，退出码 $LASTEXITCODE。" }
    return $result
}
# 记住 Compose 文件、环境变量文件和项目名，后续命令都带着它们执行。
function Initialize-Compose([string]$ComposeFile, [string]$EnvFile, [string]$Project) {
    # 项目名限定小写字母/数字/短横线且至少 3 位，避免和系统默认项目重名。
    if ($Project -notmatch '^[a-z0-9][a-z0-9-]{2,60}$') { throw '请明确指定合法的 Compose 项目名。' }
    # 路径先转成绝对路径，防止切换工作目录后 compose 找不到文件。
    $script:ComposeArgs = @('compose','--project-name',$Project,'--env-file',(Resolve-Path -LiteralPath $EnvFile).Path,'-f',(Resolve-Path -LiteralPath $ComposeFile).Path)
}
function Invoke-Compose([string[]]$Arguments) { Invoke-Docker ($script:ComposeArgs + $Arguments) }
# 按服务名取恰好一个容器，多容器或少容器都视为异常。
function Get-Container([string]$Service) {
    # --all 连已停止的容器一起查；--quiet 只输出容器 ID。
    $id = Invoke-Compose @('ps','--all','--quiet',$Service)
    if (!$id -or @($id).Count -ne 1) { throw "服务 $Service 必须恰有一个容器。" }
    (Invoke-Docker @('inspect',$id) | ConvertFrom-Json)[0]
}
# 找到 API 容器挂载的 /data 命名卷，乐谱文件都在这个卷里。
function Get-ScoreVolume {
    $mount = (Get-Container 'api').Mounts | Where-Object { $_.Destination -eq '/data' -and $_.Type -eq 'volume' }
    # /data 必须是唯一一个命名卷，否则备份/恢复会找错目标。
    if (@($mount).Count -ne 1) { throw 'API 必须使用唯一的 /data 命名卷。' }
    $mount.Name
}
# 校验备份目录：清单格式正确、已停写、两个产物哈希都对得上。
function Test-Backup([string]$BackupDirectory) {
    $directory = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw | ConvertFrom-Json
    # format=1 是当前备份格式；quiesced 表示备份时已暂停写入。
    if ($manifest.format -ne 1 -or !$manifest.quiesced) { throw '不支持的备份格式或未停写的备份。' }
    # 数据库导出和乐谱文件归档两个产物都要核对哈希。
    foreach ($name in @('database.dump','scores.tar.gz')) {
        if ((Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash -ne $manifest.hashes.$name) { throw "备份摘要不匹配：$name" }
    }
    $manifest
}
