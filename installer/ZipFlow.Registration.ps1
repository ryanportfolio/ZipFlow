function New-ZipFlowRegistrationRecord {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('SetValue', 'RemoveValue', 'RemoveKey')]
        [string] $Operation,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [AllowEmptyString()]
        [string] $Name = '',

        [AllowNull()]
        [object] $Value,

        [AllowEmptyString()]
        [string] $Type = ''
    )

    [pscustomobject]@{
        PSTypeName = "ZipFlow.Registration.$Operation"
        Operation  = $Operation
        Path       = $Path
        Name       = $Name
        Value      = $Value
        Type       = $Type
    }
}

function Get-ZipFlowRegistrationPlan {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $ExecutablePath
    )

    $command = '"{0}" "%1"' -f $ExecutablePath

    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\Classes\ZipFlow.Archive' -Value 'ZipFlow ZIP Archive' -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\Classes\ZipFlow.Archive\shell\open\command' -Value $command -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\ZipFlow\Capabilities' -Name 'ApplicationName' -Value 'ZipFlow' -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\ZipFlow\Capabilities' -Name 'ApplicationDescription' -Value 'Safely extracts ZIP archives to the Desktop.' -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\ZipFlow\Capabilities\FileAssociations' -Name '.zip' -Value 'ZipFlow.Archive' -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\RegisteredApplications' -Name 'ZipFlow' -Value 'Software\ZipFlow\Capabilities' -Type String
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\Classes\.zip\OpenWithProgids' -Name 'ZipFlow.Archive' -Value ([byte[]]::new(0)) -Type None
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\Classes\Applications\ZipFlow.exe\SupportedTypes' -Name '.zip' -Value ([byte[]]::new(0)) -Type None
    New-ZipFlowRegistrationRecord -Operation SetValue -Path 'HKCU:\Software\Classes\Applications\ZipFlow.exe\shell\open\command' -Value $command -Type String
}

function Get-ZipFlowUnregistrationPlan {
    New-ZipFlowRegistrationRecord -Operation RemoveValue -Path 'HKCU:\Software\RegisteredApplications' -Name 'ZipFlow'
    New-ZipFlowRegistrationRecord -Operation RemoveValue -Path 'HKCU:\Software\Classes\.zip\OpenWithProgids' -Name 'ZipFlow.Archive'
    New-ZipFlowRegistrationRecord -Operation RemoveKey -Path 'HKCU:\Software\Classes\ZipFlow.Archive'
    New-ZipFlowRegistrationRecord -Operation RemoveKey -Path 'HKCU:\Software\Classes\Applications\ZipFlow.exe'
    New-ZipFlowRegistrationRecord -Operation RemoveKey -Path 'HKCU:\Software\ZipFlow'
}

function ConvertTo-ZipFlowCurrentUserSubKeyPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $prefix = 'HKCU:\'
    if (-not $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ZipFlow registration is limited to HKCU paths: $Path"
    }

    $Path.Substring($prefix.Length)
}

