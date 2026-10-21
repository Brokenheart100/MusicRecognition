[CmdletBinding()]
param(
    [string]$InputImage,
    [string]$InputPdf,
    [string]$OutputDirectory = 'output/poc',
    [string]$Python = 'python',
    [string]$Font = 'C:/Windows/Fonts/seguisym.ttf',
    [switch]$ReuseRecognition
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $repo
try {
    $tools = Join-Path $repo '.tools'
    $output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
    New-Item -ItemType Directory -Force $tools,$output | Out-Null

    function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
        if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
        $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Sha256) { throw "Checksum mismatch: $Path" }
    }

    # Portable administrative extraction; does not install Audiveris system-wide.
    $msi = Join-Path $tools 'Audiveris-5.11.0-windowsConsole-x86_64.msi'
    Get-VerifiedFile 'https://github.com/Audiveris/audiveris/releases/download/5.11.0/Audiveris-5.11.0-windowsConsole-x86_64.msi' $msi '5f1b4e96a12c53c7da426814b76e599363c4181e291855996e0a6878dda95f71'
    $audiveris = Join-Path $tools 'audiveris/Audiveris'
    if (!(Test-Path "$audiveris/Audiveris.exe")) {
        $extract = Join-Path $tools 'audiveris'
        $extractLog = Join-Path $tools 'audiveris-extract.log'
        $process = Start-Process msiexec.exe -ArgumentList @('/a',('"'+$msi+'"'),'/qn',('TARGETDIR="'+$extract+'"'),'/l*v',('"'+$extractLog+'"')) -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "MSI extraction failed: $($process.ExitCode)" }
    }
    $jdkArchive = Join-Path $tools 'jdk25.zip'
    Get-VerifiedFile 'https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip' $jdkArchive '00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283'
    $jdk = Join-Path $tools 'jdk25/jdk-25.0.4.1+1'
    if (!(Test-Path "$jdk/bin/javac.exe")) { Expand-Archive -LiteralPath $jdkArchive -DestinationPath "$tools/jdk25" -Force }

    $inputDir = Join-Path $output 'input'
    New-Item -ItemType Directory -Force $inputDir | Out-Null
    if ($InputImage -and $InputPdf) { throw 'Choose either InputImage or InputPdf' }
    if ($InputPdf) {
        $InputPdf = (Resolve-Path -LiteralPath $InputPdf).Path
        & $Python "$PSScriptRoot/pdf_geometry.py" --pdf $InputPdf --output $inputDir
        if ($LASTEXITCODE -ne 0) { throw 'PDF preparation failed' }
        $InputImage = Join-Path $inputDir 'recognition.png'
    } elseif (!$InputImage) {
        $InputImage = Join-Path $inputDir 'BachInvention5.jpg'
        Get-VerifiedFile 'https://raw.githubusercontent.com/Audiveris/audiveris/7a36078e7ba0c006052c1f661b949cf9b729f505/data/examples/BachInvention5.jpg' $InputImage '9ab9a9a8ebf609d60a7d0cddcccd5ffc91d433812fd86153244595371d282805'
    }
    $InputImage = (Resolve-Path -LiteralPath $InputImage).Path
    $stem = [IO.Path]::GetFileNameWithoutExtension($InputImage)
    $omrDir = Join-Path $output 'omr'
    New-Item -ItemType Directory -Force $omrDir | Out-Null
    $omr = Join-Path $omrDir "$stem.omr"
    $mxl = Join-Path $omrDir "$stem.mxl"
    $recognitionManifest = Join-Path $output 'recognition-input.json'
    $inputHash = (Get-FileHash -LiteralPath $InputImage -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ReuseRecognition) {
        if (!(Test-Path $recognitionManifest) -or !(Test-Path $omr) -or !(Test-Path $mxl)) { throw 'Reuse requires existing recognition files and manifest' }
        $previous = Get-Content $recognitionManifest -Raw | ConvertFrom-Json
        if ($previous.sha256 -ne $inputHash -or $previous.engine -ne '5.11.0') { throw 'Cached recognition does not match this input or engine' }
        if ($previous.omrSha256 -ne (Get-FileHash $omr -Algorithm SHA256).Hash.ToLowerInvariant() -or $previous.mxlSha256 -ne (Get-FileHash $mxl -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Cached recognition artifacts changed' }
    } else {
        # Image input creates a new Book. 5.11.0 -force on a fresh image throws in reachStep.
        # A fresh run directory also keeps a failed run from replacing valid cached artifacts.
        $runDirectory = Join-Path $omrDir ('run-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $runDirectory | Out-Null
        & "$audiveris/Audiveris.exe" -batch -transcribe -export -save -output $runDirectory -- $InputImage *> "$output/audiveris.log"
        $runOmr = Join-Path $runDirectory "$stem.omr"
        $runMxl = Join-Path $runDirectory "$stem.mxl"
        if ($LASTEXITCODE -ne 0 -or !(Test-Path $runOmr) -or !(Test-Path $runMxl)) { throw "Audiveris failed; inspect $output/audiveris.log" }
        Copy-Item -LiteralPath $runOmr -Destination $omr
        Copy-Item -LiteralPath $runMxl -Destination $mxl
        @{sha256=$inputHash;engine='5.11.0';input=$InputImage;omrSha256=(Get-FileHash $omr -Algorithm SHA256).Hash.ToLowerInvariant();mxlSha256=(Get-FileHash $mxl -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content $recognitionManifest -Encoding utf8
    }

    $classes = Join-Path $tools 'poc-classes'
    New-Item -ItemType Directory -Force $classes | Out-Null
    $classpath = (Get-ChildItem "$audiveris/app" -Filter '*.jar' | ForEach-Object FullName) -join ';'
    & "$jdk/bin/javac.exe" --release 25 -encoding UTF-8 -cp $classpath -d $classes "$PSScriptRoot/ExportNotes.java"
    if ($LASTEXITCODE -ne 0) { throw 'Audiveris adapter compilation failed' }
    $runtimeClasspath = $classes+';'+$audiveris+'/app/*'
    & "$audiveris/runtime/bin/java.exe" '-Djava.awt.headless=true' '-Dfile.encoding=UTF-8' '-Djpackage.app-version=5.11.0' "-Dmusicrecognition.output=$output" '--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED' '--enable-native-access=ALL-UNNAMED' -cp $runtimeClasspath Audiveris -batch -run musicrecognition.poc.ExportNotes -- $omr *> "$output/export-notes.log"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path "$output/notes-1.json")) { throw 'Note export failed; inspect export-notes.log' }
    $annotationOptions = @('--image',$InputImage,'--omr',$omr,'--mxl',$mxl,'--notes',"$output/notes-1.json",'--output',$output,'--font',$Font)
    if ($InputPdf) { $annotationOptions += @('--pdf-source',$InputPdf) }
    & $Python "$PSScriptRoot/annotate.py" @annotationOptions
    if ($LASTEXITCODE -ne 0) { throw 'Annotation failed' }
    if (Get-Command pdftoppm -ErrorAction SilentlyContinue) {
        $notes = Get-Content "$output/notes-1.json" -Raw | ConvertFrom-Json
        $displayWidth,$displayHeight = $notes.width,$notes.height
        if ($InputPdf) {
            $geometry = Get-Content "$inputDir/pdf-geometry.json" -Raw | ConvertFrom-Json
            $displayWidth,$displayHeight = $geometry.displaySize
        }
        foreach ($name in @('original','annotated')) {
            if ($InputPdf) {
                & pdftoppm -png -singlefile -cropbox -r 216 "$output/$name.pdf" "$output/$name-render"
            } else {
                & pdftoppm -png -singlefile -cropbox -scale-to-x $displayWidth -scale-to-y $displayHeight "$output/$name.pdf" "$output/$name-render"
            }
            if ($LASTEXITCODE -ne 0) { throw "PDF rendering failed: $name" }
        }
        & $Python "$PSScriptRoot/verify.py" --output $output
        if ($LASTEXITCODE -ne 0) { throw 'Geometry / raster verification failed' }
    } else {
        Write-Warning 'pdftoppm missing: PDF raster verification was not run. Install Poppler and run verify.py after rendering.'
    }
    Write-Output "Diagnostic preview: $output/preview.html"
} finally {
    Pop-Location
}
