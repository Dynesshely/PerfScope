<#
.SYNOPSIS
  PerfScope 发布打包：self-contained 单文件 exe → portable zip → NSIS 安装包 → 校验和。
.DESCRIPTION
  产物（默认 dist\release）：
    PerfScope-<版本>-win-x64-portable.zip      免安装版（解压即用，内置 .NET 运行时）
    PerfScope-Setup-<版本>-win-x64.exe         NSIS 安装包（按用户安装，无需管理员）
    SHA256SUMS.txt                             两个产物的 SHA-256
.PARAMETER Version
  版本号，例如 1.0.0（不带 v 前缀）。会写入程序集与安装包。
.PARAMETER NoInstaller
  跳过 NSIS 步骤（本机没装 makensis 时用）。
.EXAMPLE
  .\scripts\package.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutDir = '',
    [switch]$NoInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'dist\release' }

$cleanVersion = $Version.TrimStart('v', 'V')
$numericVersion = ($cleanVersion -split '-')[0]
if ($cleanVersion -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "版本号格式不正确：$Version（应为 0.1.0 / 1.0.0 这样的语义化版本）"
}

$rootFull = [System.IO.Path]::GetFullPath($root)
$resolvedOut = [System.IO.Path]::GetFullPath($OutDir)
if (-not $resolvedOut.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "输出目录必须位于仓库内：$resolvedOut"
}

$publishDir = Join-Path $root 'dist\publish'
$zipPath = Join-Path $resolvedOut "PerfScope-$cleanVersion-win-x64-portable.zip"
$installerPath = Join-Path $resolvedOut "PerfScope-Setup-$cleanVersion-win-x64.exe"

Push-Location $root
try {
    if (-not $SkipTests) {
        Write-Host '==> 运行测试' -ForegroundColor Cyan
        & (Join-Path $root 'scripts\run-tests.ps1')
        if ($LASTEXITCODE -ne 0) { throw "测试失败（exit $LASTEXITCODE）" }
    }

    Write-Host "==> 发布 self-contained 单文件（版本 $cleanVersion）" -ForegroundColor Cyan
    foreach ($dir in @($publishDir, $resolvedOut)) {
        $full = [System.IO.Path]::GetFullPath($dir)
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $full | Out-Null
    }

    dotnet publish 'src\PerfScope.App\PerfScope.App.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:Version=$cleanVersion -p:AssemblyVersion=$cleanVersion -p:FileVersion=$cleanVersion -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

    $exe = Join-Path $publishDir 'PerfScope.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "没有生成 $exe" }
    Write-Host ("==> 程序：{0:N1} MB" -f ((Get-Item -LiteralPath $exe).Length / 1MB)) -ForegroundColor Green

    Write-Host '==> 制作 portable zip' -ForegroundColor Cyan
    $portableDir = Join-Path $root 'dist\portable'
    if (Test-Path -LiteralPath $portableDir) { Remove-Item -LiteralPath $portableDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
    Copy-Item -LiteralPath $exe -Destination $portableDir
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $portableDir
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $portableDir
    Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("==> {0}（{1:N1} MB）" -f (Split-Path -Leaf $zipPath), ((Get-Item -LiteralPath $zipPath).Length / 1MB)) -ForegroundColor Green

    if (-not $NoInstaller) {
        $makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
        if (-not $makensis) {
            $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
            $candidates = @(
                (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
                (Join-Path $programFilesX86 'NSIS\makensis.exe')
            )
            $makensis = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }

        if (-not $makensis) {
            throw '未找到 makensis（NSIS）。可先安装 NSIS 3，或用 -NoInstaller 跳过安装包步骤。'
        }

        Write-Host "==> 构建安装包（$makensis）" -ForegroundColor Cyan
        $icon = Join-Path $root 'src\PerfScope.App\Assets\app.ico'
        & $makensis "/DAPP_VERSION=$cleanVersion" "/DAPP_VERSION_NUMERIC=$numericVersion" "/DSOURCE_DIR=$publishDir" "/DOUTPUT_FILE=$installerPath" "/DICON_FILE=$icon" (Join-Path $root 'installer\PerfScope.nsi') | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath)) { throw "安装包构建失败（exit $LASTEXITCODE）" }
        Write-Host ("==> {0}（{1:N1} MB）" -f (Split-Path -Leaf $installerPath), ((Get-Item -LiteralPath $installerPath).Length / 1MB)) -ForegroundColor Green
    }

    Write-Host '==> 生成校验和' -ForegroundColor Cyan
    $sums = Get-ChildItem -LiteralPath $resolvedOut -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    Set-Content -LiteralPath (Join-Path $resolvedOut 'SHA256SUMS.txt') -Value $sums -Encoding utf8

    Write-Host '==> 产物清单' -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $resolvedOut -File | ForEach-Object { '    {0,10:N0} KB  {1}' -f ($_.Length / 1KB), $_.Name }
    Write-Host '==> 完成' -ForegroundColor Green
}
finally {
    Pop-Location
}