param(
    [switch]$Clean,
    [switch]$Beta,
    [switch]$Beta2,
    [switch]$Beta3
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot "src\CwsEditor.Wpf\CwsEditor.Wpf.csproj"
if ((@($Beta, $Beta2, $Beta3) | Where-Object { $_.IsPresent }).Count -gt 1) {
    throw "Use only one beta switch."
}

$output = if ($Beta3) {
    Join-Path $repoRoot "dist\win-x64-beta3"
} elseif ($Beta2) {
    Join-Path $repoRoot "dist\win-x64-beta2"
} elseif ($Beta) {
    Join-Path $repoRoot "dist\win-x64-beta"
} else {
    Join-Path $repoRoot "dist\win-x64"
}

if ($Clean) {
    Remove-Item -LiteralPath $output -Force -Recurse -ErrorAction SilentlyContinue
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    -o $output `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:NuGetAudit=false `
    "-p:BetaBuild=$($Beta.IsPresent.ToString().ToLowerInvariant())" `
    "-p:Beta2Build=$($Beta2.IsPresent.ToString().ToLowerInvariant())" `
    "-p:Beta3Build=$($Beta3.IsPresent.ToString().ToLowerInvariant())"
