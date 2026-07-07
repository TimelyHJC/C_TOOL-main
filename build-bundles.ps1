[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [ValidateSet("All", "2024")]
    [string]$Target = "2024",

    [string]$Version = "",

    [switch]$CreateZip,

    [switch]$SkipIfZipUpToDate,

    [string]$Acad2024InstallPath = "C:\Program Files\Autodesk\AutoCAD 2024"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-DotnetBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string[]]$PropertyArguments,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $projectName = Split-Path -Leaf $ProjectPath
    Write-Host ("Building {0}..." -f $projectName)

    $arguments = @(
        "build",
        $ProjectPath,
        "-c", $Configuration,
        "-nologo",
        "-v:minimal",
        "-m:1",
        "-nr:false",
        "-p:UseSharedCompilation=false"
    ) + $PropertyArguments

    & dotnet @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet build failed: {0}" -f $projectName)
    }
}

function Assert-BundleOutput {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)][string[]]$RequiredFiles
    )

    if (-not (Test-Path -LiteralPath $BundleRoot)) {
        throw ("Bundle root not found: {0}" -f $BundleRoot)
    }

    $manifestPath = Join-Path $BundleRoot "PackageContents.xml"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw ("Missing PackageContents.xml: {0}" -f $BundleRoot)
    }

    $win64Path = Join-Path $BundleRoot "Contents\Win64"
    if (-not (Test-Path -LiteralPath $win64Path)) {
        throw ("Missing Contents\Win64: {0}" -f $BundleRoot)
    }

    $missingFiles = @(
        $RequiredFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $win64Path $_))
        }
    )

    if ($missingFiles.Count -gt 0) {
        throw ("Bundle is incomplete: {0} (missing: {1})" -f
            (Split-Path -Leaf $BundleRoot),
            ($missingFiles -join ", "))
    }

    $fileCount = @(Get-ChildItem -LiteralPath $BundleRoot -Recurse -File).Count
    Write-Host ("Ready: {0} ({1} files)" -f (Split-Path -Leaf $BundleRoot), $fileCount)
}

function Normalize-ProductVersion {
    param([AllowNull()][string]$Value)

    $normalized = if ($null -eq $Value) { "" } else { $Value.Trim() }
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    return $normalized
}

function Get-OptionalQlPluginProjectRoots {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $documentsDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $parentRoot = Split-Path -Parent $RepositoryRoot

    return @(
        (Join-Path $RepositoryRoot "src\QlPlugin"),
        (Join-Path $RepositoryRoot "ql\QlPlugin"),
        (Join-Path $parentRoot "ql\QlPlugin"),
        (Join-Path $documentsDir "GitHub\C_tools\ql\QlPlugin"),
        (Join-Path $desktopDir "C_tools\ql\QlPlugin")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
}

function Get-OptionalQlPluginProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    return Get-OptionalQlPluginProjectRoots -RepositoryRoot $RepositoryRoot |
        ForEach-Object { Join-Path $_ "QlPlugin.csproj" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

function Get-OptionalQlPluginCandidatePaths {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $projectRoots = Get-OptionalQlPluginProjectRoots -RepositoryRoot $RepositoryRoot
    $candidatePaths = foreach ($projectRoot in $projectRoots) {
        foreach ($subPath in @(
                ("bin\{0}\net48\QlPlugin.dll" -f $Configuration),
                ("obj\{0}\net48\QlPlugin.dll" -f $Configuration)
            )) {
            Join-Path $projectRoot $subPath
        }
    }

    return @($candidatePaths | Select-Object -Unique)
}

function Build-OptionalQlPlugin {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Acad2024InstallPath
    )

    $projectPath = Get-OptionalQlPluginProjectPath -RepositoryRoot $RepositoryRoot
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        return
    }

    Write-Host "Building optional QlPlugin for 2024 (net48)..."

    $arguments = @(
        "build",
        $projectPath,
        "-c", $Configuration,
        "-f", "net48",
        "-nologo",
        "-v:minimal",
        "-m:1",
        "-nr:false",
        "-p:UseSharedCompilation=false",
        ("-p:Acad2024InstallPath={0}" -f $Acad2024InstallPath)
    )
    if (-not [string]::IsNullOrWhiteSpace($script:ProductVersion)) {
        $arguments += ("-p:Version={0}" -f $script:ProductVersion)
        $arguments += ("-p:InformationalVersion={0}" -f $script:ProductVersion)
    }

    & dotnet @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Optional QlPlugin build failed; F_QL will rely on manual/runtime discovery."
    }
}

