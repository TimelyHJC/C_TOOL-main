[CmdletBinding()]
param(
    [string]$Owner = "TimelyHJC",
    [string]$Repo = "C_TOOL",
    [string]$RunnerRoot = (Join-Path $env:USERPROFILE "actions-runner-c_tool"),
    [string]$RunnerName = ("{0}-C_TOOL" -f $env:COMPUTERNAME),
    [string]$Labels = "c-tool,autocad,windows",
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [switch]$ConfigureService,
    [switch]$ReplaceExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Tool {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw ("Required tool not found: {0}" -f $Name)
    }
}

function Assert-PathExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("Required path not found: {0}" -f $Path)
    }
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $FilePath @Arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-ExistingRunnerId {
    param([Parameter(Mandatory = $true)][string]$RunnerName)

    $json = gh api ("repos/{0}/{1}/actions/runners" -f $Owner, $Repo)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query existing runners."
    }

    $response = $json | ConvertFrom-Json
    $runnerId = @($response.runners | Where-Object { $_.name -eq $RunnerName } | Select-Object -First 1 -ExpandProperty id)
    if ($runnerId.Count -gt 0) {
        return [string]$runnerId[0]
    }

    if ([string]::IsNullOrWhiteSpace($runnerId)) {
        return $null
    }

    return [string]$runnerId
}

function Remove-ExistingRemoteRunner {
    param([Parameter(Mandatory = $true)][string]$RunnerName)

    $runnerId = Get-ExistingRunnerId -RunnerName $RunnerName
    if ([string]::IsNullOrWhiteSpace($runnerId)) {
        return
    }

    Write-Host ("Removing existing GitHub runner registration: {0} (id {1})" -f $RunnerName, $runnerId)
    Invoke-NativeChecked -FilePath "gh" -Arguments @(
        "api",
        "-X", "DELETE",
        ("repos/{0}/{1}/actions/runners/{2}" -f $Owner, $Repo, $runnerId)
    ) -FailureMessage "Failed to remove existing runner registration."
}

function Remove-ExistingLocalRunnerConfiguration {
    param([Parameter(Mandatory = $true)][string]$RunnerRoot)

    $configPath = Join-Path $RunnerRoot "config.cmd"
    $runnerStatePath = Join-Path $RunnerRoot ".runner"
    if (-not (Test-Path -LiteralPath $configPath) -or -not (Test-Path -LiteralPath $runnerStatePath)) {
        return
    }

    Write-Host "Removing existing local runner configuration."
    Push-Location $RunnerRoot
    try {
        & .\config.cmd remove --local 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to remove local runner configuration."
        }
    }
    finally {
        Pop-Location
    }
}

Assert-Tool -Name "gh"
Assert-Tool -Name "git"
Assert-Tool -Name "powershell"

Assert-PathExists -Path "C:\Program Files\Autodesk\AutoCAD 2024"

$repoUrl = "https://github.com/{0}/{1}" -f $Owner, $Repo
$runnerVersionTag = (gh api repos/actions/runner/releases/latest --jq ".tag_name").Trim()
if ([string]::IsNullOrWhiteSpace($runnerVersionTag)) {
    throw "Unable to resolve the latest actions runner version."
}

$runnerVersion = $runnerVersionTag.TrimStart("v")
$runnerZipName = "actions-runner-win-{0}-{1}.zip" -f $Architecture, $runnerVersion
$runnerDownloadUrl = "https://github.com/actions/runner/releases/download/{0}/{1}" -f $runnerVersionTag, $runnerZipName
$runnerZipPath = Join-Path $RunnerRoot $runnerZipName

Write-Host ("Repository: {0}" -f $repoUrl)
Write-Host ("Runner root: {0}" -f $RunnerRoot)
Write-Host ("Runner name: {0}" -f $RunnerName)
Write-Host ("Runner labels: {0}" -f $Labels)
Write-Host ("Runner package: {0}" -f $runnerZipName)

New-Item -ItemType Directory -Force -Path $RunnerRoot | Out-Null

if (-not (Test-Path -LiteralPath $runnerZipPath)) {
    Invoke-WebRequest -Uri $runnerDownloadUrl -OutFile $runnerZipPath
}

$configCmdPath = Join-Path $RunnerRoot "config.cmd"
if (-not (Test-Path -LiteralPath $configCmdPath)) {
    Expand-Archive -LiteralPath $runnerZipPath -DestinationPath $RunnerRoot -Force
}

if ($ConfigureService -and $ReplaceExisting) {
    Remove-ExistingRemoteRunner -RunnerName $RunnerName
    Remove-ExistingLocalRunnerConfiguration -RunnerRoot $RunnerRoot
}

$registrationToken = (gh api -X POST ("repos/{0}/{1}/actions/runners/registration-token" -f $Owner, $Repo) --jq ".token").Trim()
if ([string]::IsNullOrWhiteSpace($registrationToken)) {
    throw "Unable to obtain a self-hosted runner registration token."
}

$configArguments = @(
    "--url", $repoUrl,
    "--token", $registrationToken,
    "--name", $RunnerName,
    "--labels", $Labels,
    "--work", "_work",
    "--unattended"
)

if ($ReplaceExisting) {
    $configArguments += "--replace"
}

if ($ConfigureService) {
    $configArguments += "--runasservice"
}

Push-Location $RunnerRoot
try {
    Invoke-NativeChecked -FilePath ".\config.cmd" -Arguments $configArguments -FailureMessage "Runner configuration failed."

    if ($ConfigureService) {
        $service = Get-Service | Where-Object {
            $_.Name -like "actions.runner.*" -and $_.DisplayName -like "*$RunnerName*"
        } | Select-Object -First 1

        if ($null -eq $service) {
            throw "Runner service was not created."
        }

        Start-Service -Name $service.Name
        Write-Host ("Runner service installed and started: {0}" -f $service.Name)
    }
    else {
        Write-Host "Runner configured. Start it manually with .\\run.cmd"
    }
}
finally {
    Pop-Location
}
