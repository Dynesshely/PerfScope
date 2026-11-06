<#
.SYNOPSIS
  性能回归门：跑几帧 CLI 采样，检查监控器自身开销是否仍在预算内。
.DESCRIPTION
  这是产品的核心承诺（"自身占用极低"），必须自动化守住：
    - 自身 CPU 均值 > 2%            失败
    - 自身内存峰值 > 200 MB         失败
  说明：CI 无桌面环境，所以用 CLI（复用同一套 Core 采样与归并）做回归门，
  而不是启动 GUI。CLI 读不到本机全部进程时开销会更低，属于更宽松的下界，仍能拦住明显退化。
.EXAMPLE
  .\scripts\check-perf.ps1
#>
[CmdletBinding()]
param(
    [int]$Frames = 8,
    [int]$IntervalMs = 1000,
    [double]$MaxCpuPercent = 2.0,
    [int]$MaxMemoryMb = 200
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root 'tools\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe'

if (-not (Test-Path -LiteralPath $cli)) {
    Write-Host '==> 构建 CLI（Release）' -ForegroundColor Cyan
    Push-Location $root
    try { dotnet build tools\PerfScope.Cli -c Release --nologo | Out-Host } finally { Pop-Location }
}

if (-not (Test-Path -LiteralPath $cli)) { throw "找不到 CLI：$cli" }

Write-Host "==> 采样 $Frames 帧（间隔 $IntervalMs ms）" -ForegroundColor Cyan
$output = & $cli --frames $Frames --interval $IntervalMs --top 3 2>&1
$output | Select-Object -Last 12 | Out-Host

$cpuValues = @()
$memoryValues = @()
foreach ($line in $output) {
    if ($line -match '自身 CPU\s+([0-9.]+)%.*?内存\s+([0-9.]+)\s*MB') {
        $cpuValues += [double]$matches[1]
        $memoryValues += [double]$matches[2]
    }
}

if ($cpuValues.Count -eq 0) {
    throw '没有解析到「自身 CPU / 内存」数据，无法判定性能门'
}

$avgCpu = ($cpuValues | Measure-Object -Average).Average
$peakMemory = ($memoryValues | Measure-Object -Maximum).Maximum
Write-Host ("==> 自身 CPU 均值 {0:N2}%（阈值 {1}%），内存峰值 {2:N0} MB（阈值 {3} MB）" -f $avgCpu, $MaxCpuPercent, $peakMemory, $MaxMemoryMb)

if ($avgCpu -gt $MaxCpuPercent) {
    throw ("自身 CPU 均值 {0:N2}% 超过阈值 {1}%" -f $avgCpu, $MaxCpuPercent)
}

if ($peakMemory -gt $MaxMemoryMb) {
    throw ("自身内存峰值 {0:N0} MB 超过阈值 {1} MB" -f $peakMemory, $MaxMemoryMb)
}

Write-Host '==> 性能门通过' -ForegroundColor Green