function Invoke-ZipFlowAssociationChanged {
    if (-not ('ZipFlow.NativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ZipFlow
{
    internal static class NativeMethods
    {
        [DllImport("shell32.dll")]
        internal static extern void SHChangeNotify(
            uint eventId,
            uint flags,
            IntPtr item1,
            IntPtr item2);
    }
}
'@
    }

    [ZipFlow.NativeMethods]::SHChangeNotify(
        0x08000000,
        0,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
}

function New-ZipFlowRegistryBackend {
    [pscustomobject]@{
        CreateKey = {
            param($subKeyPath)
            [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKeyPath)
        }
        OpenKey = {
            param($subKeyPath)
            [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKeyPath, $true)
        }
        SetValue = {
            param($key, $name, $value, $kind)
            $key.SetValue($name, $value, $kind)
        }
        ValueExists = {
            param($key, $name)
            return $key.GetValueNames() -contains $name
        }
        DeleteValue = {
            param($key, $name)
            $key.DeleteValue($name, $false)
        }
        SubKeyExists = {
            param($key, $name)
            return $key.GetSubKeyNames() -contains $name
        }
        DeleteTree = {
            param($key, $name)
            $key.DeleteSubKeyTree($name, $false)
        }
        DisposeKey = {
            param($key)
            $key.Dispose()
        }
    }
}

function Invoke-ZipFlowRegistryOperation {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Operation,

        [Parameter(Mandatory = $true)]
        [object] $Backend
    )

    $subKeyPath = ConvertTo-ZipFlowCurrentUserSubKeyPath -Path $Operation.Path
    if ($Operation.Operation -eq 'SetValue') {
        $createKey = $Backend.CreateKey
        $setValue = $Backend.SetValue
        $disposeKey = $Backend.DisposeKey
        $key = & $createKey $subKeyPath
        if ($null -eq $key) {
            throw "Unable to create or open HKCU registry key: $subKeyPath"
        }

        try {
            $kind = [Microsoft.Win32.RegistryValueKind]::$($Operation.Type)
            & $setValue $key $Operation.Name $Operation.Value $kind
            return $true
        }
        finally {
            & $disposeKey $key
        }
    }

    if ($Operation.Operation -eq 'RemoveValue') {
        $openKey = $Backend.OpenKey
        $valueExists = $Backend.ValueExists
        $deleteValue = $Backend.DeleteValue
        $disposeKey = $Backend.DisposeKey
        $key = & $openKey $subKeyPath
        if ($null -eq $key) {
            return $false
        }

        try {
            if (& $valueExists $key $Operation.Name) {
                & $deleteValue $key $Operation.Name
                return $true
            }
            return $false
        }
        finally {
            & $disposeKey $key
        }
    }

    if ($Operation.Operation -eq 'RemoveKey') {
        $separator = $subKeyPath.LastIndexOf('\')
        $parentPath = $subKeyPath.Substring(0, $separator)
        $leafName = $subKeyPath.Substring($separator + 1)
        $openKey = $Backend.OpenKey
        $subKeyExists = $Backend.SubKeyExists
        $deleteTree = $Backend.DeleteTree
        $disposeKey = $Backend.DisposeKey
        $parentKey = & $openKey $parentPath
        if ($null -eq $parentKey) {
            return $false
        }

        try {
            if (& $subKeyExists $parentKey $leafName) {
                & $deleteTree $parentKey $leafName
                return $true
            }
            return $false
        }
        finally {
            & $disposeKey $parentKey
        }
    }

    throw "Unsupported ZipFlow registration operation: $($Operation.Operation)"
}

function Invoke-ZipFlowRegistrationOperations {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Operations,

        [Parameter(Mandatory = $true)]
        [object] $Backend,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Notify
    )

    $mutated = $false
    try {
        foreach ($operation in $Operations) {
            if (Invoke-ZipFlowRegistryOperation -Operation $operation -Backend $Backend) {
                $mutated = $true
            }
        }
    }
    finally {
        if ($mutated) {
            & $Notify
        }
    }
}

function Set-ZipFlowRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $ExecutablePath,

        [object] $Backend,

        [scriptblock] $Notify
    )

    if ($null -eq $Backend) {
        $Backend = New-ZipFlowRegistryBackend
    }
    if ($null -eq $Notify) {
        $Notify = { Invoke-ZipFlowAssociationChanged }
    }

    Invoke-ZipFlowRegistrationOperations `
        -Operations @(Get-ZipFlowRegistrationPlan -ExecutablePath $ExecutablePath) `
        -Backend $Backend `
        -Notify $Notify
}

function Remove-ZipFlowRegistration {
    param(
        [object] $Backend,

        [scriptblock] $Notify
    )

    if ($null -eq $Backend) {
        $Backend = New-ZipFlowRegistryBackend
    }
    if ($null -eq $Notify) {
        $Notify = { Invoke-ZipFlowAssociationChanged }
    }

    Invoke-ZipFlowRegistrationOperations `
        -Operations @(Get-ZipFlowUnregistrationPlan) `
        -Backend $Backend `
        -Notify $Notify
}