function Sync-OptionalQlPlugin {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Acad2024InstallPath
    )

    $bundleWin64Path = Join-Path $RepositoryRoot "C_TOOL_2024.bundle\Contents\Win64"
    if (-not (Test-Path -LiteralPath $bundleWin64Path)) {
        Write-Host "Optional QlPlugin sync skipped: missing C_TOOL_2024.bundle."
        return
    }

    Build-OptionalQlPlugin `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -Acad2024InstallPath $Acad2024InstallPath

    $targetDllPath = Join-Path $bundleWin64Path "QlPlugin.dll"
    $targetPdbPath = Join-Path $bundleWin64Path "QlPlugin.pdb"
    $candidateDllPath = Get-OptionalQlPluginCandidatePaths -RepositoryRoot $RepositoryRoot -Configuration $Configuration |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($candidateDllPath)) {
        if (Test-Path -LiteralPath $targetDllPath) {
            Write-Host ("Optional QlPlugin already present in {0}" -f $bundleWin64Path)
        }
        else {
            Write-Host "Optional QlPlugin not found; F_QL will rely on manual/runtime discovery."
        }

        return
    }

    $resolvedTargetDllPath = [System.IO.Path]::GetFullPath($targetDllPath)
    $resolvedCandidateDllPath = [System.IO.Path]::GetFullPath($candidateDllPath)
    if (-not [string]::Equals($resolvedCandidateDllPath, $resolvedTargetDllPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $resolvedCandidateDllPath -Destination $resolvedTargetDllPath -Force
    }

    $candidatePdbPath = [System.IO.Path]::ChangeExtension($resolvedCandidateDllPath, ".pdb")
    if (Test-Path -LiteralPath $candidatePdbPath) {
        Copy-Item -LiteralPath $candidatePdbPath -Destination $targetPdbPath -Force
    }

    Write-Host ("Optional QlPlugin synced: {0}" -f $resolvedCandidateDllPath)
}

$repoRoot = $PSScriptRoot
$script:ProductVersion = Normalize-ProductVersion -Value $Version
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

$zipScriptPath = Join-Path $repoRoot "src\C_toolsSetup\zip-bundle.ps1"
$projectBuildOrder = @(
    "src\C_toolsPlugin\C_toolsPlugin.csproj",
    "src\C_toolsSysPlugin\C_toolsSysPlugin.csproj",
    "src\C_toolsAaaPlugin\C_toolsAaaPlugin.csproj",
    "src\C_toolsBbbPlugin\C_toolsBbbPlugin.csproj",
    "src\C_toolsDddPlugin\C_toolsDddPlugin.csproj",
    "src\C_toolsQqqPlugin\C_toolsQqqPlugin.csproj"
) | ForEach-Object { Join-Path $repoRoot $_ }

foreach ($projectPath in $projectBuildOrder) {
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw ("Project not found: {0}" -f $projectPath)
    }
}

$bundleRoot = Join-Path $repoRoot "C_TOOL_2024.bundle"
$bundleZip = Join-Path $repoRoot "C_TOOL_2024.bundle.zip"
$requiredFiles = @(
    "C_TOOL_NetFx.dll",
    "V_YYY_NetFx.dll",
    "V_AAA_NetFx.dll",
    "V_BBB_NetFx.dll",
    "V_DDD_NetFx.dll",
    "V_QQQ_NetFx.dll"
)

$propertyArguments = @(
    ("-p:Acad2024InstallPath={0}" -f $Acad2024InstallPath)
)
if (-not [string]::IsNullOrWhiteSpace($script:ProductVersion)) {
    $propertyArguments += ("-p:Version={0}" -f $script:ProductVersion)
    $propertyArguments += ("-p:InformationalVersion={0}" -f $script:ProductVersion)
}

Write-Host ("Repository root: {0}" -f $repoRoot)
Write-Host ("Configuration: {0}" -f $Configuration)
Write-Host ("Target: {0}" -f $Target)
Write-Host ("Product version: {0}" -f $(if ([string]::IsNullOrWhiteSpace($script:ProductVersion)) { "(project defaults)" } else { $script:ProductVersion }))
Write-Host ("AutoCAD 2024 path: {0}" -f $Acad2024InstallPath)

Push-Location $repoRoot
try {
    foreach ($projectPath in $projectBuildOrder) {
        Invoke-DotnetBuild -ProjectPath $projectPath -PropertyArguments $propertyArguments -Configuration $Configuration
    }

    Sync-OptionalQlPlugin `
        -RepositoryRoot $repoRoot `
        -Configuration $Configuration `
        -Acad2024InstallPath $Acad2024InstallPath

    Assert-BundleOutput -BundleRoot $bundleRoot -RequiredFiles $requiredFiles

    if ($CreateZip) {
        if (-not (Test-Path -LiteralPath $zipScriptPath)) {
            throw ("Zip helper script not found: {0}" -f $zipScriptPath)
        }

        $zipArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $zipScriptPath,
            "-Source", $bundleRoot,
            "-ZipOut", $bundleZip
        )
        if ($SkipIfZipUpToDate) {
            $zipArguments += "-SkipIfUpToDate"
        }

        & powershell @zipArguments 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw ("Bundle zip failed: {0}" -f $bundleZip)
        }

        Write-Host ("Zip ready: {0}" -f $bundleZip)
    }
}
finally {
    Pop-Location
}
