# PrivGvard — Agent Guidelines, Architecture & Security Rules

This document provides operational context, safety rules, architectural conventions, and engineering guidelines for AI agents working on **PrivGvard**.

> **Repository Identity:**
> - **Public Product Identity**: **PrivGvard** (produces `PrivGvard.exe` on Windows, `PrivGvard` on Linux/macOS).
> - **Internal Historical Identifiers**: The solution file is `CamMicBlocker.sln`, and internal namespaces/projects use `PrivLock.*` for backward compatibility and stability.
> - **Canonical Project Context**: [docs/ai/PROJECT_CONTEXT.md](docs/ai/PROJECT_CONTEXT.md).
> - **Audit & Evolution Roadmap**: [docs/ai/AUDIT-ROADMAP.md](docs/ai/AUDIT-ROADMAP.md).
> - **Do not perform a global rename of internal namespaces or solution files unless the task explicitly requests an internal code migration.**

---

## 0. Current Baseline — Do Not Overstate Support

- **Windows**: The primary verified platform with a production-grade durable privacy-session journal (WAL), ownership attestations, authenticated on-demand privileged worker (`--privileged-worker`), and verified recovery.
- **Linux & macOS**: Integration scaffolds. Their capability providers expose `CapabilityLevel.None` and production DI registers `UnsupportedPrivacySessionPlatformAdapter`. Do not describe either platform as safely supported for camera/microphone mutation until exact capture, verification, rollback, and hotplug adapters exist.
- **Test Baseline**: Run the complete active test suite. Do not hardcode volatile test counts into durable documentation; refer to CI and local test runner output.
- **Verified State Invariant**: A method returning `OperationResult.Ok()` is not evidence that a device is blocked. Only a fresh native observation (`EffectiveStatus`) justifies a verified protected state.

---

## 1. Multiplatform Safety, Hardware Integrity & Security Policy

### A. Core Security Principles
- **Single-Binary & Least Privilege (On-Demand Elevation)**:
  - PrivGvard runs as **one single application (`PrivGvard.exe` / `PrivGvard`)** starting with standard unprivileged user rights (`asInvoker`).
  - There is **NO second elevated application** (`PrivLock.Elevated.exe` or similar is strictly prohibited).
  - Privileged actions (e.g., PnP node state, HKLM Registry Group Policies) request OS authorization/elevation **strictly on-demand** at the moment the user triggers that specific action.
- **Official Native APIs First**: Always use officially supported platform APIs:
  - **Windows**: `CfgMgr32.dll` PnP node management, `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` Group Policies, WMI `Win32_PnPEntity`, and Core Audio endpoint state.
  - **Linux**: V4L2 device discovery, PipeWire/PulseAudio or WirePlumber APIs, and Polkit only after an exact reversible state model exists. Never use a blind `chmod`/`chmod 660` pair as a restore strategy.
  - **macOS**: CoreAudio HAL for exact input state and AVFoundation/TCC for truthful camera capability reporting. macOS TCC must not be represented as if the application can silently revoke another app's camera permission.
- **Zero Low-Level Hardware Tampering**: Never attempt to modify hardware firmware, device registers, flash memory, EEPROM, or kernel-mode driver binaries. All hardware protection must be strictly software-level PnP node state toggling, driver unbinds, sound server locks, and OS Group Policy enforcement.
- **Zero Security Bypasses**: Never implement UAC bypasses, bypass driver signature enforcement, disable Windows Defender, bypass macOS SIP / hardened runtime, or alter system security subsystems. Security and system stability take priority over implementation convenience.
- **Fail Securely & Verified State**: Never report a device as "Blocked" unless verified against actual state (`EffectiveStatus`).
- **No Swallowing Errors**: Never catch exceptions silently (`catch { }` is strictly prohibited). Critical driver, PnP, permission, or hardware errors must be logged with context and passed to `CrashReporter`. Native command exit codes, timeouts, and verification failures are operation failures, not warnings that can be converted into success.

### B. Risk Classification & Validation Policy
Validate changes proportionally to their potential impact on system stability:

- **Low-Risk Changes (UI / UX / Localization / ViewModels)**:
  - *Scope*: Avalonia XAML layouts, Fluent dark styling, `LocalizationCatalog`, tray tooltips, non-blocking UI logic.
  - *Validation*: Build (`dotnet build`) and visual smoke test.

- **High-Risk Changes (PnP / Kernel Driver / Audio HAL / Registry / Privileges / OS Subsystems)**:
  - *Scope*: `PrivLock.Platform.Windows`, `PrivLock.Platform.Linux`, `PrivLock.Platform.MacOS`, `ProtectionService`, `app.manifest`, `Program.cs` startup.
  - *Validation*: Full unit test suite (`dotnet test`), published binary validation (`dotnet publish`), and 9-point risk assessment verification.

---

## 2. High-Risk Change Evaluation Checklist (9-Point Assessment)

