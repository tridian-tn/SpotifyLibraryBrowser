<#
.SYNOPSIS
    Builds the release artefacts: a portable zip and a self-contained zip.

.DESCRIPTION
    Publishes the app twice as a single file. The portable build needs the .NET 10 runtime on the
    target machine, which is what keeps it small and lets the runtime be serviced independently.
    The self-contained build carries the runtime with it, so it needs nothing installed — worth the
    extra megabytes for anyone who'd otherwise have to go and find a prerequisite first.

    The version comes from -Version when it is given, and from Directory.Build.props otherwise.
    Given, it is passed to dotnet publish as well, so the stamped assembly, the filenames and the
    release tag cannot disagree. Not given, the props are the one place to change it and the rest
    still cannot disagree — CI passes the tag through when there is one.

.PARAMETER Version
    The version to build, e.g. 1.2.3, optionally with a pre-release part. Overrides
    Directory.Build.props and stamps the assembly with the same number the filenames carry.

.PARAMETER SkipTests
    Package without running the test suite first. For iterating on the packaging itself, and for
    CI, where the suite has just run in the job before this one.

.EXAMPLE
    ./packaging/build.ps1

.EXAMPLE
    ./packaging/build.ps1 -Version 1.2.3 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'SpotifyLibraryBrowser.slnx'
$app = Join-Path $root 'src/SpotifyLibraryBrowser.App/SpotifyLibraryBrowser.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Get-ProductVersion {
    param([string]$Override)

    # An override wins so CI can carry the release tag through. Not given, asked of MSBuild rather
    # than parsed out of the props file, so this is the version that actually stamped the assembly
    # even if a project overrides it.
    $resolved = if ($Override) {
        $Override
    } else {
        (dotnet msbuild $app -getProperty:Version -nologo).Trim()
    }

    if (-not $resolved) { throw 'Could not work out a version to build.' }

    # Caught here rather than after a few minutes of publishing, where a malformed version would
    # only show up in a filename nobody can match to a tag.
    if ($resolved -notmatch '^\d+(\.\d+){0,3}(-[0-9A-Za-z.-]+)?$') {
        throw "'$resolved' isn't a version. Expected something like 1.2.3, optionally -prerelease."
    }

    return $resolved
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with $LASTEXITCODE." }
}

$productVersion = Get-ProductVersion -Override $Version
Write-Host "Building $productVersion"

if (-not $SkipTests) {
    Write-Host 'Running the tests...'
    Invoke-Dotnet @('test', $solution, '-c', 'Release')
}

# Cleared rather than merged into, so a rename or a dropped file can't leave the previous run's
# output sitting in this one's zip.
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$builds = @(
    @{ Name = 'portable'; SelfContained = 'false' }
    @{ Name = 'self-contained'; SelfContained = 'true' }
)

foreach ($build in $builds) {
    $target = Join-Path $publish $build.Name
    Write-Host "Publishing $($build.Name)..."

    Invoke-Dotnet @(
        'publish', $app,
        '-c', 'Release',
        '-r', 'win-x64',
        "--self-contained", $build.SelfContained,
        '-p:PublishSingleFile=true',
        "-p:Version=$productVersion",
        '-o', $target
    )

    $zip = Join-Path $artifacts "SpotifyLibraryBrowser-$productVersion-win-x64-$($build.Name).zip"
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip -Force
}

# Checksums, so a download can be checked against what the release page says it should be.
$sums = Join-Path $artifacts 'SHA256SUMS.txt'
Get-ChildItem $artifacts -Filter *.zip |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content $sums -Encoding ascii

Write-Host ''
Get-ChildItem $artifacts -File | ForEach-Object {
    Write-Host ("  {0}  {1} MB" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
