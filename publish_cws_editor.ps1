param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot "src\CwsEditor.Wpf\CwsEditor.Wpf.csproj"
$output = Join-Path $repoRoot "dist\win-x64"

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
    -p:NuGetAudit=false
