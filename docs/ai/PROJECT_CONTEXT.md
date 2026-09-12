---
schema: privgvard.project-context.v1
kind: canonical-open-knowledge
product_name: PrivGvard
repository_name: CamMicroBlocker
current_code_identity: PrivLock
status_date: 2026-09-12
source_of_truth: repository-code-and-tests
primary_supported_platform: Windows
target_platforms: [Windows, Linux, macOS]
---

# PrivGvard — Canonical Project Context (OKF)

## 1. Purpose and product identity

PrivGvard is a transparent, reversible privacy utility designed to provide users with explicit control over their camera and microphone without modifying hardware firmware, device registers, kernel binaries, security subsystems, or driver signature enforcement.

### Public Product Identity vs. Internal Historical Identifiers
- **Public Product Identity**: **PrivGvard** (Version 2.0.0).
  - Executable: `PrivGvard.exe` on Windows; `PrivGvard` on Linux and macOS.
  - AssemblyName, Product, and Title: `PrivGvard` (configured in [PrivLock.Desktop.csproj](../../src/PrivLock.Desktop/PrivLock.Desktop.csproj)).
  - Storage directory: `%LOCALAPPDATA%\PrivGvard` (Windows), `~/.local/share/PrivGvard` (Linux), `~/Library/Application Support/PrivGvard` (macOS), with automated migration from legacy `PrivLock` directories via `StorageMigrationHelper`.
  - Logging directory: `%LOCALAPPDATA%\PrivGvard\Logs\`.
  - Crash reports: `%LOCALAPPDATA%\PrivGvard\CrashReports\`.
  - Windows installer: `PrivGvard-Setup-2.0.0.exe` (built via [build-installer.ps1](../../build-installer.ps1) with Inno Setup 6).
  - Portable distribution: `PrivGvard-Portable-2.0.0.zip`.
  - CI build artifacts: `PrivGvard-win-x64`, `PrivGvard-win-arm64`, `PrivGvard-linux-x64`, `PrivGvard-osx-arm64`.
- **Internal Historical Code Identifiers**:
  - Repository name: `Cam&MicroBlocker` (GitHub: `cristianbarreiro/PrivGvard`).
  - Solution file: `CamMicBlocker.sln`.
  - Namespaces and projects: `PrivLock.*` (`PrivLock.Domain`, `PrivLock.Platform.Abstractions`, `PrivLock.Infrastructure.Common`, `PrivLock.Application`, `PrivLock.UI`, `PrivLock.Platform.Windows`, `PrivLock.Platform.Linux`, `PrivLock.Platform.MacOS`, `PrivLock.Desktop`).
  - Registry keys: `SOFTWARE\PrivLock\PrivilegedOwnership\v2`.
  - IPC/Mutex primitives: `Global\PrivLock_SingleInstance`, `Global\PrivLock_UninstallGate`.
  - *Note*: Internal identifiers remain intentionally stable for code continuity, backward compatibility, and atomic migration safety.

---

## 2. Current verified state

| Area | Verified truth | Confidence |
|---|---|---|
| Runtime | .NET 10, Avalonia UI 11.2.3, single desktop host at `src/PrivLock.Desktop` | High |
| Architecture | Clean Architecture: Domain, Platform.Abstractions, Infrastructure.Common, Application, Native Platform Adapters, UI, and Desktop Host | High |
| Windows | Full mutation & persistent recovery supported: dual-layer AppPrivacy Group Policy, verified PnP device node control (`CfgMgr32.dll`), Core Audio capture endpoint mute lock, durable JSON Write-Ahead Log (WAL) session journal, authenticated transient on-demand privileged worker (`--privileged-worker`) via named pipes with 256-bit nonces and bilateral PID verification | High for simulated/unit tests; manual hardware VM acceptance required for physical release |
| Linux | Discovery & UI scaffold only: `LinuxDeviceDetector` enumerates V4L2/sysfs; capability provider reports `CapabilityLevel.None`; production DI registers `UnsupportedPrivacySessionPlatformAdapter`; privacy mutation is not supported | High |
| macOS | Discovery & UI scaffold only: `MacOSDeviceDetector` enumerates CoreAudio HAL inputs; capability provider reports `CapabilityLevel.None`; production DI registers `UnsupportedPrivacySessionPlatformAdapter`; privacy mutation is not supported (TCC cannot be silently revoked) | High |
| Test suite | Full active automated test suite covering Domain, Infrastructure, Application, and Windows Platform tests. Dynamic passing counts are validated by CI and test runner; volatile totals are intentionally omitted from durable docs | High |
| CI Topology | Multiplatform matrix (`windows-latest`, `ubuntu-latest`, `macos-latest`) in [.github/workflows/ci.yml](../../.github/workflows/ci.yml). Cross-platform tests (Domain, Application, Infrastructure) execute across all runners; Windows Platform tests execute strictly on Windows runners (`if: runner.os == 'Windows'`) | High |
| Legacy quarantine | Archived PrivLock 1.x WPF codebase isolated under `legacy/CamMicBlocker` and `legacy/tests/CamMicBlocker.Tests`; protected with build guards; completely excluded from active solution and normal builds | High |

> [!IMPORTANT]
> **Build Success != Test Success != Publish Success != Native Privacy Support**
> Successful cross-platform build or single-file publishing on Linux (`linux-x64`) or macOS (`osx-arm64`) verifies compiler and packaging compatibility only. It does NOT confer production privacy mutation or persistent recovery support on non-Windows platforms.

---

## 3. Architecture map

```text
src/
├── PrivLock.Domain/                  # Pure models, capabilities, results and session state
├── PrivLock.Platform.Abstractions/   # Device, protection, elevation, state and system contracts
├── PrivLock.Infrastructure.Common/   # JSON state/session storage, logging, crash reporting, localization
├── PrivLock.Application/             # Serialized use cases, privacy journal orchestration and recovery
├── PrivLock.Platform.Windows/        # Windows registry, PnP, Core Audio and authenticated worker
├── PrivLock.Platform.Linux/          # Linux discovery and experimental controller scaffolding
├── PrivLock.Platform.MacOS/          # macOS discovery and experimental controller scaffolding
├── PrivLock.UI/                      # Avalonia views, view models, localization, custom chrome
└── PrivLock.Desktop/                 # Single executable host (PrivGvard.exe), DI composition root, worker entry point

