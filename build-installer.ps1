[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$SingleFile,
    [switch]$IncludeBundles,
    [switch]$SyncToRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Ensure-TrailingSeparator {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($Path.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or
        $Path.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
        return $Path
    }

    return $Path + [System.IO.Path]::DirectorySeparatorChar
}

function Normalize-ProductVersion {
    param([AllowNull()][string]$Value)

    $normalized = if ($null -eq $Value) { "" } else { $Value.Trim() }
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    return $normalized
}

function Invoke-InstallerPublish {
    param([string]$PublishDirOverride)

    $arguments = @(
        "publish",
        $script:ProjectPath,
        "-c", $Configuration,
        "-nologo",
        "-v:minimal",
        "-m:1",
        "-nr:false",
        "-p:UseSharedCompilation=false",
        ("-p:PublishProfile={0}" -f $script:PublishProfile),
        ("-p:C_toolsSkipBundleOnPublish={0}" -f $script:SkipBundleOnPublish)
    )
    if (-not [string]::IsNullOrWhiteSpace($script:ProductVersion)) {
        $arguments += ("-p:Version={0}" -f $script:ProductVersion)
        $arguments += ("-p:InformationalVersion={0}" -f $script:ProductVersion)
    }

    if (-not [string]::IsNullOrWhiteSpace($PublishDirOverride)) {
        $arguments += ("-p:PublishDir={0}" -f $PublishDirOverride)
    }

    & dotnet @arguments 2>&1 | Out-Host
    return [int]$LASTEXITCODE
}

function Sync-InstallerArtifactsToRepoRoot {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    if (-not (Test-Path -LiteralPath $PublishDirectory)) {
        throw ("Publish directory not found: {0}" -f $PublishDirectory)
    }

    $artifactNames = @(
        "C_TOOL_Setup.exe",
        "C_TOOL_Setup.dll",
        "C_TOOL_Setup.deps.json",
        "C_TOOL_Setup.runtimeconfig.json",
        "C_TOOL_Setup.pdb"
    )

    foreach ($name in $artifactNames) {
        $sourcePath = Join-Path $PublishDirectory $name
        $targetPath = Join-Path $RepositoryRoot $name

        if (Test-Path -LiteralPath $sourcePath) {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
            continue
        }

        if (Test-Path -LiteralPath $targetPath) {
            Remove-Item -LiteralPath $targetPath -Force
        }
    }

    $bundlesDir = Join-Path $RepositoryRoot "Bundles"
    $bundlesSourceDir = Join-Path $RepositoryRoot "src\C_toolsSetup\PublishExtras\Bundles"
    $bundlesReadmeSource = Get-ChildItem -LiteralPath $bundlesSourceDir -File |
        Where-Object { $_.Extension -eq ".txt" } |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($bundlesReadmeSource)) {
        throw ("Bundles readme not found under: {0}" -f $bundlesSourceDir)
    }

    $bundlesReadmeTarget = Join-Path $bundlesDir (Split-Path -Leaf $bundlesReadmeSource)
    New-Item -ItemType Directory -Force -Path $bundlesDir | Out-Null
    Copy-Item -LiteralPath $bundlesReadmeSource -Destination $bundlesReadmeTarget -Force
}

function Get-BundleDirectoriesForPublish {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    if (-not (Test-Path -LiteralPath $RepositoryRoot)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $RepositoryRoot -Directory -Filter "C_TOOL_2024.bundle" |
        Sort-Object Name, FullName |
        Select-Object -ExpandProperty FullName)
}

function Resolve-BundleRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)][string]$ModuleName
    )

    $bundleRootFullPath = [System.IO.Path]::GetFullPath($BundleRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $relativePath = $ModuleName.Trim().Replace('/', '\')
    while ($relativePath.StartsWith(".\")) {
        $relativePath = $relativePath.Substring(2)
    }

    if ([System.IO.Path]::IsPathRooted($relativePath)) {
        throw ("Bundle 中的 ModuleName 不应为绝对路径：{0}" -f $ModuleName)
    }

    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $bundleRootFullPath $relativePath))
    $bundleRootPrefix = $bundleRootFullPath + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedPath -ne $bundleRootFullPath -and
        $resolvedPath -notlike "$bundleRootPrefix*") {
        throw ("Bundle 中的 ModuleName 越界：{0}" -f $ModuleName)
    }

    return $resolvedPath
}

function Get-BundleStartupModules {
    param(
        [Parameter(Mandatory = $true)][string]$BundleDirectory
    )

    $packageContentsPath = Join-Path $BundleDirectory "PackageContents.xml"
    if (-not (Test-Path -LiteralPath $packageContentsPath)) {
        throw ("Bundle 缺少 PackageContents.xml：{0}" -f $BundleDirectory)
    }

    [xml]$packageContents = Get-Content -LiteralPath $packageContentsPath
    $componentEntries = @($packageContents.SelectNodes("//*[local-name()='ComponentEntry']"))
    $modules = New-Object System.Collections.Generic.List[object]

    foreach ($entry in $componentEntries) {
        $loadOnStartup = [string]$entry.GetAttribute("LoadOnAutoCADStartup")
        if (-not [string]::IsNullOrWhiteSpace($loadOnStartup) -and
            $loadOnStartup -match '^(false|0|no)$') {
            continue
        }

        $moduleName = [string]$entry.GetAttribute("ModuleName")
        if ([string]::IsNullOrWhiteSpace($moduleName) -or
            [System.IO.Path]::GetExtension($moduleName) -ine ".dll") {
            continue
        }

        $resolvedPath = Resolve-BundleRelativePath -BundleRoot $BundleDirectory -ModuleName $moduleName
        $appName = [string]$entry.GetAttribute("AppName")
        if ([string]::IsNullOrWhiteSpace($appName)) {
            $appName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath)
        }

        $modules.Add([pscustomobject]@{
                AppName = $appName
                ModuleName = $moduleName
                ModulePath = $resolvedPath
            })
    }

    return $modules.ToArray()
}

