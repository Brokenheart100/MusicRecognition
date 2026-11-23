# 为 homr 识别引擎单独准备一套隔离环境（uv + Python + 模型），只装 CPU 版本。
# 不碰 Audiveris 和主 Python 环境；版本全部以 homr.lock.json 为准。
# 装完会把清单写进 runtime.json 并跑一次 --check 自检。
[CmdletBinding()]
param([string]$Uv = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
# 锁定清单：记录 uv 版本、Python 版本、每个模型文件的路径和哈希。
$lock = Get-Content "$PSScriptRoot/homr.lock.json" -Raw | ConvertFrom-Json
# homr 的所有文件都放在 .tools/homr，隔离且可整体删除。
$runtime = Join-Path $repo '.tools/homr'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
# 没指定 uv 时，Windows 下自动下载锁定版本；其它系统必须传 -Uv。
if (!$Uv) {
    if (!$IsWindows) { throw '在 Linux 上安装 uv，并通过 -Uv 指定它的路径；此脚本的自动 uv 下载只覆盖 Windows。' }
    $Uv = Join-Path $runtime 'uv/uv.exe'
    if (!(Test-Path -LiteralPath $Uv)) {
        # 下载锁定版本的 uv 压缩包到运行时目录。
        $zip = Join-Path $runtime 'uv.zip'
        Invoke-WebRequest "https://github.com/astral-sh/uv/releases/download/$($lock.uv.version)/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 300
        # 下载内容哈希必须与锁定文件一致。
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.uv.windowsSha256) { throw 'uv 下载摘要不匹配。' }
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $runtime 'uv') -Force
    }
}
# 运行 uv --version 再核对一次，防止用了错版本的 uv。
if ((& $Uv --version) -notmatch "^uv $([regex]::Escape($lock.uv.version))(?:\s|$)") { throw 'uv 版本与 homr.lock.json 不一致。' }
# 临时改掉 uv 的下载/缓存目录，结束时要恢复。
$previousPythonDir=$env:UV_PYTHON_INSTALL_DIR; $previousCache=$env:UV_CACHE_DIR
try {
    # Python 解释器和缓存都放项目内，不污染用户目录。
    $env:UV_PYTHON_INSTALL_DIR=Join-Path $runtime 'python'
    $env:UV_CACHE_DIR=Join-Path $runtime 'cache'
    # 虚拟环境里的解释器；不存在时才创建。
    $python = Join-Path $runtime $(if ($IsWindows) { 'venv/Scripts/python.exe' } else { 'venv/bin/python' })
    if (!(Test-Path -LiteralPath $python)) {
        # 用锁定版本创建独立的 Python 虚拟环境。
        & $Uv venv (Join-Path $runtime 'venv') --python $lock.python --managed-python
        if ($LASTEXITCODE) { throw 'homr 独立 Python 创建失败。' }
    }
    # pip sync 按 requirements 精确对齐版本（多了会删、少了会装）。
    & $Uv pip sync --python $python "$PSScriptRoot/requirements-homr.txt"
    if ($LASTEXITCODE) { throw 'homr 依赖恢复失败。' }
    # pip check 检查依赖之间有没有冲突。
    & $Uv pip check --python $python
    if ($LASTEXITCODE) { throw 'homr 依赖一致性检查失败。' }
    # 找到 homr 安装包所在目录，模型文件要放进它旁边。
    $package = & $python -c 'import homr,pathlib; print(pathlib.Path(homr.__file__).parent)'
    if ($LASTEXITCODE) { throw 'homr 导入失败。' }
    # 逐个检查锁定清单里的模型文件。
    foreach ($model in $lock.models) {
        $target = Join-Path $package $model.path
        # 目标文件已存在且哈希正确就跳过，不重复下载。
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $model.sha256) { continue }
        $name = [IO.Path]::GetFileNameWithoutExtension($target)
        # 模型以 zip 形式发布，先缓存到运行时目录。
        $zip = Join-Path $runtime "$name.zip"
        # 缓存不存在或哈希不对就重新下载。
        if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.zipSha256) {
            Invoke-WebRequest "https://github.com/liebharc/homr/releases/download/onnx_checkpoints/$name.zip" -OutFile $zip -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 600
        }
        # 下载后再次校验 zip 哈希。
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.zipSha256) { throw "模型下载摘要不匹配：$name" }
        # 打开模型压缩包，只取里面同名的 .onnx 文件。
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            # 归档里必须恰好有一个同名模型文件。
            $entry = @($archive.Entries | Where-Object { $_.FullName -eq "$name.onnx" })
            if ($entry.Count -ne 1) { throw "模型归档布局不符合固定版本：$name" }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            # 解压到 homr 包目录，覆盖旧模型文件。
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $target, $true)
        } finally { $archive.Dispose() }
        # 解压后的模型再校验一次，确保文件完整。
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) { throw "模型文件摘要不匹配：$name" }
    }
    # 把锁定清单原样存一份，方便排查装的是哪个版本。
    $lock | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'runtime.json') -Encoding utf8
    # 调用 homr 自检脚本，确认能加载模型。
    & $python "$repo/scripts/runtime/homr_runner.py" --check
    if ($LASTEXITCODE) { throw 'homr 运行环境验证失败。' }
    Write-Output 'homr CPU 独立环境与模型已就绪；Audiveris/Python 原有环境未修改。'
} finally {
    # 收尾：恢复被临时改动的 uv 环境变量。
    $env:UV_PYTHON_INSTALL_DIR=$previousPythonDir; $env:UV_CACHE_DIR=$previousCache
}
