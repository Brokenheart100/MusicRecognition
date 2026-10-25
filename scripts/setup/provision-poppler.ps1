# Restore the complete pinned Windows runtime, including its adjacent DLLs.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This provisioner is for Windows; Linux containers install Poppler through their image.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tools = Join-Path $repo '.tools'
$target = Join-Path $tools 'poppler'
$archive = Join-Path $tools 'Release-26.07.0-0.zip'
$expected = 'a711b0563b06edc488583d28198b6734c5a494afbbd1b9d87d3d2866062fb7e2'
New-Item -ItemType Directory -Force $tools | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    $partial = Join-Path $tools ('poppler-download-' + [Guid]::NewGuid().ToString('N') + '.partial')
    try {
        Invoke-WebRequest 'https://github.com/oschwartz10612/poppler-windows/releases/download/v26.07.0-0/Release-26.07.0-0.zip' -OutFile $partial -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 300
        if ((Get-FileHash -LiteralPath $partial).Hash.ToLowerInvariant() -ne $expected) { throw 'Poppler download SHA256 mismatch.' }
        Move-Item -LiteralPath $partial -Destination $archive
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}
if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $expected) { throw "Poppler cache SHA256 mismatch: $archive" }
$stage = Join-Path $tools ('poppler-prepare-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $stage
    $candidates = @(Get-ChildItem -LiteralPath $stage -Recurse -Filter pdftoppm.exe)
    if ($candidates.Count -ne 1 -or $candidates[0].Directory.Name -ne 'bin') { throw 'Unexpected Poppler archive layout.' }
    $library = $candidates[0].Directory.Parent.FullName
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem -LiteralPath $library | Copy-Item -Destination $target -Recurse -Force
    & (Join-Path $target 'bin/pdftoppm.exe') -v
    if ($LASTEXITCODE) { throw 'Restored Poppler cannot start; inspect its DLLs and local protection history.' }
    Write-Output "Poppler 26.07.0 restored: $target"
} finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = Get-Item -LiteralPath $stage -Force
        $prefix = [IO.Path]::GetFullPath($tools) + [IO.Path]::DirectorySeparatorChar
        if (!$resolved.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            !$resolved.Name.StartsWith('poppler-prepare-') -or ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to clean an unexpected Poppler extraction path.'
        }
        Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
    }
}