tests/
├── PrivLock.Domain.Tests/            # Cross-platform domain logic tests
├── PrivLock.Infrastructure.Tests/    # Cross-platform storage, crash reporting & localization tests
├── PrivLock.Application.Tests/       # Cross-platform application orchestration & recovery tests
└── PrivLock.Platform.Windows.Tests/  # Windows-specific PnP, registry, audio, worker & IPC tests

legacy/                               # Quarantined archived legacy code (build-guarded)
├── CamMicBlocker/
└── tests/CamMicBlocker.Tests/
```

### Active Host vs. Legacy Quarantine
- **ACTIVE HOST**: `src/PrivLock.Desktop` (compiles as `PrivGvard.exe` / `PrivGvard`).
- **LEGACY**: `legacy/CamMicBlocker` (archived PrivLock 1.x WPF code with compile-time build guard).
- **RULE**: Active builds and CI must never reference or build legacy files.

---

## 4. Window and System Tray Lifecycle

PrivGvard features an integrated desktop and notification-area lifecycle:

1. **Title-Bar Close Button (`X`) & `Alt+F4`**:
   - Intercepted by `MainWindow.OnClosing` (`e.Cancel = true`).
   - Hides `MainWindow` (`Hide()`) and removes the application from the Windows taskbar.
   - The process remains running in the background with its System Tray icon active.
2. **System Tray Icon**:
   - Left-click or double-click: calls `ShowMainWindow()` (`Show()`, `WindowState = Normal`, `Activate()`, `BringIntoView()`).
   - Right-click context menu: localized options dynamically bound via `LocalizationCatalog`:
     - **Abrir PrivGvard** / **Open PrivGvard**: restores the main window.
     - **Salir** / **Exit**: initiates clean application shutdown.
3. **Application Shutdown (`Salir` / `Exit` / OS Shutdown)**:
   - Initiates centralized teardown through `ShutdownCoordinator.RestoreAsync()`.
   - Reverses only confirmed PrivGvard-owned changes according to the persistent WAL journal.
   - Preserves external conflicts.
   - Closes open child windows, disposes the tray icon, and terminates the process cleanly.

---

## 5. Safety invariants

These invariants are mandatory for every implementation and AI-assisted change:

1. **Start Unelevated (`asInvoker`)**: The application process runs as a standard user process by default.
2. **On-Demand Elevation**: Elevation is requested transiently and strictly for the exact privileged action authorized by the user.
3. **Single Application Binary**: No permanent background service and no second elevated executable (`PrivLock.Elevated.exe` is strictly prohibited).
4. **Authenticated Worker**: Privileged operations execute via self-invocation (`PrivGvard.exe --privileged-worker <pipe> <pid> <nonce>`) using Windows UAC `runas`. IPC uses random named pipes, strict parent PID validation, 256-bit cryptographically random nonces, and a closed command whitelist (`apply-policy`, `restore-policy`, `apply-device`, `restore-device`, `verify-policy-ownership`, `verify-device-ownership`, `ping`). The legacy public `--privileged-exec` dispatcher is permanently removed.
5. **No Low-Level Hardware Tampering**: Never touch firmware, EEPROM, device registers, kernel driver binaries, SIP, UAC bypasses, Defender, or driver signature enforcement.
6. **Verified Effective State**: Never report a device or resource as `Blocked` or `Protected` without a fresh native post-mutation observation (`EffectiveStatus`). Desired state is never evidence of effective protection.
7. **Durable Journal & Reversibility**: Capture exact original state (existence, type, value, mute/volume, problem state) before mutation. Journal intent in Write-Ahead Logging (`privacy-session-v1.json`). Restore only confirmed session-owned resources.
8. **Preserve External Conflicts**: If a native resource was modified externally outside the session, preserve the external state as a conflict rather than overwriting it blindly.
9. **Fail Securely**: Treat timeouts, command failures, missing tools, permission denials, and verification failures as explicit operation errors. Never swallow exceptions or convert errors into `OperationResult.Ok()`.
10. **Sanitized Diagnostics**: Do not log raw user paths, credentials, or sensitive device details in rolling logs or crash dumps. Use operation IDs, anonymized metadata, and hashed identifiers.

---

## 6. Capability truth by platform

| Platform | Camera Protection | Microphone Protection | Persistent Recovery | Status / Posture |
|---|---|---|---|---|
| **Windows** | Dual-layer: AppPrivacy Group Policy + verified PnP device node disable (`CfgMgr32.dll`) | Dual-layer: AppPrivacy Group Policy + verified PnP node disable + Core Audio endpoint mute lock | Fully implemented via `WindowsPrivacySessionPlatformAdapter` and WAL journal | **Supported** (Primary production candidate) |
| **Linux** | Unsupported (`CapabilityLevel.None`) | Unsupported (`CapabilityLevel.None`) | `UnsupportedPrivacySessionPlatformAdapter` | **Discovery & UI Only** (Mutation experimental/unsupported) |
| **macOS** | Unsupported (`CapabilityLevel.None`; TCC cannot silently revoke permissions) | Unsupported (`CapabilityLevel.None`) | `UnsupportedPrivacySessionPlatformAdapter` | **Discovery & UI Only** (Mutation experimental/unsupported) |

---

## 7. Known audit status & resolved findings

- **Cross-Platform CI Topology**: Resolved. CI split separates shared tests (Domain, Application, Infrastructure) from Windows-only platform tests.
- **System Tray Localization**: Resolved. Dynamic localized headers for tray items ("Abrir PrivGvard" / "Open PrivGvard", "Salir" / "Exit") update on language change.
- **Title-Bar Close Button Styling**: Resolved. Fluent Dark hover/pressed states with distinct red background and white glyph.
- **Executable & Metadata Branding**: Resolved. Active desktop binary compiles as `PrivGvard.exe` / `PrivGvard` with official metadata.
- **Storage Migration**: Resolved. `StorageMigrationHelper` safely migrates `%LOCALAPPDATA%\PrivLock` data to `%LOCALAPPDATA%\PrivGvard`.
- **Legacy Quarantine**: Resolved. Archived WPF code quarantined under `legacy/CamMicBlocker` with compile-time build guard.
- **Linux & macOS Mutation Guarding**: Maintained. Capability providers explicitly declare `CapabilityLevel.None` and register `UnsupportedPrivacySessionPlatformAdapter`.
- **Volatile Test Counts**: Removed from canonical documentation in favor of dynamic test suite validation.

---

## 8. Documentation precedence

When documentation and code conflict, follow this strict hierarchy:

1. **Current source code and passing tests** (CODE WINS; TESTS WIN).
2. **This file ([PROJECT_CONTEXT.md](PROJECT_CONTEXT.md))** and root safety rules ([AGENTS.md](../../AGENTS.md)).
3. **[AUDIT-ROADMAP.md](AUDIT-ROADMAP.md)** and **[recovery-validation.md](../recovery-validation.md)**.
4. **Public README** ([README.md](../../README.md)).

AI agents must identify and flag contradictions explicitly rather than assuming outdated statements are correct.
