<#
.SYNOPSIS
  运行全部单元测试。
  说明：本机 dotnet test 会崩在 VSTest testhost 启动（无法打开父进程句柄，Win32Exception 5），
  所以这里用仓库内的反射运行器 PerfScope.TestRunner 执行同一批 [Fact]/[Theory] 用例。
.EXAMPLE
  .\scripts\run-tests.ps1
  .\scripts\run-tests.ps1 -Filter Sampling
#>
[CmdletBinding()]
param(
    [string]$Filter = '',
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build tests\PerfScope.TestRunner -c $Configuration --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "测试项目编译失败（exit $LASTEXITCODE）" }

    $exe = Join-Path $root "tests\PerfScope.TestRunner\bin\$Configuration\net10.0-windows\PerfScope.TestRunner.exe"
    if (-not (Test-Path $exe)) { throw "找不到测试运行器：$exe" }

    if ($Filter) { & $exe $Filter } else { & $exe }
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "测试失败（exit $code）" }
    Write-Host '==> 全部测试通过' -ForegroundColor Green
}
finally {
    Pop-Location
}
