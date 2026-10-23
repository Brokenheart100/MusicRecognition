param([string]$Python = '', [switch]$WithOpenCV, [string]$PopplerDirectory = '', [string]$Font = 'C:/Windows/Fonts/seguisym.ttf')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$Python) { $Python = Join-Path $root $(if ($IsWindows) { '.tools/poc-python/Scripts/python.exe' } else { '.tools/poc-python/bin/python' }) }
$app = Join-Path $root '.tools/audiveris/Audiveris'
$compiler = Join-Path $root '.tools/jdk25/jdk-25.0.4.1+1/bin/javac.exe'
if (!(Test-Path $compiler) -or !(Test-Path "$app/runtime/bin/java.exe")) { throw 'Provision the pinned P0 Audiveris/JDK tools first; see scripts/poc/README.md.' }
& $Python -c 'import PIL,numpy,pypdf,reportlab,fontTools'
if ($LASTEXITCODE) { throw 'Install scripts/poc/requirements.txt in the selected Python environment.' }
if ($WithOpenCV) {
    & $Python -c 'import cv2; print("Optional OpenCV ready:", cv2.__version__)'
    if ($LASTEXITCODE) { throw 'Install scripts/poc/requirements-opencv.txt to enable Adaptive preprocessing.' }
}
$projectPoppler = Join-Path $root '.tools/poppler'
$projectRenderer = Join-Path $projectPoppler 'bin/pdftoppm.exe'
if ($PopplerDirectory -or !(Test-Path -LiteralPath $projectRenderer)) {
    if (!$PopplerDirectory) {
        $renderer = Get-Command pdftoppm -ErrorAction SilentlyContinue
        if (!$renderer) { throw 'Supply -PopplerDirectory <portable Poppler Library folder containing bin/pdftoppm.exe>.' }
        $PopplerDirectory = Split-Path (Split-Path $renderer.Source)
    }
    $PopplerDirectory = (Resolve-Path -LiteralPath $PopplerDirectory).Path
    if (!(Test-Path -LiteralPath (Join-Path $PopplerDirectory 'bin/pdftoppm.exe'))) {
        throw '-PopplerDirectory must contain bin/pdftoppm.exe and its runtime DLLs.'
    }
    if ($PopplerDirectory -ne $projectPoppler) {
        New-Item -ItemType Directory -Force $projectPoppler | Out-Null
        Get-ChildItem -LiteralPath $PopplerDirectory | Copy-Item -Destination $projectPoppler -Recurse -Force
    }
}
$classes = Join-Path $root '.tools/poc-classes'
New-Item -ItemType Directory -Force $classes | Out-Null
& $compiler --release 25 -encoding UTF-8 -cp "$app/app/*" -d $classes "$root/scripts/poc/ExportNotes.java"
if ($LASTEXITCODE) { throw 'Java adapter compilation failed.' }
& $Python "$PSScriptRoot/preflight.py" --root $root --pdftoppm $projectRenderer --font $Font
if ($LASTEXITCODE) { throw 'Recognition dependency validation failed.' }
Write-Output "Recognition runtime ready: $Python. AppHost defaults to .tools/poc-python; explicit overrides remain supported."
