<#
.SYNOPSIS
  PerfScope 构建脚本：还原 -> 编译 -> 测试 ->（可选）发布单文件。
.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -Test -Publish
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Test,
    [switch]$Publish,
    [string]$Configuration = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Configuration) { $Configuration = if ($Release -or $Publish) { 'Release' } else { 'Debug' } }

Push-Location $root
try {
    Write-Host "==> dotnet build ($Configuration)" -ForegroundColor Cyan
    dotnet build -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

    if ($Test) {
        Write-Host "==> dotnet test" -ForegroundColor Cyan
        dotnet test -c $Configuration --nologo --no-build
        if ($LASTEXITCODE -ne 0) { throw "测试失败（exit $LASTEXITCODE）" }
    }

    if ($Publish) {
        $dist = Join-Path $root 'dist'
        Write-Host "==> 发布到 $dist" -ForegroundColor Cyan
        if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
        $publishArgs = @(
            'publish', 'src\PerfScope.App\PerfScope.App.csproj',
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=none', '-o', $dist, '--nologo'
        )
        dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
        $exe = Join-Path $dist 'PerfScope.exe'
        if (Test-Path $exe) {
            Write-Host ("==> 产物：$exe（{0:N1} MB）" -f ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
        }
    }

    Write-Host "==> 完成" -ForegroundColor Green
}
finally {
    Pop-Location
}
