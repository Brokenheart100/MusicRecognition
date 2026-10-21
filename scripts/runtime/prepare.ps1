param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$app = Join-Path $root '.tools/audiveris/Audiveris'
$compiler = Join-Path $root '.tools/jdk25/jdk-25.0.4.1+1/bin/javac.exe'
if (!(Test-Path $compiler) -or !(Test-Path "$app/runtime/bin/java.exe")) { throw 'Provision the pinned P0 Audiveris/JDK tools first; see scripts/poc/README.md.' }
& $Python -c 'import PIL,numpy,pypdf,reportlab'
if ($LASTEXITCODE) { throw 'Install scripts/poc/requirements.txt in the selected Python environment.' }
if (!(Get-Command pdftoppm -ErrorAction SilentlyContinue)) { throw 'Install Poppler and add pdftoppm to PATH.' }
$classes = Join-Path $root '.tools/poc-classes'
New-Item -ItemType Directory -Force $classes | Out-Null
& $compiler --release 25 -encoding UTF-8 -cp "$app/app/*" -d $classes "$root/scripts/poc/ExportNotes.java"
if ($LASTEXITCODE) { throw 'Java adapter compilation failed.' }
Write-Output 'Recognition runtime ready. Set MUSICRECOGNITION_PYTHON to this Python executable before aspire start.'