Before committing changes to hardware controllers, policy managers, sound servers, or privileged subsystem code on any operating system, evaluate the following 9 failure vectors:

1. **OS Subsystem Impact**: Which subsystem is modified (Windows PnP / Registry, Linux V4L2 / PipeWire / PulseAudio, macOS CoreAudio HAL)?
2. **Partial Failure Handling**: What happens if device 1 disable succeeds but device 2 fails?
3. **Clean Reversibility**: Can the system state be 100% restored if an error occurs mid-operation?
4. **Reboot Resilience**: Will the device or policy state remain consistent after a system restart?
5. **Hardware Disconnection**: How does the code handle a camera or microphone being physically unplugged/replugged mid-operation (*hotplug*)?
6. **Elevation / Permission Denial**: What happens if the user denies elevation (UAC prompt, Polkit dialog, sudo)? The system must return a clean user-facing error and avoid corrupted state.
7. **Privilege Loss / Restricted Environment**: Does the application degrade gracefully if running in a sandboxed or non-root environment?
8. **Driver Problem States**: How does the system handle pre-existing problem states (e.g., `CM_PROB_DISABLED` 0x16 on Windows, unmounted sound card on Linux)?
9. **Post-Mortem Observability**: Is sufficient non-sensitive diagnostic data logged to diagnose field failures via structured JSON crash dumps?

---

## 3. Dynamic On-Demand Elevation Architecture (6-Point Assessment)

The following architecture is implemented for Windows. Linux and macOS elevation providers are present as platform seams, but their privacy mutation and persistent recovery paths are not production-enabled.

### A. Architectural Rationale & Threat Model
1. **Why On-Demand Elevation is Used**:
   Windows security requires administrative permissions to modify machine-wide registry keys (`HKLM`) and toggle PnP device states (`CfgMgr32.dll!CM_Disable_DevNode`). Linux/macOS elevation must not be introduced for privacy mutation until an exact recovery adapter exists.
2. **What Privileged Operations Are Performed on Windows**:
   - Setting/removing `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` keys (`LetAppsAccessCamera`, `LetAppsAccessMicrophone`).
   - Disabling/enabling PnP device nodes in `CfgMgr32.dll` via device instance IDs.
   - Linux `/dev/video*`, driver unbind, PipeWire, and macOS authorization changes are roadmap items, not currently supported operations.
3. **Why Single-Binary Self-Invocation is Superior on Windows**:
   Instead of a permanent elevated daemon or a separate binary (`PrivLock.Elevated.exe`), `PrivGvard` invokes its own executable (`Environment.ProcessPath`) as a transient authenticated `--privileged-worker` using Windows UAC `Verb="runas"`. The legacy public `--privileged-exec` mutation dispatcher is prohibited and rejected because it could bypass the recovery journal.
4. **Communication & IPC**:
   - Windows uses short-lived transient execution and a bounded, versioned named-pipe protocol.
   - The CLI carries only a random pipe name, parent PID, and 256-bit nonce.
   - ACL, nonce, request correlation, and bilateral kernel PID verification are required; no temporary file is used for IPC results. Linux/macOS have no equivalent production worker path yet.
5. **Minimizing Attack Surface**:
   - The main application runs with unprivileged `asInvoker` token.
   - The authenticated worker implements a closed per-resource whitelist (`apply-policy`, `restore-policy`, `apply-device`, `restore-device`, `verify-policy-ownership`, `verify-device-ownership`, `ping`) and revalidates canonical targets immediately before native calls.
   - Strict parameter validation prevents command injection.
6. **Zero Unnecessary Privilege Retention**:
   The elevated transient process exits immediately upon completing the API call. The user-facing application never retains administrative privileges.

---

## 4. Architecture & Layered Project Structure

