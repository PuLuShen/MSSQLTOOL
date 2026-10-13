# Uploads a built VSIX to the Open SSMS VSIX Gallery (https://ssmsgallery.azurewebsites.net).
#
#   .\tools\Publish-VsixGallery.ps1                       # newest artifacts\*.vsix
#   .\tools\Publish-VsixGallery.ps1 -VsixPath <path>       # a specific package
#
# The gallery takes the VSIX as a multipart form field named "file"; the manage token only has to be
# supplied once (it becomes, or replaces, the password of the manage page).
param(
    [string]$VsixPath,
    [string]$Repo = 'https://github.com/PuLuShen/MSSQLTOOL',
    [string]$ManageToken = $env:VSIX_GALLERY_TOKEN,
    [string]$Gallery = 'https://ssmsgallery.azurewebsites.net/api/upload'
)

$ErrorActionPreference = 'Stop'

if (-not $VsixPath) {
    $VsixPath = Get-ChildItem (Join-Path $PSScriptRoot '..\artifacts') -Filter *.vsix |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $VsixPath -or -not (Test-Path -LiteralPath $VsixPath)) { throw "No VSIX to publish." }

$vsix = (Resolve-Path -LiteralPath $VsixPath).Path
Write-Host "publishing $([Math]::Round((Get-Item -LiteralPath $vsix).Length / 1MB, 2)) MB : $vsix"

$url = "$Gallery" + "?repo=$Repo&readmeUrl=main/README.zh-CN.md&issuetracker=$Repo/issues"
$arguments = @('-sS', '--fail-with-body', '-X', 'POST', $url, '-F', "file=@$vsix")
if ($ManageToken) { $arguments += @('-H', "X-Manage-Token: $ManageToken") }

$response = & curl.exe @arguments
if ($LASTEXITCODE -ne 0) { throw "Upload failed (curl exit $LASTEXITCODE): $response" }

$result = $response | ConvertFrom-Json
Write-Host ("published : {0} {1}" -f $result.name, $result.version)
Write-Host ("details   : https://ssmsgallery.azurewebsites.net{0}" -f $result.detailsLink)
Write-Host ("feed      : https://ssmsgallery.azurewebsites.net{0}" -f $result.feedLink)
Write-Host ("manage    : https://ssmsgallery.azurewebsites.net{0}" -f $result.managePageLink)
if ($result.hasValidatorErrors) { throw "The gallery reported validation errors." }