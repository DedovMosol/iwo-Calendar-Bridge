param(
    [string]$Configuration = 'Release',
    [string]$OutDir = 'artifacts'
)

# Builds the two distributions of the desktop app: x86 (32-bit Windows, and 64-bit as well) and x64.
# Each one is a portable folder (install = copy) plus a zip of it:
#   artifacts\iwo-Calendar-Bridge-x86\   artifacts\iwo-Calendar-Bridge-x86.zip
#   artifacts\iwo-Calendar-Bridge-x64\   artifacts\iwo-Calendar-Bridge-x64.zip
# The folder contains the program, config\connection.example.json and the docs (README, LICENSE, PRIVACY).
# Target runtime: .NET Framework 4.8 (Windows 7 SP1 and newer). ASCII-only on purpose: Windows PowerShell 5.1
# reads BOM-less UTF-8 as ANSI.
# Usage: powershell -ExecutionPolicy Bypass -File build\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = [System.IO.Path]::Combine($root, 'src', 'IwoBridge.Wpf', 'IwoBridge.Wpf.csproj')
$artifacts = [System.IO.Path]::Combine($root, $OutDir)
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

& dotnet restore $project --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

foreach ($arch in @('x86', 'x64')) {
    $name = 'iwo-Calendar-Bridge-' + $arch
    $target = [System.IO.Path]::Combine($artifacts, $name)
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }

    # PlatformTarget sets the bitness of the exe; libraries stay AnyCPU and run in either process.
    # Separate output and intermediate folders per bitness: an incremental build does not notice a changed
    # PlatformTarget and could otherwise ship an exe of the wrong bitness.
    & dotnet publish $project --configuration $Configuration --no-restore --nologo `
        "-p:PlatformTarget=$arch" "-p:IntermediateOutputPath=obj\$Configuration\$arch\" "-p:OutputPath=bin\$Configuration\$arch\" `
        -p:ContinuousIntegrationBuild=true --output $target
    if ($LASTEXITCODE -ne 0) { throw "publish $arch failed" }

    # Check the actual bitness written into the exe header.
    $exe = [System.IO.Path]::Combine($target, 'IwoCalendarBridge.exe')
    $expected = if ($arch -eq 'x86') { 'X86' } else { 'Amd64' }
    $actual = [System.Reflection.AssemblyName]::GetAssemblyName($exe).ProcessorArchitecture.ToString()
    if ($actual -ne $expected) { throw "exe bitness is $actual, expected $expected" }

    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::Combine($target, 'config')) | Out-Null
    Copy-Item -LiteralPath ([System.IO.Path]::Combine($root, 'config', 'connection.example.json')) `
        -Destination ([System.IO.Path]::Combine($target, 'config'))
    foreach ($doc in @('README.md', 'LICENSE')) {
        Copy-Item -LiteralPath ([System.IO.Path]::Combine($root, $doc)) -Destination $target
    }
    Copy-Item -LiteralPath ([System.IO.Path]::Combine($root, 'docs', 'PRIVACY.md')) -Destination $target

    # Debug symbols and XML docs are not part of the distribution. Files only, filtered by extension
    # (-Include does not work together with -LiteralPath and would match everything).
    Get-ChildItem -LiteralPath $target -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' -or $_.Extension -eq '.xml' } |
        Remove-Item -Force

    $zip = $target + '.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($target, $zip)
    "built $name"
}
