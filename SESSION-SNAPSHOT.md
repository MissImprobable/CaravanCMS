# CaravanCMS session snapshot — 2026-08-11

Resume point for continuing in an elevated PowerShell/Claude Code session.

## Done and confirmed working

1. **Outlook add-in cert chain** — fully resolved, user confirmed a successful
   request round-tripped to the server through the add-in.
   - Root causes (all three had to be fixed, in this order): trust never
     installed on `LocalMachine\Root`; cert had a `DNS Name` SAN for an IP
     instead of `IP Address` SAN (WebView2/Chromium rejects that); the
     *build-output* copy of `certs/*.pfx`/`*.cer` under
     `bin\Debug\net10.0-windows\certs\` was stale and the running process was
     still serving the old cert even after the source-tree cert was fixed.
   - Fixed `CaravanCMS.Api/certs/New-CaravanCmsCertificate.ps1` to build a
     proper `IPAddress=` SAN via `-TextExtension` when `-Address` is an IP.
   - Full writeup and troubleshooting checklist: `.claude/skills/outlook-addin/SKILL.md`.

2. **Admin app "Could not find CaravanCMS.Api.exe" bug** — fixed. Settings
   changes to `ApiExePath` weren't taking effect until a full Admin app
   restart because `App.ApiHost` was built once at startup. Added
   `App.RefreshApiHostSettings()`, wired into `SettingsWindow.Save_Click`.
   Rebuilt and relaunched, confirmed no compile errors.

3. **Stale build output cleanup** — removed leftover `net9.0`/`net9.0-windows`
   bin/obj folders across all 4 projects (all now target
   `net10.0`/`net10.0-windows` per their `.csproj` files), plus a stray
   non-windows `net10.0` build of `CaravanCMS.Api` that was the direct cause
   of item 1's third root cause. All gitignored/regenerable, safe.

## Resolved

4. **`CaravanCMS.Client` hard crash in the Conversations tab — ROOT CAUSE FOUND
   AND FIXED (2026-08-11, elevated session).**
   - WER LocalDumps was registered for `CaravanCMS.Client.exe`
     (`HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\`),
     crash reproduced, full dump captured to `%TEMP%\CaravanCMS-dumps`, and
     analyzed with `dotnet-dump analyze` (`clrthreads`, `clrstack`,
     `printexception -nested`).
   - **Actual root cause:** `CaravanCMS.Client/Views/CaravanDetail.xaml` line
     397, `<Run Text="{Binding Messages.Count}"/>` in the conversation-message
     template, was missing `Mode=OneWay`. `Run.Text` defaults to a `TwoWay`
     binding (like `TextBox.Text`), and `Messages` is a
     `List<CommunicationLogDto>` whose `Count` property has no setter, so WPF
     threw: *"A TwoWay or OneWayToSource binding cannot work on the read-only
     property 'Count' of type
     'System.Collections.Generic.List\`1[CaravanCMS.Core.CommunicationLogDto]'."*
   - **Why it was a StackOverflowException and not just an error dialog:**
     `App.xaml.cs` line 18's `DispatcherUnhandledException` handler just calls
     `MessageBox.Show(...)`. `MessageBox.Show` pumps its own nested Win32
     message loop, which re-entered layout/focus processing for the same
     `TabItem`, re-triggering the identical binding exception, which showed
     another `MessageBox`, recursively, ~24 times nesting
     `UIElement.Measure`/`FrameworkElement.MeasureCore` 448 deep before the
     native stack overflowed (`0xc00000fd`, uncatchable). Confirmed via
     `clrstack` showing 24 repeated nested cycles of
     `App.OnStartup.b__7_0` → `MessageBox.Show` → `TabItem.OnMouseLeftButtonDown`
     → `UpdateLayout` → template load → binding attach → same exception.
   - **Fix applied:** added `Mode=OneWay` to that one `Run.Text` binding,
     matching the pattern already used for the sibling `Jobs.Count` /
     `Documents.Count` / `Conversations.Count` bindings in the same file.
     Build verified clean (0 errors/warnings).
   - Note: `App.xaml.cs`'s `DispatcherUnhandledException` handler is still
     fragile (any future unhandled exception on this thread risks the same
     MessageBox-reentrancy stack overflow) but that's a separate, lower-
     priority hardening item, not required to fix this specific crash.

## Superseded — original crash write-up (kept for history)

4-orig. **`CaravanCMS.Client` hard crash in the Conversations tab.** User reports
   it crashes when opening a caravan and viewing a logged conversation.
   - First fix applied: `CaravanCMS.Client/Views/CaravanDetail.xaml`, the
     `MultiBinding` for the nested messages `ItemsControl.Visibility`
     (around line ~432) was missing `Mode="OneWay"`. `MultiBinding` defaults
     to `Mode="TwoWay"` (unlike a plain `Binding`), so WPF was calling
     `ConvertBack` on `EqualityToVisibilityConverter`
     (`CaravanDetail.xaml.cs`), which just `throw new NotImplementedException()`.
     Added `Mode="OneWay"`. **This was a real bug and is fixed, but it is NOT
     the crash the user is hitting.**
   - Actual crash confirmed via Windows Event Log
     (`Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1001}`):
     `System.StackOverflowException`, faulting module `PresentationCore`,
     exception code `0xc00000fd`. This is a native fail-fast — cannot be
     caught by any managed exception handler (`AppDomain.UnhandledException`
     etc. will not reliably fire or won't prevent termination).
   - **Confirmed this happened AGAIN after rebuilding with the MultiBinding
     fix** (crash at 9:42:44, exe rebuilt at 9:39:00) — so there's a second,
     still-unidentified recursion bug, separate from the ConvertBack one.
   - Same crash signature (`CaravanCMS.Client`, same fault bucket family) has
     occurred before, on 2026-05-25, per old WER ReportArchive entries — this
     is not new, just newly noticed.
   - No `.mdmp` crash dump was retained anywhere (WER only kept `Report.wer`
     summaries, not full dumps) — couldn't get a real stack trace from past
     crashes.
   - `dotnet-dump` global tool is now installed (`dotnet tool install --global
     dotnet-dump` succeeded) so a dump CAN be analyzed once we have one.
   - Attempted to configure `HKLM:\SOFTWARE\Microsoft\Windows\Windows Error
     Reporting\LocalDumps\CaravanCMS.Client.exe` (DumpType=2 full dumps,
     DumpFolder=`%TEMP%\CaravanCMS-dumps`) so the *next* crash saves a full
     dump automatically — **this needs Administrator and was denied**. This
     is why the user is restarting in an elevated prompt.

## Next steps once elevated

1. Run this (also pasted earlier in chat) to enable full crash dumps:
   ```powershell
   $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\CaravanCMS.Client.exe"
   New-Item -Path $key -Force | Out-Null
   New-ItemProperty -Path $key -Name "DumpFolder" -Value "$env:TEMP\CaravanCMS-dumps" -PropertyType ExpandString -Force | Out-Null
   New-ItemProperty -Path $key -Name "DumpType" -Value 2 -PropertyType DWord -Force | Out-Null
   New-ItemProperty -Path $key -Name "DumpCount" -Value 5 -PropertyType DWord -Force | Out-Null
   New-Item -ItemType Directory -Path "$env:TEMP\CaravanCMS-dumps" -Force | Out-Null
   ```
2. Relaunch `CaravanCMS.Client.exe` (rebuilt already with the `Mode="OneWay"`
   fix — that fix is real and should stay, it just isn't the whole story).
3. Reproduce the crash the same way as before (open a caravan, go to
   Conversations, do whatever triggered it — expanding a conversation and/or
   clicking a tag chip are the two interactive paths in that tab).
4. A `.dmp` should land in `%TEMP%\CaravanCMS-dumps`. Analyze with:
   ```
   dotnet-dump analyze <path-to-dump>
   ```
   then `clrstack` (or `pstacks` for all threads) inside the analyzer to get
   the actual repeating frame — that tells us exactly what's recursing,
   instead of guessing further through the XAML.

## Also relevant / already known from this session

- Server PC is `192.168.1.150`, runs `CaravanCMS.Api.exe` as the Kestrel
  host; same PC doubles as an Outlook client.
- Real config (incl. secrets) lives in `CaravanCMS.Api/appsettings.Production.json`
  — **in the repo**, not external. API key:
  `caravanland-internal-api-key-2024` (`CaravanCMS.Api/appsettings.json`).
- `Start-Process -Verb RunAs` from this tool's PowerShell does not reliably
  trigger a UAC prompt the user can interact with — that's the whole reason
  for this restart. Elevated actions need to be run by the user directly (or
  from an already-elevated shell).
