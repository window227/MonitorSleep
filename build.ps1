<#
.SYNOPSIS
    构建 显示器睡眠助手。

.DESCRIPTION
    把 .NET SDK、NuGet 缓存、临时目录全部限制在工作区内的 .tools 下，
    因此在受限（沙箱）环境里也能反复构建，不触碰工作区以外的路径。

    首次使用需先装 SDK（工作区内，不需要管理员）：
        .\.tools\dotnet-install.ps1 -Channel 8.0 -InstallDir .\.tools\dotnet -NoPath

.EXAMPLE
    .\build.ps1                       # Release 编译
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Publish              # 生成 dist\Release\MonitorSleep.exe（依赖已装的 .NET 8 桌面运行时）
    .\build.ps1 -Publish -SelfContained  # 自包含单文件（需要联网还原运行时包，约 150MB）
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$tools  = Join-Path $root '.tools'
$dotnet = Join-Path $tools 'dotnet\dotnet.exe'
$proj   = Join-Path $root 'MonitorSleep\MonitorSleep.csproj'

if (-not (Test-Path $dotnet)) {
    throw @"
未找到 .NET SDK：$dotnet
请先在工作区内安装（不需要管理员权限）：
    & "$tools\dotnet-install.ps1" -Channel 8.0 -InstallDir "$tools\dotnet" -NoPath
"@
}

foreach ($d in 'tmp', 'clihome', 'nuget', 'appdata') {
    New-Item -ItemType Directory -Force -Path (Join-Path $tools $d) | Out-Null
}

# 把所有可变路径都关进工作区，避免被沙箱或权限策略挡住
$env:TEMP                        = Join-Path $tools 'tmp'
$env:TMP                         = Join-Path $tools 'tmp'
$env:DOTNET_CLI_HOME             = Join-Path $tools 'clihome'
$env:NUGET_PACKAGES              = Join-Path $tools 'nuget'
$env:DOTNET_ROOT                 = Join-Path $tools 'dotnet'
$env:DOTNET_NOLOGO               = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE     = '1'

$common = @('-c', $Configuration, '-v', 'minimal', '--nologo', '-nodeReuse:false')

if (-not $Publish) {
    & $dotnet build $proj @common --no-restore
    exit $LASTEXITCODE
}

$out = Join-Path $root "dist\$Configuration"
$publishArgs = @('publish', $proj) + $common + @(
    '-o', $out,
    '-p:PublishSingleFile=true',
    '-p:DebugType=none'
)

if ($SelfContained) {
    # 自包含需要从 NuGet 还原运行时包
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true',
                      '-p:IncludeNativeLibrariesForSelfExtract=true',
                      "-p:RestoreConfigFile=$(Join-Path $tools 'NuGet.Config')")
} else {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'false')
}

& $dotnet @publishArgs
exit $LASTEXITCODE
