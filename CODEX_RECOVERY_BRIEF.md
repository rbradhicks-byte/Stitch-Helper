# EV CWS Editor / Stitch Helper Recovery Brief

Created: 2026-04-29

## Project

`C:\Cursor\cws_editor` is the EV CWS Editor / Stitch Helper Windows desktop project for ZIP-based `.cws` Composite Wellbore Stitch archives.

The project is C#/.NET with a WPF UI:

- `src\CwsEditor.Core`: archive IO, warp math, depth mapping, rendering, and save pipeline.
- `src\CwsEditor.Wpf`: desktop UI.
- `tests\CwsEditor.Core.Tests`: load/save/render regression tests.

Git remote:

```text
https://github.com/rbradhicks-byte/Stitch-Helper.git
```

## Current Recovery State

The working tree is dirty only for the app icon:

- `src\CwsEditor.Wpf\Assets\StitchHelper.ico`

That likely corresponds to the user's old request to update the EXE icon from `Stitch Logo_Crop.png`.

## Related Conversations And State

- `Add CWS editing tools` - `019dacc7-5520-7931-8645-d00e2903ff52`
- Cursor prompt history file: `C:\Cursor\_recovered_old_pc\extracted_cursor_state\openai_chatgpt_prompt_history.md`
- Recovery index: `C:\Cursor\_recovered_old_pc\RECOVERY_INDEX.md`

The Cursor prompt history captures the original CWS request, scrollbar fixes, unit selector issues, overview behavior, app rename to Stitch Helper, GitHub setup, icon updates, and later planning for a separate twisted-CWS straightening module.

## Recommended Continuation

1. Create a recovery branch.
2. Commit the icon change as a recovery checkpoint.
3. Run `dotnet test .\tests\CwsEditor.Core.Tests\CwsEditor.Core.Tests.csproj -p:NuGetAudit=false`.
4. Run or publish from source using the commands in `README.md`.
5. For the twisted-CWS straightening module, start from the codebase-inspection prompt in the recovered Cursor prompt history rather than bolting it into existing edit tools immediately.

