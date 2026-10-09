# Installs a downloaded MSSQL Tool update after SSMS has exited.
#
# The extension writes this script next to the downloaded package and starts it detached when the
# user picks "Update on Close".  Because the script outlives SSMS it can wait for the process to
# exit and download the package itself, so a slow download no longer cancels the update.
#
# Every step is appended to -LogPath; the extension shows the tail of that file on its Updates page.

param(
    [int]$TargetProcessId = 0,
    [string]$Version = '',
    [string]$VsixPath = '',
    [string]$AssetUrl = '',
    [string]$Sha256 = '',
    [string]$Installer = '',
    [string]$SsmsPath = '',
    [string]$InstanceId = '',
    [string]$ReleasePage = '',
    [string]$LogPath = '',
    [int]$WaitMinutes = 5
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Write-Log([string]$message) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    try { Add-Content -LiteralPath $LogPath -Value ("$stamp $message") -Encoding UTF8 } catch { }
}

function Get-FileSha256([string]$path) {
    try { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } catch { return $null }
}

function Test-Package([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    if ([string]::IsNullOrWhiteSpace($Sha256)) { return $true }
    $actual = Get-FileSha256 $path
    if ($actual -eq $Sha256.ToLowerInvariant()) { return $true }
    Write-Log "checksum mismatch for $path (expected $Sha256, actual $actual)"
    return $false
}

Write-Log "helper started for version '$Version'; waiting for process $TargetProcessId to exit"

$deadline = (Get-Date).AddMinutes($WaitMinutes)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
}

if (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) {
    Write-Log "SSMS is still running after $WaitMinutes minute(s); trying to install anyway"
} else {
    Write-Log 'SSMS has exited'
}

# The staged package is used when it is complete: the marker file is written only after the download
# finished and the checksum matched.
$readyMarker = $VsixPath + '.ready'
$package = $null
if ((Test-Path -LiteralPath $VsixPath) -and (Test-Path -LiteralPath $readyMarker)) {
    if (Test-Package $VsixPath) {
        $package = $VsixPath
        Write-Log 'using the package staged by SSMS'
    } else {
        Remove-Item -LiteralPath $readyMarker -Force -ErrorAction SilentlyContinue
    }
}

if (-not $package) {
    if ([string]::IsNullOrWhiteSpace($AssetUrl)) {
        Write-Log 'no download URL was provided; nothing to install'
    } else {
        $partial = $VsixPath + '.part'
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Write-Log "downloading $AssetUrl"
            Invoke-WebRequest -Uri $AssetUrl -OutFile $partial -UseBasicParsing
            if (Test-Package $partial) {
                Move-Item -LiteralPath $partial -Destination $VsixPath -Force
                Set-Content -LiteralPath $readyMarker -Value 'verified by the update helper' -Encoding UTF8
                $package = $VsixPath
                Write-Log 'download finished and verified'
            } else {
                Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
                Write-Log 'download did not pass verification'
            }
        } catch {
            Write-Log "download failed: $($_.Exception.Message)"
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $package) {
    Write-Log 'update skipped; opening the release page'
    if (-not [string]::IsNullOrWhiteSpace($ReleasePage)) {
        try { Start-Process -FilePath $ReleasePage | Out-Null } catch { Write-Log "opening the release page failed: $($_.Exception.Message)" }
    }
    return
}

if (-not (Test-Path -LiteralPath $Installer)) {
    Write-Log "VSIXInstaller not found at '$Installer'; opening the release page"
    if (-not [string]::IsNullOrWhiteSpace($ReleasePage)) {
        try { Start-Process -FilePath $ReleasePage | Out-Null } catch { Write-Log "opening the release page failed: $($_.Exception.Message)" }
    }
    return
}

# SSMS is an isolated shell, so VSIXInstaller needs the instance id of the installation that runs
# this extension; without it the package can land in the wrong instance (or nowhere).
$resolvedInstanceId = $InstanceId
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if ([string]::IsNullOrWhiteSpace($resolvedInstanceId) -and (Test-Path -LiteralPath $vswhere)) {
        $resolvedInstanceId = & $vswhere -products Microsoft.VisualStudio.Product.Ssms -latest -property instanceId 2>$null | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($resolvedInstanceId) -and -not [string]::IsNullOrWhiteSpace($SsmsPath)) {
            $resolvedInstanceId = & $vswhere -products * -path $SsmsPath -property instanceId 2>$null | Select-Object -First 1
        }
    }
} catch {
    Write-Log "resolving the SSMS instance failed: $($_.Exception.Message)"
}

$instanceArgument = $null
if (-not [string]::IsNullOrWhiteSpace($resolvedInstanceId)) {
    $instanceArgument = '/instanceIds:' + $resolvedInstanceId.Trim()
    Write-Log "using SSMS instance $instanceArgument"
}

$targetArguments = @($package)
if ($instanceArgument) { $targetArguments += $instanceArgument }

Write-Log "installing $package quietly"
try {
    $process = Start-Process -FilePath $Installer -ArgumentList (@('/quiet') + $targetArguments) -PassThru -Wait
    Write-Log "VSIXInstaller exit code $($process.ExitCode)"
    if ($process.ExitCode -eq 0) {
        Remove-Item -LiteralPath $package -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $readyMarker -Force -ErrorAction SilentlyContinue
        Write-Log "update installed; restart SSMS to run version $Version"
        return
    }
} catch {
    Write-Log "quiet VSIXInstaller run failed: $($_.Exception.Message)"
}

# A quiet install fails when the extension needs elevation, which is also why the install helper in
# the released package runs the visible installer; the window can ask for elevation.
Write-Log 'retrying with the visible installer window'
try {
    $visible = Start-Process -FilePath $Installer -ArgumentList $targetArguments -PassThru -Wait
    Write-Log "visible VSIXInstaller exit code $($visible.ExitCode)"
    if ($visible.ExitCode -eq 0) {
        Remove-Item -LiteralPath $package -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $readyMarker -Force -ErrorAction SilentlyContinue
        Write-Log "update installed; restart SSMS to run version $Version"
        return
    }
} catch {
    Write-Log "visible installer failed: $($_.Exception.Message)"
}

Write-Log 'the update could not be installed; opening the release page'
if (-not [string]::IsNullOrWhiteSpace($ReleasePage)) {
    try { Start-Process -FilePath $ReleasePage | Out-Null } catch { }
}
