<#
.SYNOPSIS
    Builds the mod and publishes it to the Steam Workshop with SteamCMD.

.DESCRIPTION
    Without -Publish this is a dry run: it builds the mod into Steam\content, fills in Steam\base.vdf and prints what
    it would upload, but sends nothing. Pass -Publish to run SteamCMD.

    The first publish creates the Workshop item and SteamCMD writes its id back; this script stores that id in
    Steam\base.vdf, so later runs update the same item. Commit that file after the first publish.

    SteamCMD asks for your password and Steam Guard code itself. Nothing secret is read or stored here.

.EXAMPLE
    .\Steam\publish.ps1 -SteamCmd C:\Path\To\Your\steamcmd.exe

.EXAMPLE
    .\Steam\publish.ps1 -SteamCmd C:\Path\To\Your\steamcmd.exe -Publish -ChangeNote "First release"
#>
[CmdletBinding()]
param(
    [string] $SteamUser = 'moomooky',

    # Falls back to the STEAMCMD environment variable, then to whatever "steamcmd" resolves to on PATH.
    [string] $SteamCmd = $(if ($env:STEAMCMD) { $env:STEAMCMD } else { 'steamcmd' }),

    [string] $PreviewImage = (Join-Path $PSScriptRoot 'preview.png'),

    # Optional. Each non-empty note becomes an entry in the item's Change Notes tab; leave it out for a quiet update.
    [string] $ChangeNote = '',

    # Only sent when given, so an update never changes visibility you set on the Steam page. A new item is created
    # Private so it can be checked before anyone else can see it.
    [ValidateSet('Public', 'FriendsOnly', 'Private', 'Unlisted')]
    [string] $Visibility,

    [switch] $Publish
)

$ErrorActionPreference = 'Stop'

# A terminal opened from an IDE that was already running when the game set the modding variables inherits the IDE's
# older environment, not the current one, so take any that are missing from the user environment.
foreach ($name in 'SPZ2_PATH', 'SPZ2_PERSISTENT', 'SPZ2_SHIFTER') {
    if (-not [Environment]::GetEnvironmentVariable($name, 'Process')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'User')
        if ($value) {
            Set-Item "env:$name" $value
        }
    }
}

$visibilityValues = @{ Public = 0; FriendsOnly = 1; Private = 2; Unlisted = 3 }

$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'MachineEfficiency.csproj'
$template = Join-Path $PSScriptRoot 'base.vdf'
$descriptionFile = Join-Path $PSScriptRoot 'description.txt'
$contentFolder = Join-Path $PSScriptRoot 'content'
$filledVdf = Join-Path $PSScriptRoot 'base.tmp.vdf'

function ConvertTo-VdfPath([string] $path) {
    # Backslashes are doubled, as in Valve's own examples. Windows accepts the doubled form either way.
    $path.Replace('\', '\\')
}

function ConvertTo-VdfText([string] $text) {
    # SteamCMD does not process backslash escapes in these strings (a "\n" shows up literally on the Workshop page),
    # so newlines go in as real newlines. A double quote cannot be represented, so it becomes a single quote.
    $text.Replace('"', "'").Replace("`r`n", "`n")
}

# --- Checks -----------------------------------------------------------------------------------------------------

if (-not (Test-Path $PreviewImage)) {
    throw "Preview image not found: $PreviewImage. Put a picture at Steam\preview.png, or pass -PreviewImage."
}

$previewKb = (Get-Item $PreviewImage).Length / 1KB
if ($previewKb -gt 1024) {
    Write-Warning ("The preview image is {0:N0} KB. The Workshop expects it to be under 1 MB." -f $previewKb)
}

if (-not (Get-Command $SteamCmd -ErrorAction SilentlyContinue)) {
    throw "SteamCMD not found ('$SteamCmd'). Pass -SteamCmd <path to steamcmd.exe> or set the STEAMCMD variable."
}

# --- Build ------------------------------------------------------------------------------------------------------

if (Test-Path $contentFolder) {
    Remove-Item $contentFolder -Recurse -Force
}

Write-Host "Building into $contentFolder"
dotnet build $project -c Release "-p:OutputPath=$contentFolder\" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed.'
}

