# 一键准备"第一次运行"所需的本机环境，Windows 首次安装时执行。
# 依次：建项目专用 Python 虚拟环境 → 装锁定版本依赖 → 下载校验 Audiveris/JDK
# → 准备 Poppler 等运行时。所有东西都放在仓库 .tools 下，不污染系统环境。
param([string]$Python = 'python', [switch]$WithOpenCV, [string]$PopplerDirectory = '', [string]$Font = 'C:/Windows/Fonts/seguisym.ttf')
$ErrorActionPreference = 'Stop'
# 仓库根目录：脚本在 scripts/setup 下，向上两级就是仓库根。
$root = (Resolve-Path "$PSScriptRoot/../..").Path
# 项目专用 Python 环境放进仓库的 .tools 目录，方便整体删除或迁移。
$environmentPath = Join-Path $root '.tools/poc-python'
# Windows 的解释器在 Scripts 下，Linux/macOS 在 bin 下。
$projectPython = Join-Path $environmentPath $(if ($IsWindows) { 'Scripts/python.exe' } else { 'bin/python' })
if (!(Test-Path -LiteralPath $projectPython)) {
    & $Python -m venv $environmentPath
    if ($LASTEXITCODE) { throw 'Could not create the project Python environment.' }
}
# 普通安装与带 OpenCV 的安装使用不同依赖清单，OpenCV 是可选的图像增强能力。
$requirements = if ($WithOpenCV) { 'scripts/setup/requirements-opencv.txt' } else { 'scripts/setup/requirements.txt' }
& $projectPython -m pip install -r (Join-Path $root $requirements)
if ($LASTEXITCODE) { throw 'Could not install the pinned recognition dependencies.' }
# 下载并校验 Audiveris 与 JDK（识别引擎和它的 Java 运行环境）。
& "$PSScriptRoot/provision-tools.ps1" | Out-Null
# 校验 Poppler、字体、Java 适配器等运行时，并试编译一次。
& "$PSScriptRoot/prepare.ps1" -Python $projectPython -WithOpenCV:$WithOpenCV -PopplerDirectory $PopplerDirectory -Font $Font
Write-Output "Project Python ready: $projectPython. AppHost selects it by default."
