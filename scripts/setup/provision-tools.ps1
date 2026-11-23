# Provision pinned portable Audiveris/JDK tools without a global installation.
# 下载并校验项目锁定的 Audiveris 与 JDK，全部解压到仓库 .tools 目录。
# 不装进系统全局，删掉 .tools 就能还原干净；版本号和哈希写死在脚本里。
# 输出两个工具的实际路径，供 prepare.ps1 和运行时读取。
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# 仓库根目录与统一的工具目录（.tools 不进版本库）。
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tools = Join-Path $repo '.tools'
New-Item -ItemType Directory -Force $tools | Out-Null
# 下载文件并校验 SHA256：文件已存在也校验，防止缓存被篡改或下坏。
function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    # 只有文件不存在时才下载，重复执行不会重复拉取。
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "Checksum mismatch: $Path" }
}
# Portable administrative extraction; does not install Audiveris system-wide.
$msi = Join-Path $tools 'Audiveris-5.11.0-windowsConsole-x86_64.msi'
Get-VerifiedFile 'https://github.com/Audiveris/audiveris/releases/download/5.11.0/Audiveris-5.11.0-windowsConsole-x86_64.msi' $msi '5f1b4e96a12c53c7da426814b76e599363c4181e291855996e0a6878dda95f71'
# 解压后的 Audiveris 主目录，下面应有 Audiveris.exe、runtime、app。
$audiveris = Join-Path $tools 'audiveris/Audiveris'
# 三个关键文件缺任意一个就重新解压 MSI。
if (!(Test-Path "$audiveris/Audiveris.exe") -or !(Test-Path "$audiveris/runtime/bin/java.exe") -or !(Test-Path "$audiveris/app/audiveris.jar")) {
    $extract = Join-Path $tools 'audiveris'
    # MSI 解压过程的详细日志，出问题时可查。
    $extractLog = Join-Path $tools 'audiveris-extract.log'
    # /a 是管理式解压：只把文件摊开，不注册、不安装到系统。
    $process = Start-Process msiexec.exe -ArgumentList @('/a',('"'+$msi+'"'),'/qn',('TARGETDIR="'+$extract+'"'),'/l*v',('"'+$extractLog+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "MSI extraction failed: $($process.ExitCode)" }
}
# JDK 压缩包与解压位置；Audiveris 编译 Java 适配器时要用。
$jdkArchive = Join-Path $tools 'jdk25.zip'
Get-VerifiedFile 'https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip' $jdkArchive '00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283'
$jdk = Join-Path $tools 'jdk25/jdk-25.0.4.1+1'
# 只解压一次，已有 javac 就跳过。
if (!(Test-Path "$jdk/bin/javac.exe")) { Expand-Archive -LiteralPath $jdkArchive -DestinationPath "$tools/jdk25" -Force }

# 返回两个路径，调用方按需使用。
[pscustomobject]@{ Audiveris = $audiveris; Jdk = $jdk }
