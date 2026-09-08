# Desktop Shell restore tests

`Test-DeskRewind.ps1` requires PowerShell 7 and an STA process. It compiles the current C# source in memory, uses source fonts directly, and stores manual test layouts under `test-logs/manual-test-layouts.json`. It does not create or overwrite an EXE or alter the production layout file.

Modes: `UI` opens the application; `Inspect` reads live Shell size; `Integration` tests save/detect/confirmation selection/restore against the actual desktop; `UiTest` exercises the actual save dialog chain and renders the confirmation/result dialogs. Integration captures the current desktop to a timestamped JSON file and restores it in `finally`.

New snapshots contain `shellState.version=1`, the live IFolderView2 view mode/icon size, cell spacing, folder flags and Shell item parsing identities/positions. Old registry-derived `iconSize` values and spacing-only snapshots cannot establish the original image size. Old production records remain untouched; save a new test layout before changing icon size.

Snapshots also capture system base icon metrics (`metricX/metricY`) separately from rendered cell spacing. Normal restoration updates these persistent metrics in a defined DPI context, lets Shell rebuild its grid through folder flags, and writes original coordinates through IFolderView. Earlier snapshots with temporary ListView spacing are migrated by bounded measurement of Shell's rendered spacing, without coordinate offsets. Three actual Shell refreshes must pass before success; integration tests additionally perform five refreshes. Raw ListView state writes remain only for emergency rollback to a pre-operation snapshot.

Validated on this host: 85 items, size 40 -> 36/44/64 -> 40, exact original coordinates and flags. Expected coordinates are never shifted to match actual coordinates. New items are left where the Shell places them; missing or misplaced saved items prevent a full-success result.

System scaling uses active QueryDisplayConfig paths and monitor identities. Scale values are independently read with GetScaleFactorForMonitor. The source-DPI -3/-4 request packets are undocumented Windows functionality (research: https://github.com/lihas/windows-DPI-scaling-sample), so packet sizes/ranges and actual applied percentages are validated before/after use. Cached PerMonitorSettings are never written. Custom or unsupported scale targets are rejected, and operation failures attempt to restore the pre-operation scales and desktop.

`ScaleIntegration` also tests primary-only, secondary-only and combined icon/scale changes, correct A/A+B confirmation text, and rollback after a rejected icon-size target. Physical topology comparison excludes DPI scaling. `UiTest` drives the actual combined restore dialog/worker/success chain and checks mask continuity and the final desktop. Current host validated: each monitor 150% -> 125% -> 150%, unchanged physical resolutions, and 85 exact positions. Unplug/replacement scenarios have not been physically tested.

The .NET Framework shipping compiler can separately compile the same sources as a library under `bin/` to check C# 5 compatibility. Do not run `build-wpf.cmd` until the user requests an EXE build.
