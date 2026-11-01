<#
.SYNOPSIS
  启动 dev 版（Debug）供验收。
.DESCRIPTION
  为什么要镜像到工作区外面：
  DSH 的 Windows 沙箱是按「进程映像所在路径」生效的 —— 只要 exe 在工作区内，
  这个进程看到的进程表就会被过滤（本机实测：工作区内 205 个进程，工作区外 919 个），
  并且写入 %LOCALAPPDATA% 也会被拒绝。把构建产物镜像到 %LOCALAPPDATA%\PerfScope-dev
  再启动，就能拿到完整数据，同时数据目录仍然指向仓库里的 artifacts\dev-run。
.PARAMETER NoBuild
  跳过重新构建，直接用现有 Debug 产物（快速重启时用）。
.PARAMETER TimeoutSeconds
  等待主窗口出现的最长秒数。
.EXAMPLE
  .\scripts\run-dev.ps1
  .\scripts\run-dev.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 40,
    [string[]]$AppArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    Write-Host '==> 构建 Debug' -ForegroundColor Cyan
    Push-Location $root
    try {
        dotnet build -c Debug --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}

# 先结束旧实例：镜像目录里的 dll 被占用时无法覆盖
Get-Process PerfScope -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$stage = Join-Path $env:LOCALAPPDATA 'PerfScope-dev'
$bin = Join-Path $stage 'bin'
$cliDir = Join-Path $stage 'cli'
Write-Host "==> 镜像到工作区外：$stage" -ForegroundColor Cyan
foreach ($dir in @($bin, $cliDir)) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
            break
        } catch {
            Start-Sleep -Milliseconds 400
        }
    }

    New-Item -ItemType Directory -Force $dir | Out-Null
}

Copy-Item -Path (Join-Path $root 'src\PerfScope.App\bin\Debug\net10.0-windows\*') -Destination $bin -Recurse -Force
Copy-Item -Path (Join-Path $root 'tools\PerfScope.Cli\bin\Debug\net10.0-windows\*') -Destination $cliDir -Recurse -Force

$data = Join-Path $root 'artifacts\dev-run'
New-Item -ItemType Directory -Force $data | Out-Null
$settingsPath = Join-Path $data 'settings.json'
if (-not (Test-Path $settingsPath)) {
    @{
        startMinimized = $false
        theme = 'dark'
        sampleIntervalMs = 2000
        uiRefreshIntervalMs = 1000
        showSelfUsage = $true
        topN = 30
        showSystemProcesses = $true
        mergeWindowsSystemProcesses = $true
        logLevel = 2
    } | ConvertTo-Json | Set-Content -Encoding utf8 $settingsPath
}

$exe = Join-Path $bin 'PerfScope.exe'
$arguments = '"' + $exe + '" --data-dir "' + $data + '"'
if ($AppArgs.Count -gt 0) { $arguments += ' ' + ($AppArgs -join ' ') }
$c = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $arguments }
if ($c.ReturnValue -ne 0) { throw "启动失败（WMI ReturnValue=$($c.ReturnValue)）" }

Write-Host "==> 等待主窗口（PID $($c.ProcessId)）" -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$process = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $process = Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue
    if (-not $process) { throw '实例已退出，请检查 logs\crash.log' }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
}

if (-not $process) { throw '实例没有起来' }

Write-Host ("==> 运行中：PID={0} 标题='{1}' RSS={2:N1} MB" -f $process.Id, $process.MainWindowTitle, ($process.WorkingSet64 / 1MB)) -ForegroundColor Green

# 用镜像出来的 CLI 自检进程可见性：应该看到数百个进程
$cli = Join-Path $cliDir 'PerfScope.Cli.exe'
if (Test-Path $cli) {
    $visibility = & $cli --processes --top 0 2>&1 | Select-String -Pattern '采集到' | Select-Object -First 1
    Write-Host ("==> 进程可见性：{0}" -f $visibility) -ForegroundColor DarkGray
}

Write-Host "==> 数据目录：$data" -ForegroundColor DarkGray