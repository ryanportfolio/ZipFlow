$registrationScript = Join-Path $PSScriptRoot '..\installer\ZipFlow.Registration.ps1'
$installScript = Join-Path $PSScriptRoot '..\Install.ps1'
$uninstallScript = Join-Path $PSScriptRoot '..\Uninstall.ps1'

function New-ZipFlowRecordingRegistryBackend {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.ArrayList] $Recorded
    )

    [pscustomobject]@{
        CreateKey = {
            param($subKeyPath)
            $null = $Recorded.Add("Create|$subKeyPath")
            return [pscustomobject]@{ Path = $subKeyPath }
        }.GetNewClosure()
        OpenKey = {
            param($subKeyPath)
            $null = $Recorded.Add("Open|$subKeyPath")
            return [pscustomobject]@{ Path = $subKeyPath }
        }.GetNewClosure()
        SetValue = {
            param($key, $name, $value, $kind)
            $null = $Recorded.Add(('Set|{0}|{1}|{2}' -f $key.Path, $name, $kind))
        }.GetNewClosure()
        ValueExists = { param($key, $name) return $true }
        DeleteValue = {
            param($key, $name)
            $null = $Recorded.Add(('DeleteValue|{0}|{1}' -f $key.Path, $name))
        }.GetNewClosure()
        SubKeyExists = { param($key, $name) return $true }
        DeleteTree = {
            param($key, $name)
            $null = $Recorded.Add(('DeleteTree|{0}\{1}' -f $key.Path, $name))
        }.GetNewClosure()
        DisposeKey = { param($key) }
    }
}

