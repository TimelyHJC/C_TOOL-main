[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Prerelease,
    [switch]$SkipBundles,
    [switch]$SkipInstaller,
    [switch]$SkipTag,
    [switch]$SkipGitHubRelease,
    [switch]$SkipIfZipUpToDate,
    [switch]$UploadBundlesToExistingRelease,
    [string]$UpdateBaseUrl = "",
    [switch]$SkipUpdateManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-ValidVersion {
    param([Parameter(Mandatory = $true)][string]$ReleaseVersion)

    if ($ReleaseVersion -notmatch '^v\d+\.\d+\.\d+([\-\.][0-9A-Za-z.-]+)?$') {
        throw "Version must look like v1.2.3, v1.2.3-beta.1, or v1.2.3-rc.1."
    }
}

function Assert-TagDoesNotExist {
    param([Parameter(Mandatory = $true)][string]$ReleaseVersion)

    $existing = & git ls-remote --tags origin ("refs/tags/{0}" -f $ReleaseVersion)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query remote tags."
    }

    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        throw ("Remote tag already exists: {0}" -f $ReleaseVersion)
    }
}

function Assert-GitHubReleaseExists {
    param([Parameter(Mandatory = $true)][string]$ReleaseVersion)

    $previousErrorActionPreference = $ErrorActionPreference
    $script:ErrorActionPreference = "Continue"
    try {
        & gh release view $ReleaseVersion --json tagName --jq .tagName 2>&1 | Out-Host
    }
    finally {
        $script:ErrorActionPreference = $previousErrorActionPreference
    }

    if ($LASTEXITCODE -ne 0) {
        throw ("GitHub release not found: {0}" -f $ReleaseVersion)
    }
}

function Invoke-CommandChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $script:ErrorActionPreference = "Continue"
    try {
        & $FilePath @Arguments 2>&1 | Out-Host
    }
    finally {
        $script:ErrorActionPreference = $previousErrorActionPreference
    }

    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-ArtifactPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    return (Join-Path $repoRoot $RelativePath)
}

function Normalize-ProductVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Trim()
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    return $normalized
}