```text
src/
├── PrivLock.Domain/                  # Pure C# domain: Models, Enums, Capabilities, Results
│   ├── Models/                       # BlockStatus, BlockTarget, DeviceType, DeviceInfo, BlockState
│   ├── Capabilities/                 # PlatformCapabilities, CapabilityLevel, PlatformInfo
│   └── Results/                      # OperationResult, DeviceOperationDetail, ElevationResult
│
├── PrivLock.Platform.Abstractions/   # Pure OS contracts and interfaces
│   ├── IDeviceProtectionProvider.cs  # Unified block/unblock/status contract
│   ├── IDeviceDetector.cs            # Camera and microphone hardware enumeration
│   ├── IPlatformCapabilityProvider.cs# Declarative capability matrix query
│   ├── IElevationProvider.cs         # Decoupled elevation detection & request
│   ├── IAutostartProvider.cs         # Operating system autostart management
│   ├── IGlobalHotkeyProvider.cs      # Global hotkey registration
│   ├── ISingleInstanceGuard.cs       # Single instance mutex/lockfile guard
│   └── IStateStore.cs                # State persistence contract
│
├── PrivLock.Infrastructure.Common/   # Cross-platform shared infrastructure
│   ├── Storage/                      # FileStateStore & StorageMigrationHelper (JSON in OS AppData)
│   ├── Logging/                      # LoggingConfiguration (Serilog) & CrashReporter (JSON dumps)
│   └── Localization/                 # LocalizationCatalog (In-memory bilingual catalogs)
│
├── PrivLock.Application/             # Use cases & orchestration (100% OS & UI agnostic)
│   └── Services/                     # ProtectionService, SettingsService, LocalizationService, PrivacySessionService
│
├── PrivLock.Platform.Windows/        # Windows native implementations
│   ├── Devices/                      # CfgMgr32 P/Invoke & WindowsDeviceDetector (WMI/GUIDs)
│   ├── Policies/                     # HKLM AppPrivacy Registry Policies
│   ├── Elevation/                    # WindowsElevationProvider & On-Demand UAC
│   ├── Privileged/                   # WindowsPrivilegedWorker & Named-Pipe IPC Protocol
│   └── System/                       # WindowsAutostartProvider, WindowsHotkeyProvider, WindowsSingleInstanceGuard
│
├── PrivLock.Platform.Linux/          # Linux native implementations (Scaffold)
│   ├── Devices/                      # V4L2/sysfs DeviceDetector & PipeWire/PulseAudio Controller
│   ├── Elevation/                    # LinuxElevationProvider (Polkit/pkexec / euid)
│   └── System/                       # LinuxAutostartProvider, LinuxSingleInstanceGuard
│
├── PrivLock.Platform.MacOS/          # macOS native implementations (Scaffold)
│   ├── Devices/                      # CoreAudio HAL DeviceDetector & Input Mute Controller
│   ├── Elevation/                    # MacOSElevationProvider (osascript / authorization)
│   └── System/                       # MacOSAutostartProvider, MacOSSingleInstanceGuard
│
├── PrivLock.UI/                      # Multiplatform UI in Avalonia 11
│   ├── Views/                        # MainWindow.axaml, SettingsWindow.axaml with Fluent Dark styling
│   ├── ViewModels/                   # MainViewModel, DeviceItemViewModel, SettingsViewModel
│   └── App.axaml                     # Fluent Dark theme, styles, tray icon lifecycle
│
└── PrivLock.Desktop/                 # Single Executable Host (PrivGvard.exe)
    ├── Program.cs                    # Platform DI composition root & authenticated --privileged-worker dispatcher
    └── app.manifest                  # requestedExecutionLevel = asInvoker
```

### Active Host vs. Legacy Architecture
- **ACTIVE HOST**: `src/PrivLock.Desktop` (produces `PrivGvard.exe` on Windows, `PrivGvard` on Linux/macOS).
- **LEGACY**: `legacy/CamMicBlocker` (archived PrivLock 1.x implementation with build guard; not part of active solution).
- **NORMAL BUILDS MUST NEVER BUILD LEGACY CODE.**

---

## 5. Build, Test & Publish Commands

### Restore & Build Solution
```powershell
dotnet build CamMicBlocker.sln
```

### Run Active Test Suite
```powershell
# On Windows (runs all tests including Windows platform tests):
dotnet test CamMicBlocker.sln

# Cross-platform test execution (Domain, Application, Infrastructure):
dotnet test tests/PrivLock.Domain.Tests/PrivLock.Domain.Tests.csproj
dotnet test tests/PrivLock.Application.Tests/PrivLock.Application.Tests.csproj
dotnet test tests/PrivLock.Infrastructure.Tests/PrivLock.Infrastructure.Tests.csproj

# Windows Platform tests (Windows runners only):
dotnet test tests/PrivLock.Platform.Windows.Tests/PrivLock.Platform.Windows.Tests.csproj
```

### Run Application (Debug)
```powershell
dotnet run --project src/PrivLock.Desktop/PrivLock.Desktop.csproj
```

### Publish Single-File Executables

#### Windows x64:
```powershell
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish_out/win-x64
```

#### Linux x64:
```powershell
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish_out/linux-x64
```

#### macOS ARM64 (Apple Silicon):
```powershell
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish_out/osx-arm64
```

---

## 6. AI-Assisted Engineering Workflow

1. Read [docs/ai/PROJECT_CONTEXT.md](docs/ai/PROJECT_CONTEXT.md) before making architectural assumptions.
2. For an audit, use [docs/ai/AUDIT-ROADMAP.md](docs/ai/AUDIT-ROADMAP.md) as the current evidence baseline and update it when findings change.
3. For implementation, state the affected OS subsystem, mutation boundary, rollback proof, verification observation, and test plan before editing high-risk code.
4. Treat README marketing text, generated publish folders, and legacy `legacy/CamMicBlocker` files as non-authoritative unless the task explicitly includes them.
5. If a requested feature cannot be implemented with exact capture, verification, and recovery, keep it read-only or return an explicit unsupported result. Never make a partial implementation look protected.
