# 检查并补齐"跑识别"所需的运行时：Audiveris、JDK、Poppler、字体和 Python 依赖。
# 不下载大件工具，只做校验和收尾；下载由 setup.ps1 和各自的 provision 脚本负责。
# 最后编译一次 Java 适配器并跑 preflight 自检，全部通过才算环境就绪。
param([string]$Python = '', [switch]$WithOpenCV, [string]$PopplerDirectory = '', [string]$Font = 'C:/Windows/Fonts/seguisym.ttf')
$ErrorActionPreference = 'Stop'
# 仓库根目录（本脚本位于 scripts/setup，向上两级）。
$root = (Resolve-Path "$PSScriptRoot/../..").Path
# 没传 -Python 就用项目内置环境：Windows 在 Scripts 下，Linux 在 bin 下。
if (!$Python) { $Python = Join-Path $root $(if ($IsWindows) { '.tools/poc-python/Scripts/python.exe' } else { '.tools/poc-python/bin/python' }) }
# Audiveris 是可执行目录，JDK 提供它需要的 Java 编译器和运行环境。
$app = Join-Path $root '.tools/audiveris/Audiveris'
$compiler = Join-Path $root '.tools/jdk25/jdk-25.0.4.1+1/bin/javac.exe'
# 缺工具就直接报错，提示先运行 provision 脚本；不在这里偷偷下载。
if (!(Test-Path $compiler) -or !(Test-Path "$app/runtime/bin/java.exe")) { throw 'Provision the pinned P0 Audiveris/JDK tools first; see tests/acceptance/README.md.' }
# 试导入关键 Python 库，缺哪个就说明依赖没装好。
& $Python -c 'import PIL,numpy,pypdf,reportlab,fontTools'
if ($LASTEXITCODE) { throw 'Install scripts/setup/requirements.txt in the selected Python environment.' }
# 只有明确要求 OpenCV 时才检查它，它是可选能力。
if ($WithOpenCV) {
    & $Python -c 'import cv2; print("Optional OpenCV ready:", cv2.__version__)'
    if ($LASTEXITCODE) { throw 'Install scripts/setup/requirements-opencv.txt to enable Adaptive preprocessing.' }
}
# Poppler 负责把 PDF 每页转成图片，识别前必须能调用 pdftoppm。
$projectPoppler = Join-Path $root '.tools/poppler'
$projectRenderer = Join-Path $projectPoppler 'bin/pdftoppm.exe'
# 没指定 Poppler 目录、或项目内还没有时，进入查找/安装流程。
if ($PopplerDirectory -or !(Test-Path -LiteralPath $projectRenderer)) {
    if (!$PopplerDirectory) {
        # 先看系统 PATH 里有没有 pdftoppm，有就直接复用。
        $renderer = Get-Command pdftoppm -ErrorAction SilentlyContinue
        if ($renderer) { $PopplerDirectory = Split-Path (Split-Path $renderer.Source) }
        # Windows 上没有就调用 provision-poppler.ps1 下载到 .tools。
        elseif ($IsWindows) {
            & "$PSScriptRoot/provision-poppler.ps1"
            $PopplerDirectory = $projectPoppler
        }
        # 其它系统必须显式传入便携版 Poppler 目录。
        else { throw 'Supply -PopplerDirectory <portable Poppler Library folder containing bin/pdftoppm>.' }
    }
    $PopplerDirectory = (Resolve-Path -LiteralPath $PopplerDirectory).Path
    # 指定目录必须真的含 bin/pdftoppm.exe，否则后面识别会失败。
    if (!(Test-Path -LiteralPath (Join-Path $PopplerDirectory 'bin/pdftoppm.exe'))) {
        throw '-PopplerDirectory must contain bin/pdftoppm.exe and its runtime DLLs.'
    }
    if ($PopplerDirectory -ne $projectPoppler) {
        New-Item -ItemType Directory -Force $projectPoppler | Out-Null
        # 复制到项目内，让运行时不再依赖外部目录。
        Get-ChildItem -LiteralPath $PopplerDirectory | Copy-Item -Destination $projectPoppler -Recurse -Force
    }
}
# Java 适配器（导出 OMR 结果）的 .class 输出目录。
$classes = Join-Path $root '.tools/poc-classes'
New-Item -ItemType Directory -Force $classes | Out-Null
# 用 Audiveris 自带的库编译 ExportNotes.java。
& $compiler --release 25 -encoding UTF-8 -cp "$app/app/*" -d $classes "$root/scripts/runtime/ExportNotes.java"
if ($LASTEXITCODE) { throw 'Java adapter compilation failed.' }
# 跑一次 preflight，逐项确认工具、字体和 Python 库都能用。
& $Python "$root/scripts/runtime/preflight.py" --root $root --pdftoppm $projectRenderer --font $Font
if ($LASTEXITCODE) { throw 'Recognition dependency validation failed.' }
Write-Output "Recognition runtime ready: $Python. AppHost defaults to .tools/poc-python; explicit overrides remain supported."
