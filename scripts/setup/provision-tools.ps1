# Provision pinned portable Audiveris/JDK tools without a global installation.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tools = Join-Path $repo '.tools'
New-Item -ItemType Directory -Force $tools | Out-Null
function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "Checksum mismatch: $Path" }
}
# Portable administrative extraction; does not install Audiveris system-wide.
$msi = Join-Path $tools 'Audiveris-5.11.0-windowsConsole-x86_64.msi'
Get-VerifiedFile 'https://github.com/Audiveris/audiveris/releases/download/5.11.0/Audiveris-5.11.0-windowsConsole-x86_64.msi' $msi '5f1b4e96a12c53c7da426814b76e599363c4181e291855996e0a6878dda95f71'
$audiveris = Join-Path $tools 'audiveris/Audiveris'
if (!(Test-Path "$audiveris/Audiveris.exe") -or !(Test-Path "$audiveris/runtime/bin/java.exe") -or !(Test-Path "$audiveris/app/audiveris.jar")) {
    $extract = Join-Path $tools 'audiveris'
    $extractLog = Join-Path $tools 'audiveris-extract.log'
    $process = Start-Process msiexec.exe -ArgumentList @('/a',('"'+$msi+'"'),'/qn',('TARGETDIR="'+$extract+'"'),'/l*v',('"'+$extractLog+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "MSI extraction failed: $($process.ExitCode)" }
}
$jdkArchive = Join-Path $tools 'jdk25.zip'
Get-VerifiedFile 'https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip' $jdkArchive '00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283'
$jdk = Join-Path $tools 'jdk25/jdk-25.0.4.1+1'
if (!(Test-Path "$jdk/bin/javac.exe")) { Expand-Archive -LiteralPath $jdkArchive -DestinationPath "$tools/jdk25" -Force }

[pscustomobject]@{ Audiveris = $audiveris; Jdk = $jdk }
