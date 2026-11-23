# Restore the complete pinned Windows runtime, including its adjacent DLLs.
# 在 Windows 上下载并还原锁定版本的 Poppler（PDF 转图片工具）。
# 必须连同 bin 旁边的 DLL 一起复制，只拷 exe 会启动失败；下载后做哈希校验。
# Linux 不需要此脚本，容器镜像里直接安装 Poppler。
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# 这个脚本只处理 Windows；其它系统请在镜像里装 Poppler。
if (!$IsWindows) { throw 'This provisioner is for Windows; Linux containers install Poppler through their image.' }
# 工具目录、Poppler 目标目录和安装包缓存路径。
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tools = Join-Path $repo '.tools'
$target = Join-Path $tools 'poppler'
$archive = Join-Path $tools 'Release-26.07.0-0.zip'
# 锁定版本的 SHA256，下载和缓存都要对照它。
$expected = 'a711b0563b06edc488583d28198b6734c5a494afbbd1b9d87d3d2866062fb7e2'
New-Item -ItemType Directory -Force $tools | Out-Null
# 缓存不存在才下载，避免每次都走网络。
if (!(Test-Path -LiteralPath $archive)) {
    # 先下到随机临时文件，校验通过后再转正，避免半截文件被当成缓存。
    $partial = Join-Path $tools ('poppler-download-' + [Guid]::NewGuid().ToString('N') + '.partial')
    try {
        # 限制连接和总下载时间，网络卡住时不会无限等待。
        Invoke-WebRequest 'https://github.com/oschwartz10612/poppler-windows/releases/download/v26.07.0-0/Release-26.07.0-0.zip' -OutFile $partial -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 300
        if ((Get-FileHash -LiteralPath $partial).Hash.ToLowerInvariant() -ne $expected) { throw 'Poppler download SHA256 mismatch.' }
        # 只有哈希正确才转正为正式缓存文件。
        Move-Item -LiteralPath $partial -Destination $archive
    # 无论成功失败都删掉临时文件。
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}
# 复用缓存前再校验一次，防止缓存损坏。
if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $expected) { throw "Poppler cache SHA256 mismatch: $archive" }
# 先解压到随机暂存目录，成功后再整体搬运。
$stage = Join-Path $tools ('poppler-prepare-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $stage
    # 归档里应恰好有一个 bin/pdftoppm.exe，用来定位库根目录。
    $candidates = @(Get-ChildItem -LiteralPath $stage -Recurse -Filter pdftoppm.exe)
    # 目录结构不符合预期就中止，避免拷错东西。
    if ($candidates.Count -ne 1 -or $candidates[0].Directory.Name -ne 'bin') { throw 'Unexpected Poppler archive layout.' }
    $library = $candidates[0].Directory.Parent.FullName
    # 把整个库目录（含 DLL）复制到 .tools/poppler。
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem -LiteralPath $library | Copy-Item -Destination $target -Recurse -Force
    # 试运行 -v，能打印版本才算可用。
    & (Join-Path $target 'bin/pdftoppm.exe') -v
    if ($LASTEXITCODE) { throw 'Restored Poppler cannot start; inspect its DLLs and local protection history.' }
    Write-Output "Poppler 26.07.0 restored: $target"
} finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = Get-Item -LiteralPath $stage -Force
        # 只允许删除 .tools 下以 poppler-prepare- 开头的普通目录。
        $prefix = [IO.Path]::GetFullPath($tools) + [IO.Path]::DirectorySeparatorChar
        # 前缀、目录名、非符号链接三重检查，防止误删。
        if (!$resolved.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            !$resolved.Name.StartsWith('poppler-prepare-') -or ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to clean an unexpected Poppler extraction path.'
        }
        Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
    }
}
