[CmdletBinding()]
param(
    [switch] $RunTests,
    [switch] $TestsOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$frameworkRoots = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
)
$framework = $frameworkRoots | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'csc.exe') } | Select-Object -First 1
if (-not $framework) {
    throw 'The inbox .NET Framework compiler (csc.exe) was not found.'
}

$csc = Join-Path $framework 'csc.exe'
$artifacts = Join-Path $root 'artifacts'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$references = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:Microsoft.VisualBasic.dll'
)

$coreSource = Join-Path $root 'src\ZipFlow.Core.cs'
$programSource = Join-Path $root 'src\ZipFlow.Program.cs'
$setupSource = Join-Path $root 'src\ZipFlow.Setup.cs'
$testSource = Join-Path $root 'tests\ZipFlow.Tests.cs'
$testExe = Join-Path $artifacts 'ZipFlow.Tests.exe'
$testSources = @($coreSource, $setupSource, $testSource)
if (Test-Path -LiteralPath $programSource) {
    $testSources += $programSource
}

$testArguments = @(
    '/nologo',
    '/target:exe',
    '/main:ZipFlow.Tests',
    '/langversion:5',
    '/warn:4',
    '/warnaserror+',
    ('/out:' + $testExe)
) + $references + $testSources

& $csc $testArguments
if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE."
}

if ($RunTests) {
    & $testExe
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE."
    }
}

if ($TestsOnly) {
    return
}

$appExe = Join-Path $dist 'ZipFlow.exe'
$appArguments = @(
    '/nologo',
    '/target:winexe',
    '/main:ZipFlow.Program',
    '/langversion:5',
    '/warn:4',
    '/warnaserror+',
    ('/out:' + $appExe)
) + $references + @($coreSource, $setupSource, $programSource)

& $csc $appArguments
if ($LASTEXITCODE -ne 0) {
    throw "Application compilation failed with exit code $LASTEXITCODE."
}
