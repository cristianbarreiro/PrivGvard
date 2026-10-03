# Windows Autostart Integration & Protection Lifecycle

This document specifies the architecture, sequence, and environment boundaries for PrivGvard's automatic startup integration on Windows.

---

## 1. Lifecycle Goal & Sequence

The goal of Windows autostart in PrivGvard is to ensure that privacy protections configured by the user become active upon Windows logon seamlessly, without requiring manual intervention and without blocking normal Windows boot or login performance:

```text
Windows Logon / Boot
    ↓
Windows launches autostart entry: PrivGvard.exe --minimized
    ↓
Single-Instance Mutual Exclusion Check (Local\PrivGvard_SingleInstance_Mutex)
    ↓
Privileged Operation Barrier (quiescence of any transient elevated worker)
    ↓
Startup Recovery (recover any unfinished session from previous crash / unexpected reboot)
    ↓
Desired Protection Reconciliation (reapply user's persistent DesiredState against live OS)
    ↓
Privacy Protection Becomes Active (verified via native EffectiveStatus & new WAL session)
    ↓
Application Initializes UI in Tray Mode (window hidden, tray icon visible, responsive)
```

### Architectural Division of Responsibility
- **Autostart Mechanism**: **Only launches the application process.** Autostart does NOT directly alter device states, manipulate Registry Group Policies, or hold elevated tokens.
- **Application Lifecycle**: Responsible for single-instance validation, startup recovery, desired state reconciliation, and user interaction via the System Tray.

---

## 2. Key Components & Implementation Details

### A. Registry Run & StartupApproved Integration
- **Registry Location**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **Value Name**: `PrivGvard`
- **Value Data**: `"{exePath}" --minimized`
- **Legacy Cleanup**: When enabling autostart, legacy `PrivLock` values in `Run` and `StartupApproved\Run` are automatically removed to prevent duplicate or conflicting invocations.
- **Windows Task Manager & Settings Overlay (`StartupApproved\Run`)**:
  - Windows stores user disable preferences from Task Manager ("Startup" tab) or Windows Settings in `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` as a 12-byte binary value.
  - When byte 0 has bit 0 set (`0x03`), Windows disables the autostart entry even if the key in `Run` remains.
  - `WindowsAutostartProvider.IsAutostartEnabled()` inspects this binary flag to report the effective autostart state accurately.
  - When the user enables autostart from PrivGvard Settings, `WindowsAutostartProvider` removes any existing `StartupApproved\Run` disable override so the entry becomes immediately active.

### B. Executable Path Handling
- Paths are strictly enclosed in double quotes: `"{exePath}" --minimized`. This guarantees safe execution when installed in paths with spaces (e.g., `C:\Program Files\PrivGvard\PrivGvard.exe`).
- `WindowsAutostartProvider.ExtractExecutablePath(string)` supports both quoted and unquoted command lines.

### C. Failure Handling When Executable Path Changes
- If an executable is moved, renamed, uninstalled, or deleted (e.g., clean build in a development directory):
  - `WindowsAutostartProvider.IsAutostartEnabled()` verifies `File.Exists(registeredPath)`.
  - If the registered path does not exist on disk, it logs a warning and returns `false`.
  - The UI accurately indicates that autostart is not active, allowing the user to re-enable it with the current valid binary path.

### D. Duplicate Startup Prevention
- `ISingleInstanceGuard` (`WindowsSingleInstanceGuard`) creates a session-scoped mutex: `Local\PrivGvard_SingleInstance_Mutex`.
- If an autostart launch coincides with a manual user launch (or secondary startup shortcut), `TryAcquireSingleInstance()` immediately returns `false`.
- The duplicate process terminates with exit code `2` before initializing services, touching the recovery journal, or modifying OS device states.

### E. `--minimized` Argument & UI Lifecycle
- When `--minimized` is supplied on the command line:
  - `Program.Main` logs the background startup mode.
  - The argument is stripped from the command-line array passed to Avalonia to prevent unknown parameter errors.
  - In `App.axaml.cs`:
    - `_mainWindow.ShowInTaskbar = false;`
    - `_mainWindow.WindowState = WindowState.Minimized;`
    - On the initial `Opened` event, `window.Hide()` is invoked so no window flashes on the user's desktop.
- **System Tray Lifecycle**:
  - The System Tray icon remains visible with tooltip and context menu ("Abrir PrivGvard", "Salir").
  - Clicking the tray icon or selecting "Abrir" restores the window (`WindowState.Normal`, `ShowInTaskbar = true`, `Activate()`).
  - Closing the main window (`X` button or Alt+F4) does not exit the process; it hides the window back to the tray.
  - Selecting "Salir" from the tray triggers `ShutdownCoordinator.RestoreAsync("TrayExit")`, which safely restores live hardware states and clears the runtime session while preserving the persistent `DesiredState`.

---

## 3. Environment Boundaries & Build Limitations

PrivGvard behavior differs across build and execution environments:

| Vector | Development Build (`bin\Debug\`) | Release Build (`publish_out\`) | Installed Application (`Program Files`) |
| :--- | :--- | :--- | :--- |
| **Path Stability** | **Volatile**: Rebuilt or cleaned by `dotnet clean`/IDE. | **Semi-stable**: Bound to local publish directory. | **Permanent**: Fixed path (e.g. `C:\Program Files\PrivGvard\PrivGvard.exe`). |
| **Autostart Registration Warning** | Logged as warning in `WindowsAutostartProvider`. | Logged as warning if under `\bin\Release\`. | Clean registration without path warnings. |
| **Persistence Across Rebuilds** | If the `.exe` is deleted by a rebuild, `IsAutostartEnabled()` detects missing file and returns `false`. | Remains valid as long as directory is preserved. | Fully resilient across reboots and normal user workflows. |
| **Privileged Worker Invocation** | Uses current `.exe` path via `Environment.ProcessPath`. | Uses current `.exe` path via `Environment.ProcessPath`. | Uses verified application binary path. |
| **Uninstall Integration** | No uninstaller registered; manual registry cleanup. | No uninstaller registered; manual registry cleanup. | Registered in Windows Programs & Features; safe uninstall wrapper unblocks devices before removal. |
| **MSIX Package Context** | Standard Win32 execution. | Standard Win32 execution. | Supported via `PackageIdentityHelper` if packaged. |

---

## 4. Architectural Non-Goals & Invariants

To maintain system integrity, security, and user trust, PrivGvard explicitly adheres to the following rules:

1. **No Windows Services**: PrivGvard does NOT install a permanent Windows Service running in session 0.
2. **No Task Scheduler Workarounds**: PrivGvard uses the standard, user-transparent `HKCU\...\Run` registry key. It does not create scheduled tasks to bypass UAC prompts.
3. **No SYSTEM Privileges**: The application always runs with standard user rights (`asInvoker`). Elevation occurs strictly on-demand via an authenticated ephemeral worker (`--privileged-worker`) when hardware PnP nodes or HKLM policies are modified.
4. **No Kernel Drivers**: All protections rely entirely on standard, documented OS interfaces (`CfgMgr32.dll`, Core Audio endpoints, AppPrivacy Group Policies).
5. **No Blocked Boot**: The autostart entry never blocks Windows startup; the application initializes asynchronously in the user's desktop session.
