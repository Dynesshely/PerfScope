<#
.SYNOPSIS
  PerfScope 自身开销实测：用 WMI 分离启动（避免 GUI 子进程占住父进程标准输出），
  按固定间隔采集它自己的 CPU、工作集、私有内存、句柄数，并统计数据目录的增长。
.EXAMPLE
  .\scripts\measure.ps1 -Seconds 60
  .\scripts\measure.ps1 -Seconds 300 -IntervalMs 10000 -DataDir artifacts\soak
#>
[CmdletBinding()]
param(
    [int]$Seconds = 60,
    [int]$IntervalMs = 1000,
    [string]$ExePath = '',
    [string]$DataDir = '',
    [string[]]$AppArgs = @(),
    [int]$WarmupSeconds = 10,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $candidates = @(
        (Join-Path $root 'src\PerfScope.App\bin\Release\net10.0-windows\PerfScope.exe'),
        (Join-Path $root 'dist\PerfScope.exe'),
        (Join-Path $root 'src\PerfScope.App\bin\Debug\net10.0-windows\PerfScope.exe')
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw '找不到 PerfScope.exe，请先执行 .\scripts\build.ps1 -Release 或 -Publish'
}

if ($DataDir) {
    $DataDir = [System.IO.Path]::GetFullPath((Join-Path $root $DataDir))
    Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $DataDir | Out-Null
}

function Get-DirSize([string]$path) {
    if (-not (Test-Path $path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [int64]$sum
}

Get-Process PerfScope -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$before = if ($DataDir) { 0 } else { Get-DirSize (Join-Path $env:LOCALAPPDATA 'PerfScope') }
$cores = [Environment]::ProcessorCount
$sampleCount = [Math]::Max(1, [int]($Seconds * 1000 / $IntervalMs))
$args2 = @('--selftest', [string]($Seconds + $WarmupSeconds + 30))
if ($DataDir) { $args2 += @('--data-dir', $DataDir) }
if ($AppArgs.Count -gt 0) { $args2 += $AppArgs }
$cmdLine = '"' + $ExePath + '" ' + ($args2 -join ' ')

Write-Host "==> 被测程序：$ExePath"
Write-Host ("==> 参数：{0}" -f ($args2 -join ' '))
Write-Host ("==> 逻辑核心 {0}，采样间隔 {1} ms，总时长 {2} s（预热 {3} s）" -f $cores, $IntervalMs, $Seconds, $WarmupSeconds)

$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine }
if ($created.ReturnValue -ne 0) { throw "启动失败（WMI ReturnValue=$($created.ReturnValue)）" }
$processId = $created.ProcessId
Start-Sleep -Seconds $WarmupSeconds
if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
    throw "被测进程已退出（PID $processId），请检查日志"
}

$p = Get-Process -Id $processId
$prevCpu = $p.TotalProcessorTime.TotalMilliseconds
$prevTime = (Get-Date).ToUniversalTime()
$handleStart = $p.HandleCount
$rows = @()

for ($i = 0; $i -lt $sampleCount; $i++) {
    Start-Sleep -Milliseconds $IntervalMs
    $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $p) { Write-Warning '被测进程已退出'; break }
    $now = (Get-Date).ToUniversalTime()
    $cpuNow = $p.TotalProcessorTime.TotalMilliseconds
    $wall = ($now - $prevTime).TotalMilliseconds
    $cpuPct = if ($wall -gt 0) { (($cpuNow - $prevCpu) / $wall) / $cores * 100 } else { 0 }
    $prevCpu = $cpuNow
    $prevTime = $now
    $rows += [pscustomobject]@{
        时刻秒 = ($i + 1) * ($IntervalMs / 1000)
        CpuPct = [math]::Round($cpuPct, 3)
        WsMb = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Handles = $p.HandleCount
        Threads = $p.Threads.Count
    }
}

$exitCode = $null
if (-not $KeepRunning) {
    $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($p) { Stop-Process -Id $processId -Force; Start-Sleep -Milliseconds 900 }
}
$after = if ($DataDir) { Get-DirSize $DataDir } else { Get-DirSize (Join-Path $env:LOCALAPPDATA 'PerfScope') }

if ($rows.Count -eq 0) { throw '没有采集到任何有效样本' }

$cpu = $rows | Measure-Object -Property CpuPct -Average -Minimum -Maximum
$ws = $rows | Measure-Object -Property WsMb -Average -Minimum -Maximum
$pr = $rows | Measure-Object -Property PrivateMb -Average -Maximum
$hd = $rows | Measure-Object -Property Handles -Minimum -Maximum

Write-Host ''
Write-Host ('样本(每 {0} s 一条，前 8 条)：' -f ($IntervalMs / 1000))
$rows | Select-Object -First 8 | Format-Table -AutoSize | Out-String | Write-Host

Write-Host '=== 自身开销汇总 ===' -ForegroundColor Green
[pscustomobject]@{
    '样本数'           = $rows.Count
    'CPU 平均(%)'      = [math]::Round($cpu.Average, 3)
    'CPU 峰值(%)'      = [math]::Round($cpu.Maximum, 3)
    '工作集 平均(MB)'  = [math]::Round($ws.Average, 1)
    '工作集 峰值(MB)'  = [math]::Round($ws.Maximum, 1)
    '私有内存 峰值(MB)' = [math]::Round($pr.Maximum, 1)
    '句柄 起始/最小/峰' = "$handleStart / $($hd.Minimum) / $($hd.Maximum)"
    '数据目录(KB)'     = [math]::Round(($after - $before) / 1KB, 1)
} | Format-List | Out-String | Write-Host

if ($hd.Maximum -gt $handleStart + 100) {
    Write-Warning "句柄增长较多（$handleStart -> $($hd.Maximum)），需要复查是否泄漏。"
}
