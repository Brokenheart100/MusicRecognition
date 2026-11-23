[CmdletBinding()]
param([string]$Uv = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$lock = Get-Content "$PSScriptRoot/homr.lock.json" -Raw | ConvertFrom-Json
$runtime = Join-Path $repo '.tools/homr'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
if (!$Uv) {
    if (!$IsWindows) { throw '在 Linux 上安装 uv，并通过 -Uv 指定它的路径；此脚本的自动 uv 下载只覆盖 Windows。' }
    $Uv = Join-Path $runtime 'uv/uv.exe'
    if (!(Test-Path -LiteralPath $Uv)) {
        $zip = Join-Path $runtime 'uv.zip'
        Invoke-WebRequest "https://github.com/astral-sh/uv/releases/download/$($lock.uv.version)/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 300
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.uv.windowsSha256) { throw 'uv 下载摘要不匹配。' }
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $runtime 'uv') -Force
    }
}
if ((& $Uv --version) -notmatch "^uv $([regex]::Escape($lock.uv.version))(?:\s|$)") { throw 'uv 版本与 homr.lock.json 不一致。' }
$previousPythonDir=$env:UV_PYTHON_INSTALL_DIR; $previousCache=$env:UV_CACHE_DIR
try {
    $env:UV_PYTHON_INSTALL_DIR=Join-Path $runtime 'python'
    $env:UV_CACHE_DIR=Join-Path $runtime 'cache'
    $python = Join-Path $runtime $(if ($IsWindows) { 'venv/Scripts/python.exe' } else { 'venv/bin/python' })
    if (!(Test-Path -LiteralPath $python)) {
        & $Uv venv (Join-Path $runtime 'venv') --python $lock.python --managed-python
        if ($LASTEXITCODE) { throw 'homr 独立 Python 创建失败。' }
    }
    & $Uv pip sync --python $python "$PSScriptRoot/requirements-homr.txt"
    if ($LASTEXITCODE) { throw 'homr 依赖恢复失败。' }
    & $Uv pip check --python $python
    if ($LASTEXITCODE) { throw 'homr 依赖一致性检查失败。' }
    $package = & $python -c 'import homr,pathlib; print(pathlib.Path(homr.__file__).parent)'
    if ($LASTEXITCODE) { throw 'homr 导入失败。' }
    foreach ($model in $lock.models) {
        $target = Join-Path $package $model.path
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $model.sha256) { continue }
        $name = [IO.Path]::GetFileNameWithoutExtension($target)
        $zip = Join-Path $runtime "$name.zip"
        if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.zipSha256) {
            Invoke-WebRequest "https://github.com/liebharc/homr/releases/download/onnx_checkpoints/$name.zip" -OutFile $zip -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 600
        }
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.zipSha256) { throw "模型下载摘要不匹配：$name" }
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = @($archive.Entries | Where-Object { $_.FullName -eq "$name.onnx" })
            if ($entry.Count -ne 1) { throw "模型归档布局不符合固定版本：$name" }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $target, $true)
        } finally { $archive.Dispose() }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) { throw "模型文件摘要不匹配：$name" }
    }
    $lock | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'runtime.json') -Encoding utf8
    & $python "$repo/scripts/runtime/homr_runner.py" --check
    if ($LASTEXITCODE) { throw 'homr 运行环境验证失败。' }
    Write-Output 'homr CPU 独立环境与模型已就绪；Audiveris/Python 原有环境未修改。'
} finally {
    $env:UV_PYTHON_INSTALL_DIR=$previousPythonDir; $env:UV_CACHE_DIR=$previousCache
}
