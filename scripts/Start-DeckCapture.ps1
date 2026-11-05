[CmdletBinding()]
param(
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'DeckCapture'),
    [string]$Urls = 'http://127.0.0.1:5278',
    [switch]$Published
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$env:DECKCAPTURE_STATE_DIRECTORY = [IO.Path]::GetFullPath($StateDirectory)
if ($Published) {
    $application = Join-Path $repository 'artifacts\DeckCapture\DeckCapture.Supervisor.dll'
    if (-not (Test-Path -LiteralPath $application)) { throw 'Run Publish-DeckCapture.ps1 first.' }
    & dotnet $application --urls $Urls
} else {
    & dotnet run --no-build --project (Join-Path $repository 'src\Supervisor\DeckCapture.Supervisor.csproj') -- --urls $Urls
}
if ($LASTEXITCODE -ne 0) { throw "DeckCapture exited with code $LASTEXITCODE." }
