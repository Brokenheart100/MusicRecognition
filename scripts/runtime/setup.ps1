param([string]$Python = 'python', [switch]$WithOpenCV, [string]$PopplerDirectory = '', [string]$Font = 'C:/Windows/Fonts/seguisym.ttf')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$environmentPath = Join-Path $root '.tools/poc-python'
$projectPython = Join-Path $environmentPath $(if ($IsWindows) { 'Scripts/python.exe' } else { 'bin/python' })
if (!(Test-Path -LiteralPath $projectPython)) {
    & $Python -m venv $environmentPath
    if ($LASTEXITCODE) { throw 'Could not create the project Python environment.' }
}
$requirements = if ($WithOpenCV) { 'scripts/poc/requirements-opencv.txt' } else { 'scripts/poc/requirements.txt' }
& $projectPython -m pip install -r (Join-Path $root $requirements)
if ($LASTEXITCODE) { throw 'Could not install the pinned recognition dependencies.' }
& "$PSScriptRoot/prepare.ps1" -Python $projectPython -WithOpenCV:$WithOpenCV -PopplerDirectory $PopplerDirectory -Font $Font
Write-Output "Project Python ready: $projectPython. AppHost selects it by default."
