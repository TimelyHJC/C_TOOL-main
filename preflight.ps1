[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Acad2024InstallPath = "C:\Program Files\Autodesk\AutoCAD 2024",

    [switch]$EnvironmentOnly,

    [switch]$SkipInstaller,

    [switch]$SkipBundleZip,

    [switch]$RequireGitHubCli
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw ("Required tool not found: {0}" -f $Name)
    }
}

function Assert-DotnetSdk {
    $sdks = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query installed .NET SDKs."
    }

    if ($sdks.Count -eq 0) {
        throw ".NET SDK not found. Install the .NET SDK on this machine."
    }
}

function Assert-AcadInstall {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("Required {0} path not found: {1}" -f $Label, $Path)
    }

    $requiredFiles = @(
        "AcCoreMgd.dll",
        "AcDbMgd.dll",
        "AcMgd.dll",
        "AdWindows.dll"
    )

    foreach ($file in $requiredFiles) {
        $candidate = Join-Path $Path $file
        if (-not (Test-Path -LiteralPath $candidate)) {
            throw ("Required {0} assembly not found: {1}" -f $Label, $candidate)
        }
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

$repoRoot = $PSScriptRoot
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

Write-Host ("Repository root: {0}" -f $repoRoot)
Write-Host ("Configuration: {0}" -f $Configuration)
Write-Host ("Environment only: {0}" -f $(if ($EnvironmentOnly) { "yes" } else { "no" }))
Write-Host ("AutoCAD 2024 path: {0}" -f $Acad2024InstallPath)

Assert-CommandExists -Name "dotnet"
Assert-CommandExists -Name "powershell"
if ($RequireGitHubCli) {
    Assert-CommandExists -Name "git"
    Assert-CommandExists -Name "gh"
}

Assert-DotnetSdk
Assert-AcadInstall -Label "AutoCAD 2024" -Path $Acad2024InstallPath

if ($env:GITHUB_WORKSPACE -and (Get-Command git -ErrorAction SilentlyContinue)) {
    & git config --global --add safe.directory $env:GITHUB_WORKSPACE
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to mark GitHub workspace as a safe git directory."
    }
}

Write-Host "Environment check passed."

if ($EnvironmentOnly) {
    return
}

Push-Location $repoRoot
try {
    $bundleArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repoRoot "build-bundles.ps1"),
        "-Configuration", $Configuration,
        "-Target", "2024",
        "-Acad2024InstallPath", $Acad2024InstallPath
    )

    if (-not $SkipBundleZip) {
        $bundleArguments += @("-CreateZip", "-SkipIfZipUpToDate")
    }

    Invoke-CommandChecked `
        -FilePath "powershell" `
        -Arguments $bundleArguments `
        -FailureMessage "Bundle preflight failed for AutoCAD 2024."

    if (-not $SkipInstaller) {
        $installerArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", (Join-Path $repoRoot "build-installer.ps1"),
            "-Configuration", $Configuration,
            "-SingleFile",
            "-IncludeBundles",
            "-SyncToRoot"
        )

        Invoke-CommandChecked `
            -FilePath "powershell" `
            -Arguments $installerArguments `
            -FailureMessage "Installer preflight failed."
    }
}
finally {
    Pop-Location
}

Write-Host "Preflight completed."