function Join-UpdateAssetUrl {
    param(
        [AllowNull()][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        return ("releases/{0}/{1}" -f $ReleaseVersion, $FileName)
    }

    return ("{0}/{1}" -f $BaseUrl.TrimEnd('/'), $FileName)
}

function New-UpdateManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$ProductVersion,
        [Parameter(Mandatory = $true)][string]$BundleZipPath,
        [AllowNull()][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [AllowNull()][string]$BaseUrl
    )

    if (-not (Test-Path -LiteralPath $BundleZipPath)) {
        throw ("Bundle zip not found for update manifest: {0}" -f $BundleZipPath)
    }

    $bundleFileName = Split-Path -Leaf $BundleZipPath
    $bundleSha256 = (Get-FileHash -LiteralPath $BundleZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerUrl = ""
    if (-not [string]::IsNullOrWhiteSpace($InstallerPath) -and (Test-Path -LiteralPath $InstallerPath)) {
        $installerUrl = Join-UpdateAssetUrl -BaseUrl $BaseUrl -ReleaseVersion $ReleaseVersion -FileName (Split-Path -Leaf $InstallerPath)
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        version = $ProductVersion
        cad = "2024"
        bundleName = ([System.IO.Path]::GetFileNameWithoutExtension($bundleFileName))
        bundleZipUrl = (Join-UpdateAssetUrl -BaseUrl $BaseUrl -ReleaseVersion $ReleaseVersion -FileName $bundleFileName)
        bundleZipSha256 = $bundleSha256
        setupUrl = $installerUrl
        releaseNotes = ("C_TOOL {0}" -f $ReleaseVersion)
        publishedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("o")
    }

    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Host ("Update manifest ready: {0}" -f $OutputPath)
}

Assert-ValidVersion -ReleaseVersion $Version

if ($UploadBundlesToExistingRelease) {
    if ($Prerelease) {
        throw "-Prerelease cannot be used with -UploadBundlesToExistingRelease because this mode only updates assets on an existing release."
    }

    if ($SkipGitHubRelease) {
        throw "-SkipGitHubRelease cannot be used with -UploadBundlesToExistingRelease."
    }
}

$repoRoot = $PSScriptRoot
$productVersion = Normalize-ProductVersion -Value $Version
$dotnetHome = Join-Path $repoRoot ".dotnet-home"
New-Item -ItemType Directory -Force -Path $dotnetHome | Out-Null

$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
$env:DOTNET_NOLOGO = "true"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:VSLANG = "1033"
$env:PreferredUILang = "en-US"

$allBundleZipPaths = @(
    "C_TOOL_2024.bundle.zip"
) | ForEach-Object { Get-ArtifactPath $_ }

$bundleZipPaths = @()
$installerPath = Get-ArtifactPath "C_TOOL_Setup.exe"
$updateManifestPath = Get-ArtifactPath "latest.json"
$releaseAssetPaths = New-Object System.Collections.Generic.List[string]

Write-Host ("Repository root: {0}" -f $repoRoot)
Write-Host ("Configuration: {0}" -f $Configuration)
Write-Host ("Version: {0}" -f $Version)
Write-Host ("Product version: {0}" -f $productVersion)
Write-Host ("Prerelease: {0}" -f $(if ($Prerelease) { "yes" } else { "no" }))
Write-Host ("Upload bundles to existing release: {0}" -f $(if ($UploadBundlesToExistingRelease) { "yes" } else { "no" }))
Write-Host ("Update manifest: {0}" -f $(if ($SkipUpdateManifest) { "skip" } else { "generate" }))

Push-Location $repoRoot
try {
    if (-not $SkipBundles) {
        $bundleBuildArgs = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", (Join-Path $repoRoot "build-bundles.ps1"),
            "-Configuration", $Configuration,
            "-Target", "2024",
            "-Version", $productVersion,
            "-CreateZip"
        )
        if ($SkipIfZipUpToDate) {
            $bundleBuildArgs += "-SkipIfZipUpToDate"
        }

        Invoke-CommandChecked `
            -FilePath "powershell" `
            -Arguments $bundleBuildArgs `
            -FailureMessage "Bundle build failed."
    }

    $bundleZipPaths = @($allBundleZipPaths | Where-Object { Test-Path -LiteralPath $_ })

    if ($UploadBundlesToExistingRelease) {
        Assert-GitHubReleaseExists -ReleaseVersion $Version

        if ($bundleZipPaths.Count -eq 0) {
            throw "No bundle zip assets were found to upload."
        }

        $uploadArgs = @(
            "release",
            "upload",
            $Version
        )

        foreach ($bundleZipPath in $bundleZipPaths) {
            $uploadArgs += $bundleZipPath
        }

        $uploadArgs += "--clobber"

        Invoke-CommandChecked -FilePath "gh" -Arguments $uploadArgs -FailureMessage "GitHub bundle asset upload failed."
        return
    }

    foreach ($bundleZipPath in $bundleZipPaths) {
        $releaseAssetPaths.Add($bundleZipPath)
    }

    if (-not $SkipInstaller) {
        $installerBuildArgs = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", (Join-Path $repoRoot "build-installer.ps1"),
            "-Configuration", $Configuration,
            "-Version", $productVersion,
            "-SingleFile",
            "-SyncToRoot"
        )
        if (-not $SkipBundles) {
            $installerBuildArgs += "-IncludeBundles"
        }

        Invoke-CommandChecked `
            -FilePath "powershell" `
            -Arguments $installerBuildArgs `
            -FailureMessage "Installer build failed."

        if (Test-Path -LiteralPath $installerPath) {
            $releaseAssetPaths.Add($installerPath)
        }
    }

    if (-not $SkipUpdateManifest) {
        if ($bundleZipPaths.Count -eq 0) {
            Write-Warning "Update manifest skipped: no bundle zip asset was found."
        }
        else {
            New-UpdateManifest `
                -ReleaseVersion $Version `
                -ProductVersion $productVersion `
                -BundleZipPath $bundleZipPaths[0] `
                -InstallerPath $installerPath `
                -OutputPath $updateManifestPath `
                -BaseUrl $UpdateBaseUrl
            $releaseAssetPaths.Add($updateManifestPath)
        }
    }

    if (-not $SkipTag) {
        Assert-TagDoesNotExist -ReleaseVersion $Version
        Invoke-CommandChecked -FilePath "git" -Arguments @("tag", "-a", $Version, "-m", ("C_TOOL {0}" -f $Version)) -FailureMessage "Git tag creation failed."
        Invoke-CommandChecked -FilePath "git" -Arguments @("push", "origin", $Version) -FailureMessage "Git tag push failed."
    }

    if (-not $SkipGitHubRelease) {
        if ($releaseAssetPaths.Count -eq 0) {
            throw "No release assets were generated."
        }

        $releaseNotesPath = Join-Path $env:TEMP ("c_tool_release_{0}.md" -f $Version.TrimStart("v"))
        $assetNames = $releaseAssetPaths | ForEach-Object { "- {0}" -f (Split-Path -Leaf $_) }
        @"
## Summary
- C_TOOL $Version
- Includes the assets built for this release run.

## Compatibility
- AutoCAD 2024

## Assets
$($assetNames -join [Environment]::NewLine)
"@ | Set-Content -LiteralPath $releaseNotesPath

        $releaseArgs = @(
            "release",
            "create",
            $Version,
            "--title", ("C_TOOL {0}" -f $Version),
            "--notes-file", $releaseNotesPath
        )

        if ($Prerelease) {
            $releaseArgs += "--prerelease"
        }

        foreach ($assetPath in $releaseAssetPaths) {
            $releaseArgs += $assetPath
        }

        Invoke-CommandChecked -FilePath "gh" -Arguments $releaseArgs -FailureMessage "GitHub release creation failed."
    }
}
finally {
    Pop-Location
}
