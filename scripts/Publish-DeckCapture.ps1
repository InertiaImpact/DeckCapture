[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SelfContained,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\DeckCapture' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Framework-dependent remains the default. -SelfContained includes the selected runtime
# for both the supervisor and its isolated worker; no machine .NET runtime is required.
# MSBuild property lists treat commas/semicolons as separators even inside a shell argument.
$publishProperty = $OutputDirectory.Replace('%', '%25').Replace(',', '%2C').Replace(';', '%3B') + [IO.Path]::DirectorySeparatorChar
$publishArguments = @('-c', 'Release', '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '-m:1', '-nr:false')
if ($SelfContained -or $PSBoundParameters.ContainsKey('Runtime')) { $publishArguments += @('-r', $Runtime) }
if ($SelfContained) { $publishArguments += @('-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false') }
& dotnet publish (Join-Path $repository 'src\Supervisor\DeckCapture.Supervisor.csproj') @publishArguments "-p:PublishDir=$publishProperty"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# Publishing a referenced executable as part of the supervisor's build does not guarantee
# that its full runtime pack was published. Publish the worker explicitly into its final folder.
$workerDirectory = Join-Path $OutputDirectory 'capture-worker'
$workerProperty = $workerDirectory.Replace('%', '%25').Replace(',', '%2C').Replace(';', '%3B') + [IO.Path]::DirectorySeparatorChar
& dotnet publish (Join-Path $repository 'src\CaptureWorker\DeckCapture.CaptureWorker.csproj') @publishArguments "-p:PublishDir=$workerProperty"
if ($LASTEXITCODE -ne 0) { throw 'Capture worker publish failed.' }
# Fingerprint the complete relative ESM graph, from leaf modules to the entry point.
& (Join-Path $PSScriptRoot 'Stamp-WebAssets.ps1') -WebRoot (Join-Path $OutputDirectory 'wwwroot')
foreach ($required in @('DeckCapture.Supervisor.dll', 'wwwroot\index.html', 'capture-worker\DeckCapture.CaptureWorker.dll', 'capture-worker\DeckCapture.CaptureWorker.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Published package is missing $required." }
}
if ($SelfContained -and $Runtime.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    foreach ($required in @('DeckCapture.Supervisor.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'capture-worker\DeckCapture.CaptureWorker.exe', 'capture-worker\coreclr.dll', 'capture-worker\hostfxr.dll', 'capture-worker\hostpolicy.dll', 'capture-worker\System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Self-contained package is missing $required." }
    }
}
Write-Host "DeckCapture published to $OutputDirectory"
