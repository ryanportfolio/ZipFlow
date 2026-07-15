[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Invoke-ZipFlowInstallTransaction {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $HadExistingExecutable,

        [Parameter(Mandatory = $true)]
        [scriptblock] $BackupExisting,

        [Parameter(Mandatory = $true)]
        [scriptblock] $CopyExecutable,

        [Parameter(Mandatory = $true)]
        [scriptblock] $RegisterExecutable,

        [Parameter(Mandatory = $true)]
        [scriptblock] $UnregisterPartial,

        [Parameter(Mandatory = $true)]
        [scriptblock] $RemoveCopiedExecutable,

        [Parameter(Mandatory = $true)]
        [scriptblock] $RestoreBackup,

        [Parameter(Mandatory = $true)]
        [scriptblock] $RemoveEmptyInstallDirectory,

        [Parameter(Mandatory = $true)]
        [scriptblock] $RemoveBackup
    )

    $backupCreated = $false
    $copyAttempted = $false
    $registrationAttempted = $false

    try {
        if ($HadExistingExecutable) {
            & $BackupExisting
            $backupCreated = $true
        }

        $copyAttempted = $true
        & $CopyExecutable

        $registrationAttempted = $true
        & $RegisterExecutable

        if ($backupCreated) {
            & $RemoveBackup
        }
    }
    catch {
        $installError = $_

        if ($registrationAttempted -and -not $HadExistingExecutable) {
            try {
                & $UnregisterPartial
            }
            catch {
                # Cleanup is best effort; preserve the original installation error.
            }
        }

        if ($backupCreated) {
            try {
                & $RestoreBackup
            }
            catch {
                # Leave the backup in place if restoration itself fails.
            }
        }
        elseif (-not $HadExistingExecutable -and $copyAttempted) {
            try {
                & $RemoveCopiedExecutable
            }
            catch {
                # Continue to the empty-directory check.
            }

            try {
                & $RemoveEmptyInstallDirectory
            }
            catch {
                # Cleanup is best effort; preserve the original installation error.
            }
        }

        throw $installError
    }
}

function Install-ZipFlow {
    $projectRoot = $PSScriptRoot

    $buildScript = Join-Path $projectRoot 'build.ps1'
    $sourceExecutable = Join-Path $projectRoot 'dist\ZipFlow.exe'
    $registrationScript = Join-Path $projectRoot 'installer\ZipFlow.Registration.ps1'

    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
            throw "ZipFlow.exe is missing and build.ps1 was not found: $buildScript"
        }

        Write-Host 'dist\ZipFlow.exe is missing. Building ZipFlow...'
        Push-Location $projectRoot
        try {
            & $buildScript
        }
        finally {
            Pop-Location
        }
    }

    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
        throw "Build completed without producing: $sourceExecutable"
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA is not available for this user.'
    }

    if (-not (Test-Path -LiteralPath $registrationScript -PathType Leaf)) {
        throw "Registration helper was not found: $registrationScript"
    }

    $installDirectory = Join-Path $env:LOCALAPPDATA 'ZipFlow'
    $installedExecutable = Join-Path $installDirectory 'ZipFlow.exe'
    $backupExecutable = Join-Path $installDirectory ('ZipFlow.exe.install-backup.{0}' -f [guid]::NewGuid().ToString('N'))

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    $hadExistingExecutable = Test-Path -LiteralPath $installedExecutable -PathType Leaf

    . $registrationScript

    Invoke-ZipFlowInstallTransaction `
        -HadExistingExecutable $hadExistingExecutable `
        -BackupExisting ({ Copy-Item -LiteralPath $installedExecutable -Destination $backupExecutable -Force }.GetNewClosure()) `
        -CopyExecutable ({ Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable -Force }.GetNewClosure()) `
        -RegisterExecutable ({ Set-ZipFlowRegistration -ExecutablePath $installedExecutable }.GetNewClosure()) `
        -UnregisterPartial ({ Remove-ZipFlowRegistration }.GetNewClosure()) `
        -RemoveCopiedExecutable ({
            if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
                Remove-Item -LiteralPath $installedExecutable -Force
            }
        }.GetNewClosure()) `
        -RestoreBackup ({
            if (Test-Path -LiteralPath $backupExecutable -PathType Leaf) {
                Copy-Item -LiteralPath $backupExecutable -Destination $installedExecutable -Force
                Remove-Item -LiteralPath $backupExecutable -Force
            }
        }.GetNewClosure()) `
        -RemoveEmptyInstallDirectory ({
            if ((Test-Path -LiteralPath $installDirectory -PathType Container) -and
                @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $installDirectory -Force
            }
        }.GetNewClosure()) `
        -RemoveBackup ({
            if (Test-Path -LiteralPath $backupExecutable -PathType Leaf) {
                Remove-Item -LiteralPath $backupExecutable -Force
            }
        }.GetNewClosure())

    Write-Host ''
    Write-Host 'ZipFlow is installed and available as an app for .zip files.'
    Write-Host 'Windows still controls the default. Complete this one-time choice:'
    Write-Host '  1. In Default Apps, choose defaults by file type.'
    Write-Host '  2. Find .zip and select ZipFlow.'
    Write-Host 'ZipFlow does not force or claim to be the default.'

    try {
        Start-Process 'ms-settings:defaultapps'
    }
    catch {
        Write-Warning 'Default Apps could not be opened automatically. Open Windows Settings > Apps > Default Apps to finish the one-time choice.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Install-ZipFlow
}
