#Requires -Version 5.1
<#
.SYNOPSIS
    Uploads a built VSIX to a GitHub release, manually and on demand.

.DESCRIPTION
    This script is NOT part of the build.  Nothing is uploaded unless the user runs it, which
    keeps ordinary development builds out of the public releases feed.

    The release body comes from RELEASE_NOTES.md: the section whose heading is the version
    ("## 5.371") is used, and "## Unreleased" is used as a fallback while the version heading
    has not been added yet.  Editing that file is therefore enough to change what the release
    page says, and re-running the script patches an existing release instead of ignoring it.

    Only the VSIX is attached.  The upload is idempotent: re-running for the same version
    replaces the asset and refreshes the notes.

.PARAMETER Version
    Product version to publish (for example 5.371).  Defaults to the version in
    MSSQLTool\source.extension.vsixmanifest.

.PARAMETER Repo
    GitHub repository in owner/name form.  Defaults to PuLuShen/MSSQLTOOL.

.PARAMETER VsixPath
    VSIX to upload.  Defaults to artifacts\MSSQLTool_SSMS22_<version>.vsix.

.PARAMETER Token
    GitHub token with repo scope.  Defaults to $env:GITHUB_TOKEN and then to the credential
    Windows already stores for github.com, so no token has to be written down anywhere.

.PARAMETER Notes
    Release notes text.  Overrides the notes file for a one-off upload.

.PARAMETER NotesFile
    Markdown file that holds the release notes.  Defaults to RELEASE_NOTES.md in the repository
    root.

.PARAMETER DryRun
    Resolve the version, asset and notes, print them, and stop without contacting GitHub.

.EXAMPLE
    .\tools\Publish-Release.cmd
    .\tools\Publish-Release.cmd -DryRun
    .\tools\Publish-Release.cmd -Version 5.371 -VsixPath .\artifacts\MSSQLTool_SSMS22_5.371.vsix
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repo = 'PuLuShen/MSSQLTOOL',
    [string]$VsixPath,
    [string]$Token,
    [string]$Notes,
    [string]$NotesFile,
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
        if ($Body -is [string]) {
            # Windows PowerShell 5.1 encodes a string body as ISO-8859-1 when the content type is
            # application/json, which turned every Chinese release note into "?".  Send explicit
            # UTF-8 bytes so GitHub stores the text exactly as it is in RELEASE_NOTES.md.
            $parameters.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
            $parameters.ContentType = 'application/json; charset=utf-8'
        }
        else {
            $parameters.Body = $Body
            $parameters.ContentType = $ContentType
        }
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
        $Asset,
        [string]$DownloadUrl,
        [string]$AuthToken,
        [string]$FilePath
    )

    # Verifies what GitHub actually stored: an upload that mangles the bytes must fail here rather
    # than inside VSIXInstaller on a user's machine.  The API reports the size and digest GitHub
    # computed for the stored bytes, which is cheaper and far more reliable than downloading the
    # package again; the download is only used when no digest is available.
    $expectedSize = (Get-Item -LiteralPath $FilePath).Length
    $expectedHash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($Asset -and $Asset.digest -and $Asset.size) {
        $digestHash = ($Asset.digest -replace '^sha256:', '').ToLowerInvariant()
        if ([int64]$Asset.size -ne $expectedSize -or $digestHash -ne $expectedHash) {
            throw ("The stored release asset does not match the file: expected {0} bytes / {1}, stored {2} bytes / {3}" -f
                $expectedSize, $expectedHash, $Asset.size, $digestHash)
        }

        Write-Host "verified: GitHub stores $($Asset.size) bytes, sha256 $($digestHash.Substring(0, 12))... (from the API digest)"
        return
    }

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

function Get-NotesSection {
    param(
        [string]$Path,
        [string]$Version
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Release notes file not found: $Path"
    }

    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    foreach ($heading in @($Version, 'Unreleased')) {
        $pattern = '(?ims)^##[ \t]+' + [regex]::Escape($heading) + '[ \t]*\r?\n(.*?)(?=^##[ \t]+|\z)'
        $match = [regex]::Match($text, $pattern)
        if ($match.Success) {
            $section = $match.Groups[1].Value.Trim()
            if ($section.Length -gt 0) { return $section }
        }
    }

    throw ("No release notes for {0} in {1}. Add a '## {0}' section (or '## Unreleased') with the text to publish." -f
        $Version, $Path)
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
$releaseName = "MSSQL Tool $Version"

if (-not $Notes) {
    if (-not $NotesFile) { $NotesFile = Join-Path $repoRoot 'RELEASE_NOTES.md' }
    $Notes = Get-NotesSection -Path $NotesFile -Version $Version
}
if ([string]::IsNullOrWhiteSpace($Notes)) { throw "The release notes are empty." }

$sizeMb = [Math]::Round((Get-Item $VsixPath).Length / 1MB, 2)
Write-Host "repo    : $Repo"
Write-Host "tag     : $tag"
Write-Host "asset   : $assetName ($sizeMb MB)"
if ($NotesFile) { Write-Host "notes   : $NotesFile" }
if ($DryRun) {
    Write-Host "dry run : nothing uploaded"
    Write-Host ""
    Write-Host "----- release notes -----"
    Write-Host $Notes
    return
}

$token = Get-GitHubToken -Explicit $Token
$api = "https://api.github.com/repos/$Repo"

# ---------------------------------------------------------------- create or update the release

$release = $null
try {
    $release = Invoke-GitHubApi -Method GET -Uri "$api/releases/tags/$tag" -AuthToken $token
    Write-Host "release : existing ($($release.html_url))"
}
catch {
    # 404 is the normal path for a version that was never published.
    if ($_.Exception.Message -notmatch '404') { throw }
}

if ($release) {
    # Re-running must also refresh the text: the notes are edited between builds, and the old
    # script only wrote them when the release was created.
    if ($release.name -ne $releaseName -or $release.body -ne $Notes) {
        $patch = @{
            name  = $releaseName
            body  = $Notes
            draft = $false
        } | ConvertTo-Json
        $release = Invoke-GitHubApi -Method PATCH -Uri "$api/releases/$($release.id)" -AuthToken $token -Body $patch
        Write-Host "notes   : updated"
    }
}
else {
    $body = @{
        tag_name         = $tag
        name             = $releaseName
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
Assert-ReleaseAssetMatches -Asset $uploaded -DownloadUrl $uploaded.browser_download_url -AuthToken $token -FilePath $VsixPath
Write-Host "latest  : https://github.com/$Repo/releases/latest"
