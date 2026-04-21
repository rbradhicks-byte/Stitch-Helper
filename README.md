# EV CWS Editor

Windows desktop editor for EV `.cws` files that are ZIP-based Composite Wellbore Stitch archives.

## What it does
- Opens `.cws` files with shared-read access so the source file can still be open in Stitch Viewer.
- Preserves the EV archive shape: `images/*`, `thumbs/*`, `Depth.txt`, `Telemetry.txt`, `Stitch.dat`, and passthrough entries such as `Source.stitchproj2`.
- Recreates the stitched image view with:
  - fast overview thumbnails
  - a virtualized main viewport
  - hover depth/orientation derived from `displayY -> sourceY -> Stitch.dat displacements -> Depth.txt`
- Supports edits for:
  - whole-stitch vertical scale
  - selected-interval vertical scale
  - global tone adjustments
  - selected-interval tone adjustments
- Writes a separate edited `.cws` file and regenerates `images/*`, `thumbs/*`, and `Stitch.dat`.

## Project layout
- `src/CwsEditor.Core`: archive IO, warp math, depth mapping, rendering, and save pipeline
- `src/CwsEditor.Wpf`: Windows WPF UI
- `tests/CwsEditor.Core.Tests`: regression tests for load/save/render behavior

## Run from source
```powershell
dotnet run --project .\src\CwsEditor.Wpf\CwsEditor.Wpf.csproj
```

## Build
```powershell
dotnet build .\CwsEditor.sln -p:NuGetAudit=false
```

## Test
```powershell
dotnet test .\tests\CwsEditor.Core.Tests\CwsEditor.Core.Tests.csproj -p:NuGetAudit=false
```

## Publish a single-file EXE
```powershell
.\publish_cws_editor.ps1
```

Publish output:
- `dist\win-x64\CwsEditor.Wpf.exe`

## Notes
- The editor keeps `Depth.txt`, `Telemetry.txt`, and `Source.stitchproj2` unchanged in v1.
- `Stitch.dat.layout`, `Stitch.dat.displacements`, and `debug.movement` are regenerated on save.
- The UI uses overview dragging and `Shift+drag` in the main viewer for interval selection.
