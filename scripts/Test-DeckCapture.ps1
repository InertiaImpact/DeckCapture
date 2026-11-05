[CmdletBinding()]
param([switch]$IncludeMedia)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 18 or later is required for the browser module tests.' }
$previous = $env:DECKCAPTURE_CAPTURE_INTEGRATION
try {
    if ($IncludeMedia) { $env:DECKCAPTURE_CAPTURE_INTEGRATION = '1' }
    & dotnet test (Join-Path $repository 'DeckCapture.sln') -m:1 -nr:false --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $browserTests = @(Get-ChildItem (Join-Path $repository 'tests') -Filter '*.test.mjs' -File | ForEach-Object FullName)
    & node --test @browserTests
    if ($LASTEXITCODE -ne 0) { throw 'Browser module tests failed.' }
} finally { $env:DECKCAPTURE_CAPTURE_INTEGRATION = $previous }
