$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$target = Join-Path $repo '.tools/musescore'
New-Item -ItemType Directory -Force $target | Out-Null
$package = Join-Path $target 'MuseScore-Studio-4.7.5-x86_64.msi'
$expected = '07b0f3292612e2df4c7e0632deb790add51b218f0ee33f7fecaa6111dd279ec6'
if (!(Test-Path -LiteralPath $package)) {
    Invoke-WebRequest 'https://github.com/musescore/MuseScore/releases/download/v4.7.5/MuseScore-Studio-4.7.5.260831071-x86_64.msi' -OutFile $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'MuseScore package SHA256 mismatch.' }
$binaries = @(Get-ChildItem -LiteralPath $target -Recurse -Filter MuseScore4.exe)
if (!$binaries.Count) {
    # Administrative extraction into this repository; no system-wide installation.
    $runtime = Join-Path $target 'runtime'
    $process = Start-Process msiexec.exe -ArgumentList @('/a', ('"'+$package+'"'), '/qn', ('TARGETDIR="'+$runtime+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010)) { throw "MuseScore extraction failed: $($process.ExitCode)" }
    $binaries = @(Get-ChildItem -LiteralPath $target -Recurse -Filter MuseScore4.exe)
}
if ($binaries.Count -ne 1) { throw 'Expected exactly one MuseScore4.exe in the extracted runtime.' }
@{ version='4.7.5'; executable=$binaries[0].FullName; packageSha256=$expected } | ConvertTo-Json | Set-Content (Join-Path $target 'runtime.json') -Encoding utf8
Write-Output "MuseScore 4.7.5 ready: $($binaries[0].FullName)"