Describe 'ZipFlow per-user registration plan' {
    It 'loads the registration planner' {
        (Test-Path -LiteralPath $registrationScript -PathType Leaf) | Should Be $true
    }

    It 'creates the required candidate registration records' {
        . $registrationScript

        $plan = @(Get-ZipFlowRegistrationPlan -ExecutablePath 'C:\Safe Path\ZipFlow.exe')

        $plan.Count | Should Be 9
        @($plan | Where-Object { $_.PSTypeNames -contains 'ZipFlow.Registration.SetValue' }).Count | Should Be 9
        @($plan | Where-Object { $_.Operation -ne 'SetValue' }).Count | Should Be 0

        $expectedTypes = @(
            'HKCU:\Software\Classes\ZipFlow.Archive||String',
            'HKCU:\Software\Classes\ZipFlow.Archive\shell\open\command||String',
            'HKCU:\Software\ZipFlow\Capabilities|ApplicationName|String',
            'HKCU:\Software\ZipFlow\Capabilities|ApplicationDescription|String',
            'HKCU:\Software\ZipFlow\Capabilities\FileAssociations|.zip|String',
            'HKCU:\Software\RegisteredApplications|ZipFlow|String',
            'HKCU:\Software\Classes\.zip\OpenWithProgids|ZipFlow.Archive|None',
            'HKCU:\Software\Classes\Applications\ZipFlow.exe\SupportedTypes|.zip|None',
            'HKCU:\Software\Classes\Applications\ZipFlow.exe\shell\open\command||String'
        )
        foreach ($operation in $plan) {
            $typeSignature = '{0}|{1}|{2}' -f $operation.Path, $operation.Name, $operation.Type
            ($expectedTypes -contains $typeSignature) | Should Be $true
        }

        $applicationName = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\ZipFlow\Capabilities' -and $_.Name -eq 'ApplicationName'
        }
        $applicationName.Value | Should Be 'ZipFlow'
        $applicationName.Type | Should Be 'String'

        $applicationDescription = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\ZipFlow\Capabilities' -and $_.Name -eq 'ApplicationDescription'
        }
        [string]::IsNullOrWhiteSpace([string] $applicationDescription.Value) | Should Be $false
        $applicationDescription.Type | Should Be 'String'

        $progidCommand = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\Classes\ZipFlow.Archive\shell\open\command' -and $_.Name -eq ''
        }
        $progidCommand.Value | Should Be '"C:\Safe Path\ZipFlow.exe" "%1"'

        $fileAssociation = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\ZipFlow\Capabilities\FileAssociations' -and $_.Name -eq '.zip'
        }
        $fileAssociation.Value | Should Be 'ZipFlow.Archive'

        $registeredApplication = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\RegisteredApplications' -and $_.Name -eq 'ZipFlow'
        }
        $registeredApplication.Value | Should Be 'Software\ZipFlow\Capabilities'

        @($plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\Classes\.zip\OpenWithProgids' -and $_.Name -eq 'ZipFlow.Archive'
        }).Count | Should Be 1

        @($plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\Classes\Applications\ZipFlow.exe\SupportedTypes' -and $_.Name -eq '.zip'
        }).Count | Should Be 1

        $applicationCommand = $plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\Classes\Applications\ZipFlow.exe\shell\open\command' -and $_.Name -eq ''
        }
        $applicationCommand.Value | Should Be '"C:\Safe Path\ZipFlow.exe" "%1"'
    }

    It 'never plans UserChoice access or replacement of the .zip default value' {
        . $registrationScript

        $plan = @(Get-ZipFlowRegistrationPlan -ExecutablePath 'C:\Safe Path\ZipFlow.exe')
        $allText = $plan | ForEach-Object { '{0}|{1}|{2}' -f $_.Path, $_.Name, $_.Value }

        ($allText -join "`n") | Should Not Match '(?i)UserChoice'
        @($plan | Where-Object {
            $_.Path -eq 'HKCU:\Software\Classes\.zip' -and $_.Name -eq ''
        }).Count | Should Be 0
    }

    It 'keeps every planned operation inside the owned per-user registry surface' {
        . $registrationScript

        $plan = @(Get-ZipFlowRegistrationPlan -ExecutablePath 'C:\Safe Path\ZipFlow.exe')
        $allowedPaths = @(
            '^HKCU:\\Software\\Classes\\ZipFlow\.Archive(?:\\|$)',
            '^HKCU:\\Software\\Classes\\\.zip\\OpenWithProgids$',
            '^HKCU:\\Software\\Classes\\Applications\\ZipFlow\.exe(?:\\|$)',
            '^HKCU:\\Software\\ZipFlow(?:\\|$)',
            '^HKCU:\\Software\\RegisteredApplications$'
        )

        foreach ($operation in $plan) {
            @($allowedPaths | Where-Object { $operation.Path -match $_ }).Count | Should BeGreaterThan 0
            $operation.Path | Should Match '^HKCU:'
        }
    }

    It 'plans removal of only ZipFlow-owned keys and named values' {
        . $registrationScript

        $plan = @(Get-ZipFlowUnregistrationPlan)
        $removeValues = @($plan | Where-Object { $_.Operation -eq 'RemoveValue' })
        $allowedOperations = @(
            'RemoveValue|HKCU:\Software\RegisteredApplications|ZipFlow',
            'RemoveValue|HKCU:\Software\Classes\.zip\OpenWithProgids|ZipFlow.Archive',
            'RemoveKey|HKCU:\Software\Classes\ZipFlow.Archive|',
            'RemoveKey|HKCU:\Software\Classes\Applications\ZipFlow.exe|',
            'RemoveKey|HKCU:\Software\ZipFlow|'
        )

        $plan.Count | Should Be 5
        $removeValues.Count | Should Be 2
        foreach ($operation in $plan) {
            $signature = '{0}|{1}|{2}' -f $operation.Operation, $operation.Path, $operation.Name
            ($allowedOperations -contains $signature) | Should Be $true
            $operation.Type | Should Be ''
        }

        @($removeValues | Where-Object {
            $_.Operation -eq 'RemoveValue' -and
            $_.Path -eq 'HKCU:\Software\RegisteredApplications' -and
            $_.Name -eq 'ZipFlow'
        }).Count | Should Be 1
        @($removeValues | Where-Object {
            $_.Operation -eq 'RemoveValue' -and
            $_.Path -eq 'HKCU:\Software\Classes\.zip\OpenWithProgids' -and
            $_.Name -eq 'ZipFlow.Archive'
        }).Count | Should Be 1

        $removedKeys = @($plan | Where-Object { $_.Operation -eq 'RemoveKey' } | Select-Object -ExpandProperty Path)
        $removedKeys.Count | Should Be 3
        ($removedKeys -contains 'HKCU:\Software\Classes\ZipFlow.Archive') | Should Be $true
        ($removedKeys -contains 'HKCU:\Software\Classes\Applications\ZipFlow.exe') | Should Be $true
        ($removedKeys -contains 'HKCU:\Software\ZipFlow') | Should Be $true

        $allText = $plan | ForEach-Object { '{0}|{1}' -f $_.Path, $_.Name }
        ($allText -join "`n") | Should Not Match '(?i)UserChoice'
        @($plan | Where-Object { $_.Path -eq 'HKCU:\Software\Classes\.zip' }).Count | Should Be 0
    }

    It 'derives exact create paths, value names, and registry types through the native adapter' {
        . $registrationScript

        $recorded = New-Object System.Collections.ArrayList
        $notification = [pscustomobject]@{ Count = 0 }
        $backend = New-ZipFlowRecordingRegistryBackend -Recorded $recorded
        $notify = { $notification.Count++ }.GetNewClosure()

        Set-ZipFlowRegistration -ExecutablePath 'C:\Safe Path\ZipFlow.exe' -Backend $backend -Notify $notify

        $expected = @(
            'Create|Software\Classes\ZipFlow.Archive',
            'Set|Software\Classes\ZipFlow.Archive||String',
            'Create|Software\Classes\ZipFlow.Archive\shell\open\command',
            'Set|Software\Classes\ZipFlow.Archive\shell\open\command||String',
            'Create|Software\ZipFlow\Capabilities',
            'Set|Software\ZipFlow\Capabilities|ApplicationName|String',
            'Create|Software\ZipFlow\Capabilities',
            'Set|Software\ZipFlow\Capabilities|ApplicationDescription|String',
            'Create|Software\ZipFlow\Capabilities\FileAssociations',
            'Set|Software\ZipFlow\Capabilities\FileAssociations|.zip|String',
            'Create|Software\RegisteredApplications',
            'Set|Software\RegisteredApplications|ZipFlow|String',
            'Create|Software\Classes\.zip\OpenWithProgids',
            'Set|Software\Classes\.zip\OpenWithProgids|ZipFlow.Archive|None',
            'Create|Software\Classes\Applications\ZipFlow.exe\SupportedTypes',
            'Set|Software\Classes\Applications\ZipFlow.exe\SupportedTypes|.zip|None',
            'Create|Software\Classes\Applications\ZipFlow.exe\shell\open\command',
            'Set|Software\Classes\Applications\ZipFlow.exe\shell\open\command||String'
        )
        ($recorded -join "`n") | Should Be ($expected -join "`n")
        $notification.Count | Should Be 1
    }

    It 'derives exact open, delete-value, and owned delete-tree targets through the native adapter' {
        . $registrationScript

        $recorded = New-Object System.Collections.ArrayList
        $notification = [pscustomobject]@{ Count = 0 }
        $backend = New-ZipFlowRecordingRegistryBackend -Recorded $recorded
        $notify = { $notification.Count++ }.GetNewClosure()

        Remove-ZipFlowRegistration -Backend $backend -Notify $notify

        $expected = @(
            'Open|Software\RegisteredApplications',
            'DeleteValue|Software\RegisteredApplications|ZipFlow',
            'Open|Software\Classes\.zip\OpenWithProgids',
            'DeleteValue|Software\Classes\.zip\OpenWithProgids|ZipFlow.Archive',
            'Open|Software\Classes',
            'DeleteTree|Software\Classes\ZipFlow.Archive',
            'Open|Software\Classes\Applications',
            'DeleteTree|Software\Classes\Applications\ZipFlow.exe',
            'Open|Software',
            'DeleteTree|Software\ZipFlow'
        )
        ($recorded -join "`n") | Should Be ($expected -join "`n")

        $deleteTrees = @($recorded | Where-Object { $_ -like 'DeleteTree|*' })
        $allowedDeleteTrees = @(
            'DeleteTree|Software\Classes\ZipFlow.Archive',
            'DeleteTree|Software\Classes\Applications\ZipFlow.exe',
            'DeleteTree|Software\ZipFlow'
        )
        $deleteTrees.Count | Should Be 3
        foreach ($target in $deleteTrees) {
            ($allowedDeleteTrees -contains $target) | Should Be $true
        }
        $notification.Count | Should Be 1
    }
}

