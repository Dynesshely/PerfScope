<#
.SYNOPSIS
  启动 PerfScope（默认启动 Release 产物；-Demo 使用合成数据预览界面）。
.EXAMPLE
  .\scripts\run.ps1
  .\scripts\run.ps1 -Demo
#>
[CmdletBinding()]
param(
    [switch]$Demo,
    [switch]$DebugBuild,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cfg = if ($DebugBuild) { 'Debug' } else { 'Release' }
$exe = Join-Path $root "src\PerfScope.App\bin\$cfg\net10.0-windows\PerfScope.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root 'dist\PerfScope.exe'
}
if (-not (Test-Path $exe)) {
    throw "找不到 PerfScope.exe，请先执行 .\scripts\build.ps1 -Release"
}

$arguments = @()
if ($Demo) { $arguments += '--demo' }
$arguments += $ExtraArgs
Write-Host "启动：$exe $($arguments -join ' ')" -ForegroundColor Cyan
Start-Process -FilePath $exe -ArgumentList $arguments | Out-Null
