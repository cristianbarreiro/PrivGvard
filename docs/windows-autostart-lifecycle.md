# Windows Autostart Lifecycle

This document describes the implemented Windows startup behavior and the acceptance checks that still require an isolated Windows VM.

## Distribution-specific mechanism

PrivGvard uses exactly one startup mechanism for the distribution currently running:

| Distribution | Registration | Effective state |
| --- | --- | --- |
| Unpackaged Win32 installer or portable build | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PrivGvard`, with a quoted absolute executable path and `--minimized` | The Run value must match the current verified `PrivGvard.exe` and the file must exist. The provider also reads the Windows-private `StartupApproved\Run` value as best-effort user-disable information; Windows does not document that registry format as a public API. |
| MSIX | The declared `PrivGvardStartupTask` using `Windows.ApplicationModel.StartupTask` | The state reported by Windows (`Enabled`, `Disabled`, `DisabledByUser`, `DisabledByPolicy`, and policy-enabled states). The manifest passes `--minimized` to the packaged full-trust process. |

The MSIX task is enabled only through `RequestEnableAsync`, which allows Windows to present its own consent UI. Microsoft documents that this call does not override a Task Manager user-disable decision; the user must re-enable the task in Windows. Disabling uses the distribution's matching API and verifies the effective state afterward.

For unpackaged Win32, disabling in PrivGvard removes its owned `Run` value. The entry consequently disappears from Windows Startup Apps/Task Manager rather than remaining visible as a disabled row. No supported public Win32 API was found to disable the entry while preserving that row. The `StartupApproved` key and its binary format are private Windows implementation details. Enable/disable operations do not create or alter its values; the existing ownership-checked migration may copy an existing legacy approval value byte-for-byte and removes only the old owned value after verification. Reading that value is retained only as best-effort compatibility with the existing provider and is not a documented Windows contract. Enabling writes/verifies the owned `Run` value but cannot override a Windows user decision. If Windows reports a user-disabled status, the user must enable it in Windows Startup Apps.

The `DesiredState.Autostart` field records only the last effective state confirmed after an in-app operation for storage compatibility. Startup never reads it to create or enable a registration, so an old saved preference cannot override a newer Windows user or policy decision.

Win32 registration is rejected when the current path is not fully qualified, does not name a verified `PrivGvard.exe`, is missing, or is a known unstable development output (`bin\Debug\<TFM>`, `bin\Release\<TFM>`, or `publish_out`). A published executable under a plain `bin\Release` directory is not classified as an SDK build output without the following target-framework segment. A registration to a missing or different executable is not reported as enabled. Registry and StartupTask API exceptions, invalid values, policy blocks, user disablement, and migration conflicts remain distinct statuses.

## Live state synchronization

`AutostartStateService` is the single in-process effective-state source consumed by both `MainViewModel` and `SettingsViewModel`. It serializes reads and in-app operations, publishes only a changed effective observation, and converts read failures to an explicit failed/unknown state. An unknown state is rendered as an indeterminate, disabled checkbox, not as a confident unchecked value. The checkbox binding is one-way from the shared state during refreshes; only user command invocation requests an operation.

After the single-instance and uninstall gates, startup migration, privileged-operation quiescence, and privacy recovery/reconciliation, the desktop host starts one autostart observer. For Win32, it uses `RegNotifyChangeKeyValue` on the current user's `CurrentVersion` registry subtree, re-registers after each notification, and re-reads the complete state through the provider after a 120 ms coalescing delay. The notification is only a trigger; it is never treated as the resulting state. The observer owns and disposes its registry/event handles for each wait and is cancelled before process teardown. It does not write registry values or start before the existing startup gates.

The MSIX `StartupTask` API does not expose a corresponding change event in the accessor used here. The app refreshes on main/settings window activation and when settings opens; while the settings window remains open, it refreshes at a bounded two-second interval. That timer stops when settings closes. No periodic polling runs while the settings UI is closed.

## Migration and single-instance ordering

After acquiring the Windows single-instance and uninstall-gate mutexes, Win32 startup migrates a recognized legacy `PrivLock` Run entry to the current verified executable. It validates any existing `PrivGvard` replacement and rejects conflicting `StartupApproved` user states before changing either registration. The old values are removed only after the replacement and its effective approval state are verified; failures attempt a rollback. In MSIX context, startup first reads and validates the package `StartupTask`, both app-owned Run commands, and their approval states. A prior disabled Win32 preference is carried to an enabled, non-policy-controlled task before stale Run entries are removed. If the task cannot be read, policy conflicts with that preference, or an entry is malformed, migration leaves the Win32 values in place and reports the conflict. A migration error is shown separately from privacy recovery/reconciliation errors; it does not start elevation or mutate privacy state.

The order remains:

```text
Launch (--minimized from the selected registration)
  -> acquire single-instance/uninstall-gate mutexes
  -> migrate only owned obsolete autostart entries
  -> wait for a prior privileged operation to quiesce
  -> recover the durable privacy journal
  -> reconcile desired privacy state
  -> initialize the UI and tray