Describe 'ZipFlow install failure transaction' {
    It 'cleans a failed fresh installation in safe order and rethrows the registration error' {
        $scriptText = Get-Content -Raw -LiteralPath $installScript
        $scriptText | Should Match 'function\s+Invoke-ZipFlowInstallTransaction'
        . $installScript

        $calls = New-Object System.Collections.ArrayList
        $failureMessage = ''
        try {
            Invoke-ZipFlowInstallTransaction -HadExistingExecutable $false `
                -BackupExisting { $null = $calls.Add('backup') }.GetNewClosure() `
                -CopyExecutable { $null = $calls.Add('copy') }.GetNewClosure() `
                -RegisterExecutable { $null = $calls.Add('register'); throw 'registration failed' }.GetNewClosure() `
                -UnregisterPartial { $null = $calls.Add('unregister') }.GetNewClosure() `
                -RemoveCopiedExecutable { $null = $calls.Add('remove-new') }.GetNewClosure() `
                -RestoreBackup { $null = $calls.Add('restore') }.GetNewClosure() `
                -RemoveEmptyInstallDirectory { $null = $calls.Add('remove-empty') }.GetNewClosure() `
                -RemoveBackup { $null = $calls.Add('remove-backup') }.GetNewClosure()
        }
        catch {
            $failureMessage = $_.Exception.Message
        }

        $failureMessage | Should Be 'registration failed'
        ($calls -join ',') | Should Be 'copy,register,unregister,remove-new,remove-empty'
    }

    It 'restores a pre-existing executable after failed reinstallation' {
        $scriptText = Get-Content -Raw -LiteralPath $installScript
        $scriptText | Should Match 'function\s+Invoke-ZipFlowInstallTransaction'
        . $installScript

        $calls = New-Object System.Collections.ArrayList
        $previousRegistration = [pscustomobject]@{ Preserved = $true }
        $failureMessage = ''
        try {
            Invoke-ZipFlowInstallTransaction -HadExistingExecutable $true `
                -BackupExisting { $null = $calls.Add('backup') }.GetNewClosure() `
                -CopyExecutable { $null = $calls.Add('copy') }.GetNewClosure() `
                -RegisterExecutable { $null = $calls.Add('register'); throw 'registration failed' }.GetNewClosure() `
                -UnregisterPartial {
                    $null = $calls.Add('unregister')
                    $previousRegistration.Preserved = $false
                }.GetNewClosure() `
                -RemoveCopiedExecutable { $null = $calls.Add('remove-new') }.GetNewClosure() `
                -RestoreBackup { $null = $calls.Add('restore') }.GetNewClosure() `
                -RemoveEmptyInstallDirectory { $null = $calls.Add('remove-empty') }.GetNewClosure() `
                -RemoveBackup { $null = $calls.Add('remove-backup') }.GetNewClosure()
        }
        catch {
            $failureMessage = $_.Exception.Message
        }

        $failureMessage | Should Be 'registration failed'
        ($calls -join ',') | Should Be 'backup,copy,register,restore'
        $previousRegistration.Preserved | Should Be $true
    }
}

