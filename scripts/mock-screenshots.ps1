<#
.SYNOPSIS
  用 mock 数据重新生成文档截图（docs/images）。
.DESCRIPTION
  应用支持 --mock：完全不读取本机进程，只合成一套虚构应用数据（Aurora Browser / Nova Editor …），
  并给虚构的安装路径，图标走首字母色块兜底。截图因此不含任何真实用户名、软件清单或机器信息。
  每次生成的数据是确定性的（固定随机种子），同一套参数截图结果基本一致。
.EXAMPLE
  .\scripts\mock-screenshots.ps1
#>
[CmdletBinding()]
param(
    [int]$Width = 1560,
    [int]$Height = 980
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root 'artifacts\mock-run'
$exe = Join-Path $root 'src\PerfScope.App\bin\Debug\net10.0-windows\PerfScope.exe'
$images = Join-Path $root 'docs\images'
New-Item -ItemType Directory -Force $images | Out-Null
New-Item -ItemType Directory -Force $data | Out-Null

Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int max);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
'@

function Stop-App {
    Get-Process PerfScope -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

function Start-Mock {
    param([int]$Tab, [switch]$Agent, [string]$OutFile)

    Stop-App
    (@{ SplitRatio = 0.55; TreemapVisible = $true; DistributionTab = $Tab } | ConvertTo-Json -Compress) |
        Set-Content -Encoding utf8 (Join-Path $data 'ui.json')

    $arguments = '"' + $exe + '" --mock --data-dir "' + $data + '"'
    if ($Agent) { $arguments += ' --open-agent' }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $arguments }
    Start-Sleep -Seconds 9
    $process = Get-Process -Id $created.ProcessId -ErrorAction Stop
    [W.U]::ShowWindow($process.MainWindowHandle, 9) | Out-Null
    [W.U]::SetWindowPos($process.MainWindowHandle, [IntPtr](-1), 0, 0, $Width, $Height, 0x0004) | Out-Null
    Start-Sleep -Seconds 4

    if (-not $Agent) {
        dotnet run (Join-Path $root 'scripts\capture-window.cs') -- $process.Id $OutFile | Out-Null
        return
    }

    Start-Sleep -Seconds 2
    $dialog = Find-Window $process.Id '接入 Agent'
    if ($dialog -eq [IntPtr]::Zero) { throw '没有找到「接入 Agent」窗口' }
    [W.U]::SetWindowPos($dialog, [IntPtr](-1), 120, 80, 0, 0, 0x0001 -bor 0x0002) | Out-Null
    Start-Sleep -Seconds 2
    dotnet run (Join-Path $root 'scripts\capture-window.cs') -- ('hwnd:' + $dialog.ToInt64()) $OutFile | Out-Null
}

function Find-Window {
    param([int]$ProcessId, [string]$Needle)
    $script:foundWindow = [IntPtr]::Zero
    $callback = [W.U+EnumProc]{
        param($hwnd, $lParam)
        $owner = 0
        [W.U]::GetWindowThreadProcessId($hwnd, [ref]$owner) | Out-Null
        if ($owner -eq $ProcessId) {
            $sb = New-Object System.Text.StringBuilder 256
            [W.U]::GetWindowTextW($hwnd, $sb, 256) | Out-Null
            if ($sb.ToString().Contains($Needle)) { $script:foundWindow = $hwnd; return $false }
        }
        return $true
    }
    [W.U]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $script:foundWindow
}

Write-Host '==> 构建 Debug' -ForegroundColor Cyan
Push-Location $root
try { dotnet build src/PerfScope.App -c Debug --nologo | Out-Host } finally { Pop-Location }

Write-Host '==> 截图：内存分布' -ForegroundColor Cyan
Start-Mock -Tab 0 -OutFile (Join-Path $images 'screenshot-main.png')

Write-Host '==> 截图：CPU 分布' -ForegroundColor Cyan
Start-Mock -Tab 1 -OutFile (Join-Path $images 'screenshot-cpu.png')

Write-Host '==> 截图：接入 Agent' -ForegroundColor Cyan
Start-Mock -Tab 0 -Agent -OutFile (Join-Path $images 'screenshot-agent.png')

Stop-App
Write-Host '完成：docs\images\screenshot-main.png / screenshot-cpu.png / screenshot-agent.png' -ForegroundColor Green
