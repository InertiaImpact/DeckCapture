[CmdletBinding()]
param([Parameter(Mandatory)][string]$WebRoot)
$ErrorActionPreference = 'Stop'
$WebRoot = [IO.Path]::GetFullPath($WebRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$boundary = $WebRoot + [IO.Path]::DirectorySeparatorChar
$digests = @{}
$visiting = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
# Controlled application ESM uses literal relative imports (including export-from).
# Visit children first so changing one module invalidates every importing ancestor.
$imports = [regex]'(?<prefix>\b(?:from\s*|import\s*(?:\(\s*)?))(?<quote>[''"])(?<specifier>\.[^''"]+)\k<quote>'
function Stamp-Module([string]$Path) {
    $Path = [IO.Path]::GetFullPath($Path)
    if (-not $Path.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'A browser import escapes wwwroot.' }
    if ($digests.ContainsKey($Path)) { return $digests[$Path] }
    if (-not $visiting.Add($Path)) { throw "Browser module import cycle: $Path" }
    if (-not [IO.File]::Exists($Path) -or [IO.Path]::GetExtension($Path) -ne '.js') { throw "Missing or unsupported browser module: $Path" }
    $scriptText = [IO.File]::ReadAllText($Path)
    $stamped = $imports.Replace($scriptText, [Text.RegularExpressions.MatchEvaluator]{ param($match)
        $specifier = $match.Groups['specifier'].Value
        $relative = ($specifier -split '[?#]', 2)[0]
        if ($relative -notmatch '^\.{1,2}/' -or $relative.Contains('\') -or $relative.Contains('%')) { throw "Unsupported relative browser import: $specifier" }
        $child = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Path)) $relative))
        $digest = Stamp-Module $child
        $quote = $match.Groups['quote'].Value
        return $match.Groups['prefix'].Value + $quote + $relative + '?v=' + $digest + $quote
    })
    if ($stamped -ne $scriptText) { [IO.File]::WriteAllText($Path, $stamped, [Text.UTF8Encoding]::new($false)) }
    $null = $visiting.Remove($Path)
    $digest = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0,16)
    $digests[$Path] = $digest
    return $digest
}
$appDigest = Stamp-Module (Join-Path $WebRoot 'app.js')
$cssDigest = (Get-FileHash -LiteralPath (Join-Path $WebRoot 'styles.css') -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0,16)
$indexPath = Join-Path $WebRoot 'index.html'
$html = [IO.File]::ReadAllText($indexPath)
foreach ($asset in @(@{name='app.js';digest=$appDigest},@{name='styles.css';digest=$cssDigest})) {
    $pattern = '/' + [regex]::Escape($asset.name) + '(?:\?[^"'']*)?'
    $html = [regex]::Replace($html, $pattern, '/' + $asset.name + '?v=' + $asset.digest)
}
[IO.File]::WriteAllText($indexPath,$html,[Text.UTF8Encoding]::new($false))
Write-Host "Fingerprinted $($digests.Count) browser modules and stylesheet."