function Assert-BundleReadyForPublish {
    param(
        [Parameter(Mandatory = $true)][string]$BundleDirectory
    )

    $bundleName = Split-Path -Leaf $BundleDirectory
    $modules = @(Get-BundleStartupModules -BundleDirectory $BundleDirectory)
    if ($modules.Count -eq 0) {
        throw ("Bundle 未声明可自动加载的 DLL：{0}" -f $bundleName)
    }

    $missingModules = @($modules | Where-Object { -not (Test-Path -LiteralPath $_.ModulePath) })
    if ($missingModules.Count -gt 0) {
        $details = ($missingModules | ForEach-Object {
                "{0} -> {1}" -f $_.AppName, $_.ModuleName
            }) -join "; "
        throw ("Bundle 缺少清单中声明的启动 DLL：{0}（{1}）" -f $bundleName, $details)
    }
}

function Copy-BundlesToPublishDirectory {
    param(
        [Parameter(Mandatory = $true)][string[]]$BundleDirectories,
        [Parameter(Mandatory = $true)][string]$PublishDirectory
    )

    $bundlesTargetRoot = Join-Path $PublishDirectory "Bundles"
    New-Item -ItemType Directory -Force -Path $bundlesTargetRoot | Out-Null

    foreach ($bundleDirectory in $BundleDirectories) {
        $bundleName = Split-Path -Leaf $bundleDirectory
        $targetDirectory = Join-Path $bundlesTargetRoot $bundleName
        if (Test-Path -LiteralPath $targetDirectory) {
            Remove-Item -LiteralPath $targetDirectory -Recurse -Force
        }

        Copy-Item -LiteralPath $bundleDirectory -Destination $targetDirectory -Recurse
        Write-Host ("Bundled plugin copied: {0}" -f $targetDirectory)
    }
}

$repoRoot = $PSScriptRoot
$script:ProjectPath = Join-Path $repoRoot "src\C_toolsSetup\C_TOOL_Setup.csproj"
if (-not (Test-Path -LiteralPath $script:ProjectPath)) {
    throw ("Setup project not found: {0}" -f $script:ProjectPath)
}

$script:PublishProfile = if ($SingleFile) { "Win64SingleFile" } else { "Win64FrameworkDependent" }
$script:SkipBundleOnPublish = if ($IncludeBundles) { "false" } else { "true" }
$script:ProductVersion = Normalize-ProductVersion -Value $Version

$defaultPublishDir = Join-Path $repoRoot ("src\C_toolsSetup\bin\{0}\net8.0-windows\win-x64\publish" -f $Configuration)
$fallbackPublishDir = Join-Path $repoRoot ("src\C_toolsSetup\bin\{0}\net8.0-windows\publish-local-{1}" -f $Configuration, (Get-Date -Format "yyyyMMdd-HHmmss"))
$fallbackPublishDir = Ensure-TrailingSeparator $fallbackPublishDir

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

Write-Host ("Publishing setup project: {0}" -f $script:ProjectPath)
Write-Host ("Configuration: {0}" -f $Configuration)
Write-Host ("Profile: {0}" -f $script:PublishProfile)
Write-Host ("Product version: {0}" -f $(if ([string]::IsNullOrWhiteSpace($script:ProductVersion)) { "(project default)" } else { $script:ProductVersion }))
Write-Host ("Include bundles: {0}" -f $(if ($IncludeBundles) { "yes" } else { "no" }))
Write-Host ("Sync installer to repo root: {0}" -f $(if ($SyncToRoot) { "yes" } else { "no" }))

$bundleDirectories = @()
if ($IncludeBundles) {
    $bundleDirectories = @(Get-BundleDirectoriesForPublish -RepositoryRoot $repoRoot)
    if ($bundleDirectories.Count -eq 0) {
        throw "未在仓库根目录发现任何 *.bundle；请先构建 bundle，再使用 -IncludeBundles。"
    }

    foreach ($bundleDirectory in $bundleDirectories) {
        Assert-BundleReadyForPublish -BundleDirectory $bundleDirectory
    }

    Write-Host ("Validated bundles: {0}" -f (($bundleDirectories | ForEach-Object { Split-Path -Leaf $_ }) -join ", "))
}

Push-Location $repoRoot
try {
    $publishOutputDir = $null
    $exitCode = Invoke-InstallerPublish ""
    if ($exitCode -eq 0) {
        $publishOutputDir = $defaultPublishDir
        Write-Host ("Publish completed: {0}" -f $publishOutputDir)
    }
    else {
        Write-Warning ("Default publish directory failed; retrying with fallback directory: {0}" -f $fallbackPublishDir)
        $exitCode = Invoke-InstallerPublish $fallbackPublishDir
        if ($exitCode -ne 0) {
            throw ("dotnet publish failed with exit code: {0}" -f $exitCode)
        }

        $publishOutputDir = $fallbackPublishDir
        Write-Host ("Publish completed: {0}" -f $publishOutputDir)
    }

    if ($IncludeBundles) {
        Copy-BundlesToPublishDirectory -BundleDirectories $bundleDirectories -PublishDirectory $publishOutputDir
    }

    if ($SyncToRoot) {
        Sync-InstallerArtifactsToRepoRoot -PublishDirectory $publishOutputDir -RepositoryRoot $repoRoot
        Write-Host ("Repo root installer artifacts synced from: {0}" -f $publishOutputDir)
    }
}
finally {
    Pop-Location
}