Describe 'ZipFlow uninstall filesystem scope' {
    It 'fails on missing LOCALAPPDATA before unregistering or reporting removal' {
        $scriptText = Get-Content -Raw -LiteralPath $uninstallScript
        $guardIndex = $scriptText.IndexOf('if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA))')
        $unregisterIndex = $scriptText.IndexOf('Remove-ZipFlowRegistration')
        $successMessageIndex = $scriptText.IndexOf("Write-Host 'ZipFlow was unregistered")

        $guardIndex | Should BeGreaterThan -1
        ($guardIndex -lt $unregisterIndex) | Should Be $true
        ($guardIndex -lt $successMessageIndex) | Should Be $true
        $scriptText | Should Match "throw\s+'LOCALAPPDATA is not available for this user\.'"
    }

    It 'removes only ZipFlow.exe and deletes the install directory only when empty' {
        $scriptText = Get-Content -Raw -LiteralPath $uninstallScript
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            $uninstallScript,
            [ref] $tokens,
            [ref] $parseErrors)
        $removeCommands = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Remove-Item'
        }, $true))

        $parseErrors.Count | Should Be 0
        $removeCommands.Count | Should Be 2
        @($removeCommands | Where-Object { $_.Extent.Text -match '(?i)-Recurse(?:\s|$)' }).Count | Should Be 0
        @($removeCommands | Where-Object { $_.Extent.Text -match '(?i)-LiteralPath\s+\$installedExecutable(?:\s|$)' }).Count | Should Be 1
        @($removeCommands | Where-Object { $_.Extent.Text -match '(?i)-LiteralPath\s+\$installDirectory(?:\s|$)' }).Count | Should Be 1
        $scriptText | Should Match "Join-Path\s+\`$installDirectory\s+'ZipFlow\.exe'"
        $scriptText | Should Match '@\(Get-ChildItem\s+-LiteralPath\s+\$installDirectory\s+-Force\)\.Count\s+-eq\s+0'
    }
}
