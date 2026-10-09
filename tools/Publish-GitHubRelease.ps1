#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the built VSIX as a GitHub release.

.DESCRIPTION
    Every build of MSSQL Tool is published as a release so users can install it and so the
    built-in update check (GET /releases/latest) finds it.  Only the VSIX is uploaded; the
    downloadable ZIP bundle stays local.

    The release is idempotent: an existing release for the same version is reused and its asset
    replaced, so re-running after a rebuild updates the same release instead of failing.

.PARAMETER Version
    Product version to publish (for example 4.59).  Defaults to the version in
    MSSQLTool\source.extension.vsixmanifest.

.PARAMETER Repo
    GitHub repository in owner/name form.  Defaults to PuLuShen/MSSQLTOOL.

.PARAMETER VsixPath
    VSIX to upload.  Defaults to artifacts\MSSQLTool_SSMS22_<version>.vsix.

.PARAMETER Token
    GitHub token with repo scope.  Defaults to $env:GITHUB_TOKEN and then to the credential
    Windows already stores for github.com, so no token has to be written down anywhere.

.PARAMETER Notes
    Release notes text.  Defaults to a short generated line.

.EXAMPLE
    pwsh -File tools\Publish-GitHubRelease.ps1
    pwsh -File tools\Publish-GitHubRelease.ps1 -Version 4.59
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repo = 'PuLuShen/MSSQLTOOL',
    [string]$VsixPath,
    [string]$Token,
    [string]$Notes,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-GitHubToken {
    param([string]$Explicit)

    if ($Explicit) { return $Explicit }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    if ($env:GH_TOKEN) { return $env:GH_TOKEN }

    # Reuse the credential git already uses to push, so nothing has to be stored twice.
    $payload = "protocol=https`nhost=github.com`n`n"
    $lines = $payload | & git credential fill 2>$null
    foreach ($line in $lines) {
        if ($line -like 'password=*') { return $line.Substring('password='.Length) }
    }

    throw "No GitHub token available. Pass -Token, set GITHUB_TOKEN, or sign in to github.com with git."
}

function Invoke-GitHubApi {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$AuthToken,
        [object]$Body,
        [string]$ContentType = 'application/json'
    )

    $headers = @{
        Authorization          = "Bearer $AuthToken"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'MSSQLTool-Release'
    }

    $parameters = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($null -ne $Body) {
        $parameters.Body = $Body
        $parameters.ContentType = $ContentType
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        $detail = ''
        $response = $_.Exception.Response
        if ($response) {
            try {
                $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
                $detail = $reader.ReadToEnd()
            }
            catch { }
        }
        throw ("GitHub API {0} {1} failed: {2} {3}" -f $Method, $Uri, $_.Exception.Message, $detail)
    }
}

function Send-ReleaseAsset {
    param(
        [string]$UploadUrlTemplate,
        [string]$AuthToken,
        [string]$FilePath
    )

    $name = [System.IO.Path]::GetFileName($FilePath)
    # The upload template has the form .../releases/{id}/assets{?name,label}
    $uri = ($UploadUrlTemplate -replace '\{\?[^}]*\}', '') + "?name=" + [System.Uri]::EscapeDataString($name)

    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromMinutes(20)
    try {
        $client.DefaultRequestHeaders.Authorization =
            New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $AuthToken)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('MSSQLTool-Release')
        $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

        # The asset endpoint takes the file as the raw request body.  Wrapping it in
        # MultipartFormDataContent made GitHub store the multipart envelope instead of the VSIX, so
        # every published package was rejected by VSIXInstaller as "not a valid VSIX package".
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        $content = New-Object System.Net.Http.ByteArrayContent(@(, $bytes))
        $content.Headers.ContentType =
            New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')

        $response = $client.PostAsync($uri, $content).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw ("Asset upload failed: {0} {1}" -f [int]$response.StatusCode, $text)
        }

        return ($text | ConvertFrom-Json)
    }
    finally {
        $client.Dispose()
    }
}