```

A duplicate process exits before autostart migration, recovery, or privacy mutation. `asInvoker`, the authenticated transient privileged worker, and on-demand UAC are unchanged.

The app hides at minimized startup and hides on window close only after the active Avalonia backend returns a native tray implementation and tray initialization succeeds. If tray support is unavailable, startup is shown normally and X/Alt+F4 goes through the same coordinated restore flow as the tray Exit command. The window remains open and surfaces the restore error if recovery is not safe, allowing the user to retry or intervene.

## Automated evidence and limits

Windows provider tests exercise registration/migration against unique, isolated per-test HKCU registry keys. Injected provider, observer, and StartupTask seams cover state transitions, debounce/re-arm, cancellation, observer errors, and Windows-reported MSIX states. Test-only `StartupApproved` byte arrays are opaque migration fixtures; they do not establish the real Windows binary format or prove native Task Manager behavior. Lifecycle tests that simulate a restart do not log out, reboot Windows, or prove that the OS launches the app after a real login; they are not restart acceptance evidence.

## Manual acceptance matrix — pending isolated Windows VM

Run each distribution in a disposable Windows VM with a standard user account. Record Windows version/build, package or installer version, effective UI state, relevant registration state, and screenshots/log excerpts with personal paths redacted. Do not treat a simulated lifecycle test as a substitute for the actual sign-out/sign-in steps below.

| Distribution | Scenario | Acceptance |
| --- | --- | --- |
| Win32 installer | Fresh install; enable startup | UI reports enabled only after verified Run registration; exactly one `PrivGvard` Run value contains a quoted installed path plus `--minimized`; no `PrivLock` value remains. |
| Win32 installer | Sign out and sign in with tray available | One process starts minimized, tray can restore the window, and no duplicate process remains. This is the required real logon test. |
| Win32 installer | Disable from PrivGvard, then sign out/sign in | Run registration is absent; no PrivGvard startup launch occurs. The Windows Startup Apps row disappears; it is not retained as a disabled row. |
| Win32 installer | Disable from Task Manager/Windows Startup Apps while PrivGvard is open | The registry notification triggers a full provider read and the open UI updates. Classification as disabled-by-user uses the undocumented `StartupApproved` format and remains pending native VM verification; in-app enable/disable never alters that value. |
| Win32 installer | Re-enable from Task Manager/Windows Startup Apps while PrivGvard is open | The open UI refreshes from the final provider observation. Classification uses the same undocumented `StartupApproved` value and remains pending native VM verification. |
| Win32 installer | Attempt enable from a development output, after deleting/moving the registered executable, or with a non-PrivGvard executable | Enable fails with a visible reason; stale paths are not reported enabled. |
| Win32 installer | Seed only a recognized legacy `PrivLock` Run entry and StartupApproved value | Starting the app migrates the entry to the verified current executable, preserves a user-disabled state, removes the old owned values only after verification, and leaves unrelated Run values unchanged. |
| Win32 installer | Seed an invalid current `PrivGvard` command or conflicting `PrivLock`/`PrivGvard` StartupApproved states | Migration reports failure and leaves both Run values and approval records unchanged. |
| Win32 installer | Upgrade installed version in place | The next startup registration points to the current installed executable, not an older directory. |
| MSIX | Fresh package; enable startup | Windows consent appears only after the user enables the setting; accepted consent results in `Enabled` after readback and the manifest launches with `--minimized`. |
| MSIX | Decline consent, or disable in Windows Startup Apps | UI reports the Windows/user-disabled state and explains how to change it; PrivGvard does not silently re-enable the task. |
| MSIX | Apply a policy that disables startup | UI reports `DisabledByPolicy`; enable does not claim success or create a Run-key fallback. |
| MSIX | Transition from Win32 with Startup Apps disabled | A readable, non-policy-enabled StartupTask is disabled before old Run and approval entries are removed; policy/state conflicts leave the old registrations intact and report migration failure. |
| MSIX | Disable from PrivGvard | StartupTask is disabled and verified; app-owned obsolete Win32 Run and approval values are removed; no parallel registration remains. |
| MSIX | Update package version with startup enabled | StartupTask continues to target the installed package version; no old `PrivGvard` or `PrivLock` Run value contains an obsolete package path. |
| Both | Run `--minimized` with a working tray, then with tray support unavailable | Working tray allows hiding and restoring. Unsupported tray keeps the main window visible; X/Alt+F4 runs coordinated restoration and cannot close the window while retryable recovery remains. |
| Both | Launch two processes concurrently | Exactly one acquires the Windows single-instance mutex; only that process reaches recovery/reconciliation. |
| Both | Exercise startup recovery failure and UAC denial independently | Each failure is reported as recovery/elevation, not as autostart state; the autostart checkbox continues to reflect its actual mechanism. |

**Status:** the manual matrix above has not been executed in a VM. No claim is made that either installer or MSIX startup after a real sign-out/sign-in or reboot has been validated.
