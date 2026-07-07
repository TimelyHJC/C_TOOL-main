[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,
    [Parameter(Mandatory = $true)]
    [string]$BundleName,
    [Parameter(Mandatory = $true)]
    [string]$AppVersion,
    [Parameter(Mandatory = $true)]
    [ValidateSet("2024")]
    [string]$ReleaseBand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$componentCatalog = @(
    @{
        AppName = "C_TOOL"
        Description = "C_TOOL main panel"
        Net48Assembly = "C_TOOL_NetFx.dll"
    },
    @{
        AppName = "V_YYY"
        Description = "C_TOOL system settings"
        Net48Assembly = "V_YYY_NetFx.dll"
    },
    @{
        AppName = "V_AAA"
        Description = "C_TOOL folder blocks"
        Net48Assembly = "V_AAA_NetFx.dll"
    },
    @{
        AppName = "V_BBB"
        Description = "C_TOOL device list export"
        Net48Assembly = "V_BBB_NetFx.dll"
    },
    @{
        AppName = "V_DDD"
        Description = "C_TOOL text annotation"
        Net48Assembly = "V_DDD_NetFx.dll"
    },
    @{
        AppName = "V_QQQ"
        Description = "C_TOOL native plot"
        Net48Assembly = "V_QQQ_NetFx.dll"
    }
)

function Escape-Xml {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ""
    }

    return [System.Security.SecurityElement]::Escape($Value)
}

$normalizedBundleRoot = [System.IO.Path]::GetFullPath($BundleRoot)
[System.IO.Directory]::CreateDirectory($normalizedBundleRoot) | Out-Null

$bundleDescription = "C_TOOL AutoCAD bundle (2024)"
$runtimeRequirements = '    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R24.0" SeriesMax="R24.9" />'

$componentLines = foreach ($component in $componentCatalog) {
    $assemblyName = $component.Net48Assembly
    @(
        ('    <ComponentEntry AppName="{0}" Version="{1}" ModuleName="./Contents/Win64/{2}" AppDescription="{3}" LoadOnAutoCADStartup="True">' -f
            (Escape-Xml $component.AppName),
            (Escape-Xml $AppVersion),
            (Escape-Xml $assemblyName),
            (Escape-Xml $component.Description)),
        '      <Commands GroupName="CTOOL" />',
        '    </ComponentEntry>'
    )
}

$xmlLines = @(
    '<?xml version="1.0" encoding="utf-8"?>',
    ('<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="{0}" AppVersion="{1}" Description="{2}">' -f
        (Escape-Xml $BundleName),
        (Escape-Xml $AppVersion),
        (Escape-Xml $bundleDescription)),
    '  <CompanyDetails Name="C_TOOL" />',
    '  <Components Description="Runtime components">',
    $runtimeRequirements
) + $componentLines + @(
    '  </Components>',
    '</ApplicationPackage>'
)

$targetPath = Join-Path $normalizedBundleRoot "PackageContents.xml"
$xml = $xmlLines -join [System.Environment]::NewLine
[System.IO.File]::WriteAllText($targetPath, $xml, [System.Text.UTF8Encoding]::new($false))