function Assert-ReleaseAssetMatches {
    param(
        [string]$DownloadUrl,
        [string]$AuthToken,
        [string]$FilePath
    )

    # Verifies what GitHub actually stored: an upload that mangles the bytes must fail here rather
    # than inside VSIXInstaller on a user's machine.
    $expectedSize = (Get-Item -LiteralPath $FilePath).Length
    $expectedHash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $temp = Join-Path $env:TEMP ("mssqltool-verify-" + [Guid]::NewGuid().ToString('N') + ".vsix")
    try {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $temp -UseBasicParsing `
            -Headers @{ Authorization = "Bearer $AuthToken"; 'User-Agent' = 'MSSQLTool-Release' }
        $actualSize = (Get-Item -LiteralPath $temp).Length
        $actualHash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($actualSize -ne $expectedSize -or $actualHash -ne $expectedHash) {
            throw ("The stored release asset does not match the file: expected {0} bytes / {1}, stored {2} bytes / {3}" -f
                $expectedSize, $expectedHash, $actualSize, $actualHash)
        }

        Write-Host "verified: the release asset matches ($actualSize bytes, sha256 $($actualHash.Substring(0, 12))...)"
    }
    finally
    {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- resolve inputs

if (-not $Version) {
    $manifest = Join-Path $repoRoot 'MSSQLTool\source.extension.vsixmanifest'
    if (-not (Test-Path $manifest)) { throw "Cannot find $manifest to read the version from." }
    # Only the Identity element carries the product version; the file also holds version ranges
    # for dependencies and prerequisites.
    $match = [regex]::Match((Get-Content -Raw $manifest), '<Identity\b[^>]*\bVersion="([0-9]+(?:\.[0-9]+)+)"')
    if (-not $match.Success) { throw "No version found on the Identity element of $manifest." }
    $Version = $match.Groups[1].Value
}

if (-not $VsixPath) {
    $VsixPath = Join-Path $repoRoot ("artifacts\MSSQLTool_SSMS22_{0}.vsix" -f $Version)
}
if (-not (Test-Path $VsixPath)) { throw "VSIX not found: $VsixPath (build the Release configuration first)." }
$VsixPath = (Resolve-Path $VsixPath).Path
$assetName = [System.IO.Path]::GetFileName($VsixPath)
$tag = "v$Version"

if (-not $Notes) {
    $Notes = "MSSQL Tool $Version for SSMS 22.`n`nInstall: download the VSIX below (or use the ZIP bundle's installer locally) and restart SSMS."
}

$sizeMb = [Math]::Round((Get-Item $VsixPath).Length / 1MB, 2)
Write-Host "repo    : $Repo"
Write-Host "tag     : $tag"
Write-Host "asset   : $assetName ($sizeMb MB)"
if ($DryRun) { Write-Host "dry run : nothing uploaded"; return }

$token = Get-GitHubToken -Explicit $Token
$api = "https://api.github.com/repos/$Repo"

# ---------------------------------------------------------------- create or reuse the release

$release = $null
try {
    $release = Invoke-GitHubApi -Method GET -Uri "$api/releases/tags/$tag" -AuthToken $token
    Write-Host "release : existing ($($release.html_url))"
}
catch {
    # 404 is the normal path for a version that was never published.
    if ($_.Exception.Message -notmatch '404') { throw }
}

if (-not $release) {
    $body = @{
        tag_name         = $tag
        name             = "MSSQL Tool $Version"
        body             = $Notes
        draft            = $false
        prerelease       = $false
        target_commitish = 'main'
    } | ConvertTo-Json
    $release = Invoke-GitHubApi -Method POST -Uri "$api/releases" -AuthToken $token -Body $body
    Write-Host "release : created ($($release.html_url))"
}

# ---------------------------------------------------------------- upload the VSIX only

foreach ($asset in @($release.assets)) {
    if ($asset -and $asset.name -eq $assetName) {
        Invoke-GitHubApi -Method DELETE -Uri "$api/releases/assets/$($asset.id)" -AuthToken $token | Out-Null
        Write-Host "asset   : replaced $assetName"
        break
    }
}

$uploaded = Send-ReleaseAsset -UploadUrlTemplate $release.upload_url -AuthToken $token -FilePath $VsixPath
Write-Host "uploaded: $($uploaded.name) ($([Math]::Round($uploaded.size / 1MB, 2)) MB)"
Assert-ReleaseAssetMatches -DownloadUrl $uploaded.browser_download_url -AuthToken $token -FilePath $VsixPath
Write-Host "latest  : https://github.com/$Repo/releases/latest"
