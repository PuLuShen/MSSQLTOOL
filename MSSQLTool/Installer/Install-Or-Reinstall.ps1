[CmdletBinding()]
param(
    [switch]$Admin
)

$ErrorActionPreference = 'Stop'

if (Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue) {
    throw '检测到 SSMS 正在运行。请关闭所有 SSMS 窗口后重试。'
}

$vsixPath = Join-Path $PSScriptRoot 'MSSQLTool.vsix'
if (-not (Test-Path -LiteralPath $vsixPath)) {
    $vsixPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'MSSQLTool.vsix'
}
if (-not (Test-Path -LiteralPath $vsixPath)) {
    throw '找不到与安装脚本配套的 MSSQLTool.vsix。'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "找不到 Visual Studio Installer：$vswhere"
}

$instanceJson = & $vswhere -products Microsoft.VisualStudio.Product.Ssms -latest -format json
if ($LASTEXITCODE -ne 0) {
    throw "vswhere 查询 SSMS 失败，退出码：$LASTEXITCODE"
}

$instance = @($instanceJson | ConvertFrom-Json) | Select-Object -First 1
if (-not $instance) {
    throw '未找到 SQL Server Management Studio 22 安装实例。'
}

$vsixInstaller = Join-Path $instance.installationPath 'Common7\IDE\VSIXInstaller.exe'
if (-not (Test-Path -LiteralPath $vsixInstaller)) {
    throw "找不到 VSIXInstaller：$vsixInstaller"
}

$instanceArgument = "/instanceIds:$($instance.instanceId)"
$adminArgument = if ($Admin) { @('/admin') } else { @() }

Write-Host '正在卸载已安装的 MSSQL Tool（未安装时会直接继续）...'
& $vsixInstaller @adminArgument /quiet /uninstall:MSSQLTool $instanceArgument
$uninstallExitCode = $LASTEXITCODE
if ($uninstallExitCode -ne 0) {
    Write-Warning "卸载程序返回退出码 $uninstallExitCode。若此前未安装该扩展，这是正常现象；将继续安装新版本。"
}

Write-Host '正在安装新的 MSSQL Tool...'
& $vsixInstaller @adminArgument $vsixPath $instanceArgument
if ($LASTEXITCODE -ne 0) {
    throw "安装失败，VSIXInstaller 退出码：$LASTEXITCODE"
}

Write-Host 'MSSQL Tool 安装或重装完成。'
