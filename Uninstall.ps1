[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is not available for this user.'
}

Write-Warning 'Before uninstalling, set the .zip default back to File Explorer or another installed archive app in Windows Default Apps.'

if (-not $Force) {
    $answer = Read-Host 'Uninstall ZipFlow for the current user? Type yes to continue'
    if ($answer -notmatch '^(?i:yes|y)$') {
        Write-Host 'Uninstall cancelled.'
        return
    }
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$registrationScript = Join-Path $projectRoot 'installer\ZipFlow.Registration.ps1'
if (-not (Test-Path -LiteralPath $registrationScript -PathType Leaf)) {
    throw "Registration helper was not found: $registrationScript"
}

. $registrationScript
Remove-ZipFlowRegistration

$installDirectory = Join-Path $env:LOCALAPPDATA 'ZipFlow'
$installedExecutable = Join-Path $installDirectory 'ZipFlow.exe'

if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
    Remove-Item -LiteralPath $installedExecutable -Force
}

if ((Test-Path -LiteralPath $installDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
    Remove-Item -LiteralPath $installDirectory -Force
}

Write-Host 'ZipFlow was unregistered and its per-user executable was removed.'
