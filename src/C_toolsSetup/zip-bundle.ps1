param(
    [Parameter(Mandatory = $true)][string] $Source,
    [Parameter(Mandatory = $true)][string] $ZipOut,
    [ValidateSet('Fastest', 'Optimal')][string] $CompressionLevel = 'Optimal',
    [switch] $SkipIfUpToDate
)
$ErrorActionPreference = "Stop"
$Source = $Source.TrimEnd('\', '/')
$ZipOut = [System.IO.Path]::GetFullPath($ZipOut)
if (-not (Test-Path (Join-Path $Source "PackageContents.xml"))) {
    Write-Error "Bundle 源目录无效（缺少 PackageContents.xml）: $Source"
}
if ($SkipIfUpToDate -and (Test-Path -LiteralPath $ZipOut)) {
    $markerTimes = @()
    $win64 = Join-Path $Source "Contents\Win64"
    if (Test-Path -LiteralPath $win64) {
        Get-ChildItem -LiteralPath $win64 -Filter "*.dll" -ErrorAction SilentlyContinue |
            ForEach-Object { $markerTimes += $_.LastWriteTimeUtc }
    }
    $pkg = Join-Path $Source "PackageContents.xml"
    if (Test-Path -LiteralPath $pkg) { $markerTimes += (Get-Item -LiteralPath $pkg).LastWriteTimeUtc }
    if ($markerTimes.Count -gt 0) {
        $newest = ($markerTimes | Measure-Object -Maximum).Maximum
        if ((Get-Item -LiteralPath $ZipOut).LastWriteTimeUtc -ge $newest) {
            Write-Host "Bundle zip up to date, skip compress."
            exit 0
        }
    }
}
$dir = Split-Path -Parent $ZipOut
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
if (Test-Path -LiteralPath $ZipOut) {
    try {
        Remove-Item -LiteralPath $ZipOut -Force -ErrorAction Stop
    } catch {
        Start-Sleep -Milliseconds 400
        Remove-Item -LiteralPath $ZipOut -Force
    }
}
$zipLevel = [System.IO.Compression.CompressionLevel] $CompressionLevel
try {
    Compress-Archive -LiteralPath $Source -DestinationPath $ZipOut -CompressionLevel $CompressionLevel -Force
} catch {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($Source, $ZipOut, $zipLevel, $true)
}