# --- Fill in the template ----------------------------------------------------------------------------------------

$description = (Get-Content $descriptionFile -Raw).Trim()

$templateText = Get-Content $template -Raw

$existingId = [regex]::Match($templateText, '"publishedfileid"\s+"(\d+)"').Groups[1].Value
$isNewItem = ($existingId -eq '0' -or $existingId -eq '')

if (-not $Visibility -and $isNewItem) {
    $Visibility = 'Private'
}

if (-not $Visibility) {
    # Leaving the key out keeps whatever visibility the item already has.
    $templateText = [regex]::Replace($templateText, '(?m)^[ \t]*"visibility".*\r?\n', '')
}

if ([string]::IsNullOrWhiteSpace($ChangeNote)) {
    # No note means no new entry in the item's Change Notes tab (the upload itself still goes through).
    $templateText = [regex]::Replace($templateText, '(?m)^[ \t]*"changenote".*\r?\n', '')
}

$filled = $templateText.
Replace('{{CONTENT_PATH}}', (ConvertTo-VdfPath $contentFolder)).
Replace('{{PREVIEW_IMAGE}}', (ConvertTo-VdfPath (Resolve-Path $PreviewImage).Path)).
Replace('{{VISIBILITY}}', [string] $visibilityValues[$Visibility]).
Replace('{{DESCRIPTION}}', (ConvertTo-VdfText $description)).
Replace('{{CHANGE_NOTE}}', (ConvertTo-VdfText $ChangeNote))

Set-Content -Path $filledVdf -Value $filled -Encoding ASCII

Write-Host ''
Write-Host "Item:        $(if ($isNewItem) { 'new (will be created)' } else { "existing, id $existingId" })"
Write-Host "Visibility:  $(if ($Visibility) { $Visibility } else { 'unchanged (not sent)' })"
Write-Host "Steam user:  $SteamUser"
Write-Host "Content:     $contentFolder"
Get-ChildItem $contentFolder -File | ForEach-Object { Write-Host ("             {0}  ({1:N0} B)" -f $_.Name, $_.Length) }
Write-Host "Preview:     $PreviewImage"

if (-not $Publish) {
    Write-Host ''
    Write-Host 'Dry run: nothing was uploaded. Re-run with -Publish to upload.'
    return
}

# --- Upload -----------------------------------------------------------------------------------------------------

Write-Host ''
Write-Host 'Uploading with SteamCMD (it may ask for your password and Steam Guard code)...'
& $SteamCmd +login $SteamUser +workshop_build_item $filledVdf +quit
if ($LASTEXITCODE -ne 0) {
    throw "SteamCMD exited with code $LASTEXITCODE."
}

# SteamCMD writes the new item's id into the file it was given; keep it in the template for the next update.
$newId = [regex]::Match((Get-Content $filledVdf -Raw), '"publishedfileid"\s+"(\d+)"').Groups[1].Value
if ($isNewItem -and $newId -and $newId -ne '0') {
    $updated = [regex]::Replace(
        (Get-Content $template -Raw),
        '("publishedfileid"\s+")\d+(")',
        { param($m) $m.Groups[1].Value + $newId + $m.Groups[2].Value })
    Set-Content -Path $template -Value $updated -Encoding ASCII -NoNewline

    Write-Host "Created Workshop item $newId and saved the id to Steam\base.vdf. Commit that file."
}

Remove-Item $filledVdf -Force
$finalId = if ($isNewItem) { $newId } else { $existingId }
Write-Host "Done. https://steamcommunity.com/sharedfiles/filedetails/?id=$finalId"
