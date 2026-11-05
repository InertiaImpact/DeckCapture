[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SdkPath,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CompilerDirectory = 'C:\Strawberry\c\bin'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sdkInclude = Join-Path (Resolve-Path -LiteralPath $SdkPath).Path 'Win\include'
$directShowIdl = Join-Path (Resolve-Path -LiteralPath $SdkPath).Path 'Win\DirectShow\include\KsDeckLink.idl'
$build = [IO.Path]::GetFullPath($BuildDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $build) { throw 'BuildDirectory must not exist. Retain failed builds for diagnosis; choose a new directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $sdkInclude 'DeckLinkAPI.idl'))) { throw 'SDK Windows IDL files are missing.' }
$widl = Join-Path $CompilerDirectory 'widl.exe'
$cc = Join-Path $CompilerDirectory 'gcc.exe'
$cxx = Join-Path $CompilerDirectory 'g++.exe'
foreach ($tool in @($widl,$cc,$cxx)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing build tool: $tool" } }
$null = New-Item -ItemType Directory -Path $build
$null = New-Item -ItemType Directory -Path $output -Force
foreach ($name in @('DeviceInventory.cpp','PciInventory.cpp','PciInventory.h','PciAncestry.h','PciAncestryTests.cpp','DirectShowCorrelation.cpp','DirectShowCorrelation.h')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $build }
Copy-Item -LiteralPath $directShowIdl -Destination $build
Get-ChildItem -LiteralPath $sdkInclude -File -Filter '*.idl' | Copy-Item -Destination $build
$main = Join-Path $build 'DeckLinkAPI.idl'
$text = [IO.File]::ReadAllText($main)
$license = [regex]::Match($text, '(?s)/\*\s*-LICENSE-START-.*?-LICENSE-END-\s*\*/').Value
if (-not $license) { throw 'SDK license notice not found; refusing to generate stripped source.' }
# WIDL 8.5 needs bool declared for the deprecated v15.3.1 IDL. C++ retains native bool;
# the generated C header alone receives its one-byte boolean alias. Never modify the SDK.
$import = 'import "unknwn.idl";'
if (-not $text.Contains($import)) { throw 'Unexpected SDK IDL import; review the compatibility patch.' }
$patched = $text.Replace($import, "$import`ncpp_quote(`"#ifndef __cplusplus`")`ntypedef boolean bool;`ncpp_quote(`"#endif`")")
[IO.File]::WriteAllText($main, $patched, [Text.UTF8Encoding]::new($false))
function Invoke-Logged([string]$Executable, [string[]]$ToolArguments, [string]$LogName) {
    & $Executable @ToolArguments *> (Join-Path $build $LogName)
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed ($LASTEXITCODE). See $build\$LogName" }
}
Push-Location -LiteralPath $build
try {
    Invoke-Logged $cxx @('-std=c++17','-O2','-Wall','-Wextra','-Werror','-static','-I.','PciAncestryTests.cpp','-o','PciAncestryTests.exe') 'compile-ancestry-tests.log'
    Invoke-Logged (Join-Path $build 'PciAncestryTests.exe') @() 'ancestry-tests.log'
    Invoke-Logged $widl @('--win64','-h','-H','DeckLinkAPI.h','DeckLinkAPI.idl') 'widl-header.log'
    Invoke-Logged $widl @('--win64','-u','-o','DeckLinkAPI_i.c','DeckLinkAPI.idl') 'widl-iid.log'
    foreach ($name in @('DeckLinkAPI.h','DeckLinkAPI_i.c')) {
        $path = Join-Path $build $name
        [IO.File]::WriteAllText($path, "$license`n" + [IO.File]::ReadAllText($path), [Text.UTF8Encoding]::new($false))
    }
    $directShowLicense = [regex]::Match([IO.File]::ReadAllText($directShowIdl), '(?s)/\*\s*-LICENSE-START-.*?-LICENSE-END-\s*\*/').Value
    if (-not $directShowLicense) { throw 'DirectShow SDK license missing.' }
    Invoke-Logged $widl @('--win64','-h','-H','KsDeckLink.h','KsDeckLink.idl') 'widl-directshow-header.log'
    Invoke-Logged $widl @('--win64','-u','-o','KsDeckLink_i.c','KsDeckLink.idl') 'widl-directshow-iid.log'
    foreach ($name in @('KsDeckLink.h','KsDeckLink_i.c')) {
        $path = Join-Path $build $name
        [IO.File]::WriteAllText($path, "$directShowLicense`n" + [IO.File]::ReadAllText($path), [Text.UTF8Encoding]::new($false))
    }
    Invoke-Logged $cc @('-c','-O2','-I.','DeckLinkAPI_i.c','-o','DeckLinkAPI_i.o') 'compile-iid.log'
    Invoke-Logged $cc @('-c','-O2','-I.','KsDeckLink_i.c','-o','KsDeckLink_i.o') 'compile-directshow-iid.log'
    Invoke-Logged $cxx @('-std=c++17','-O2','-Wall','-Wextra','-Werror','-static','-static-libgcc','-static-libstdc++','-I.',
        'DeviceInventory.cpp','PciInventory.cpp','DirectShowCorrelation.cpp','DeckLinkAPI_i.o','KsDeckLink_i.o','-lole32','-loleaut32','-luuid','-lstrmiids','-lsetupapi','-lcfgmgr32','-o','DeckCapture.DeviceInventory.exe') 'compile.log'
    Copy-Item -LiteralPath (Join-Path $build 'DeckCapture.DeviceInventory.exe') -Destination $output
    [IO.File]::WriteAllText((Join-Path $output 'NOTICE-DeviceInventory.txt'), "DeckCapture read-only DeckLink inventory helper.`nBuilt using public Blackmagic Design SDK Windows COM interfaces. SDK required only to rebuild; installed Desktop Video driver required to enumerate devices. The explicit --pcie-correlate-idle mode binds Blackmagic DirectShow filters to read IDs only; normal discovery never binds filters.`n`n$license`n`n$directShowLicense`n`nStatically linked GCC runtime libraries are covered by GPLv3 with the GCC Runtime Library Exception 3.1 (https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html). Bundle the compiler notices/source support supplied with the media tools distribution.`n")
    $provenance = [ordered]@{ schema=1; builtUtc=[DateTimeOffset]::UtcNow.ToString('o'); sdkPath=(Resolve-Path -LiteralPath $SdkPath).Path
        sdkMainIdlSha256=(Get-FileHash -LiteralPath (Join-Path $sdkInclude 'DeckLinkAPI.idl') -Algorithm SHA256).Hash
        sourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'DeviceInventory.cpp') -Algorithm SHA256).Hash
        pciSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PciInventory.cpp') -Algorithm SHA256).Hash
        pciHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PciInventory.h') -Algorithm SHA256).Hash
        pciAncestryHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PciAncestry.h') -Algorithm SHA256).Hash
        directShowIdlSha256=(Get-FileHash -LiteralPath $directShowIdl -Algorithm SHA256).Hash
        directShowSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'DirectShowCorrelation.cpp') -Algorithm SHA256).Hash
        directShowHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'DirectShowCorrelation.h') -Algorithm SHA256).Hash
        compiler=(& $cxx --version | Select-Object -First 1); widl=(& $widl -V 2>&1 | Out-String).Trim()
        executableSha256=(Get-FileHash -LiteralPath (Join-Path $output 'DeckCapture.DeviceInventory.exe') -Algorithm SHA256).Hash }
    $provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'device-inventory-provenance.json') -Encoding utf8
} finally { Pop-Location }
Write-Output "Built inventory helper. Generated licensed source and logs retained at $build"
