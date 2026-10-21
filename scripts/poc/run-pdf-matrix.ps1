[CmdletBinding()]
param([string]$Python='python',[string]$OutputDirectory='output/poc-pdf',[switch]$ReuseRecognition)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $repo
try {
    $root=[IO.Path]::GetFullPath($OutputDirectory,$repo)
    New-Item -ItemType Directory -Force "$root/input" | Out-Null
    $source=Join-Path $root 'input/Minuet-xpose-a4.pdf'
    if (!(Test-Path $source)) { Invoke-WebRequest 'https://www.mutopiaproject.org/ftp/BachJS/BWVAnh114/Minuet-xpose/Minuet-xpose-a4.pdf' -OutFile $source }
    if ((Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne '5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7') { throw 'Mutopia sample checksum mismatch' }
    & $Python "$PSScriptRoot/pdf_matrix.py" --source $source --root $root --prepare
    if ($LASTEXITCODE -ne 0) { throw 'PDF case preparation failed' }
    foreach ($name in @('full-r0','crop-r0')) {
        $inputPdf = if ($name -eq 'full-r0') { $source } else { Join-Path $root "input/cases/$name.pdf" }
        $options=@{InputPdf=$inputPdf;OutputDirectory=(Join-Path $root $name);Python=$Python}
        if ($ReuseRecognition -and (Test-Path "$root/$name/recognition-input.json")) { $options.ReuseRecognition=$true }
        & "$PSScriptRoot/run.ps1" @options
    }
    & $Python "$PSScriptRoot/pdf_matrix.py" --source $source --root $root
    if ($LASTEXITCODE -ne 0) { throw 'PDF matrix verification failed; inspect per-case logs' }
} finally { Pop-Location }
