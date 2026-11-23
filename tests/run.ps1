[CmdletBinding()]
param([string]$Python = '', [switch]$Integration)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
if (!$Python) { $Python = Join-Path $repo $(if ($IsWindows) { '.tools/poc-python/Scripts/python.exe' } else { '.tools/poc-python/bin/python' }) }
if (!(Test-Path -LiteralPath $Python)) { throw '请先运行 scripts/setup/setup.ps1，或通过 -Python 指定项目解释器。' }
Push-Location $repo
try {
    & $Python -m unittest discover -s tests/python -p 'test_*.py'
    if ($LASTEXITCODE) { throw 'Python 测试失败。' }
    dotnet test tests/MusicRecognition.Domain.Tests/MusicRecognition.Domain.Tests.csproj
    if ($LASTEXITCODE) { throw '领域测试失败。' }
    if ($Integration) {
        # Scope environment overrides to this runner, even when a test fails.
        $previousRoot = $env:MUSICRECOGNITION_TEST_ROOT
        $previousPython = $env:MUSICRECOGNITION_PYTHON
        try {
            $env:MUSICRECOGNITION_TEST_ROOT = $repo
            $env:MUSICRECOGNITION_PYTHON = $Python
            dotnet test tests/MusicRecognition.Infrastructure.Tests/MusicRecognition.Infrastructure.Tests.csproj
            if ($LASTEXITCODE) { throw '基础设施集成测试失败。' }
        } finally {
            $env:MUSICRECOGNITION_TEST_ROOT = $previousRoot
            $env:MUSICRECOGNITION_PYTHON = $previousPython
        }
    } else {
        dotnet test tests/MusicRecognition.Infrastructure.Tests/MusicRecognition.Infrastructure.Tests.csproj --filter 'Category!=Integration'
        if ($LASTEXITCODE) { throw '基础设施测试失败。' }
    }
    Push-Location MusicRecognition.Web
    try {
        foreach ($check in @('test:contract', 'test:unit', 'api:check', 'typecheck', 'lint')) {
            npm run $check
            if ($LASTEXITCODE) { throw "前端检查失败：$check" }
        }
    } finally { Pop-Location }
    Write-Output '检查通过；真实业务流程与浏览器验收需按 tests/README.md 单独运行。'
} finally { Pop-Location }
