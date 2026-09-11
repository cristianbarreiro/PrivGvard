---
schema: privgvard.project-context.v1
kind: canonical-open-knowledge
product_name: PrivGvard
repository_name: CamMicroBlocker
current_code_identity: PrivLock
status_date: 2026-09-06
source_of_truth: repository-code-and-tests
primary_supported_platform: Windows
target_platforms: [Windows, Linux, macOS]
---

# PrivGvard — Canonical Project Context (OKF)

## 1. Purpose and product identity

PrivGvard is intended to be a transparent, reversible privacy utility for controlling access to a user's camera and microphone on Windows, Linux and macOS. The product must improve user control without modifying firmware, device registers, kernel binaries, security bypasses or undocumented hardware state.

The requested product name is **PrivGvard**. The current repository is still named `Cam&MicroBlocker`, the solution is `CamMicBlocker.sln`, namespaces and assemblies use `PrivLock`, and the Windows executable is `PrivLock.dll`/`PrivLock.exe` depending on build output. This naming mismatch is a documentation and branding migration item, not an instruction to rename code automatically.

## 2. Current verified state

| Area | Verified truth | Confidence |
|---|---|---|
| Runtime | .NET 10, Avalonia 11.2.3, single desktop host at `src/PrivLock.Desktop` | High |
| Architecture | Domain, abstractions, common infrastructure, application, platform providers, UI and desktop composition root | High |
| Windows | Durable privacy session, JSON journal, ownership attestation, authenticated on-demand privileged worker, PnP/registry/audio orchestration and recovery tests exist | High for unit/integration simulation; hardware/UAC behavior still needs isolated VM acceptance |
| Linux | Detector/controller scaffolding exists, but capability provider reports `None`; persistent recovery adapter is unsupported | High |
| macOS | Detector/controller scaffolding exists, but capability provider reports `None`; persistent recovery adapter is unsupported; secure provider methods are currently no-op success paths | High |
| Tests | Latest local run: 156 passed, 0 failed, 0 skipped (25 Domain, 30 Infrastructure, 58 Application, 43 Windows) | High |
| CI | GitHub Actions defines Windows/Ubuntu/macOS build-test matrix and selected publish targets | High; native privacy behavior is not proven by CI |
| Legacy code | Archived at `legacy/CamMicBlocker` and `legacy/tests/CamMicBlocker.Tests`; build-guarded, not part of active solution | High |

### Active Host vs Legacy Quarantine
- **ACTIVE HOST**: `src/PrivLock.Desktop` (produces `PrivGvard.exe`).
- **LEGACY**: `legacy/CamMicBlocker` (archived PrivLock 1.x implementation with build guard).
- **NORMAL BUILDS MUST NEVER BUILD LEGACY CODE.**

The current build succeeds with `dotnet build CamMicBlocker.sln --no-restore`. A concurrent restore attempt can fail because of a NuGet scratch-lock collision; that is an environment/parallelism issue and must not be reported as a product code failure without reproducing a serialized restore.

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
├── PrivLock.UI/                      # Avalonia views, view models and localization
└── PrivLock.Desktop/                 # Single executable host, DI composition and worker entry point

tests/
├── PrivLock.Domain.Tests/
├── PrivLock.Infrastructure.Tests/
├── PrivLock.Application.Tests/
└── PrivLock.Platform.Windows.Tests/
```

The application layer serializes operations through `ProtectionService`. Windows mutations are prepared in durable storage before native dispatch, and recovery is attempted on startup and during shutdown. The UI must consume observed state; desired state is not proof of effective protection.

## 4. Safety invariants

These invariants are mandatory for every implementation and every AI-generated change:

1. Start unelevated (`asInvoker`) and request elevation only for the exact operation that needs it.
2. Keep one application binary. Do not create a permanent elevated daemon or a second `PrivLock.Elevated.exe` binary.
3. Never touch firmware, EEPROM, device registers, kernel driver binaries, SIP, UAC bypass paths, Defender or code-signing enforcement.
4. Never claim `Blocked`, `Protected`, `Secure` or equivalent unless a fresh native observation verifies the effective state.
5. Capture the exact original state before mutation: existence, type, value, permissions, mute/volume or device problem state as applicable.
6. Journal intent before mutation; record per-resource progress; verify after mutation; preserve conflicts instead of overwriting external changes.
7. Restore only resources and values proven to be owned by the current PrivGvard session. Never perform broad “enable all”, “unmute all”, “chmod 660” or “delete policy” cleanup.
8. Handle partial failure, cancellation, UAC/Polkit denial, timeout, crash, power loss, hotplug and storage failure as explicit states.
9. Never turn a native command failure, missing command, timeout or verification failure into `OperationResult.Ok()`.
10. Keep diagnostic logs useful but non-sensitive: use operation IDs and hashed resource identifiers; do not log raw device identifiers or user paths unless strictly necessary.

## 5. Capability truth by platform

| Platform | Camera | Microphone | Recovery | Release posture |
|---|---|---|---|---|
| Windows | Dual-layer design: AppPrivacy policy plus verified PnP state | Dual-layer design: policy plus verified PnP/audio endpoint state | Persistent and journaled through `WindowsPrivacySessionPlatformAdapter` | Primary development and validation target |
| Linux | No supported production mutation yet | No supported production mutation yet | `UnsupportedPrivacySessionPlatformAdapter` | Discovery/UI only until exact reversible adapters exist |
| macOS | TCC/AVFoundation can report capability; application cannot silently revoke arbitrary app consent | CoreAudio can be used only with exact per-endpoint state capture/restore | `UnsupportedPrivacySessionPlatformAdapter` | Discovery/UI only until exact reversible adapters exist |

The Linux and macOS controller classes contain experimental mutation code. That code is not a supported capability and must not be enabled merely because a method exists.

## 6. Known audit findings

- Linux camera control currently uses shell `chmod` and a hard-coded `660` restore, does not capture original mode/ACL/owner, does not verify the command result before reporting success, and has a silent catch in the process helper.
- Linux microphone control attempts `wpctl` and `pactl`, treats one success as sufficient, and the provider logs/returns success without a verified effective-state readback in the production recovery model.
- macOS microphone restoration sets input volume to `75` instead of restoring the user's captured value. macOS secure enable/disable methods currently return success without enforcing a mechanism.
- Linux/macOS capabilities are `None` and persistent recovery is unsupported, but the provider methods remain present; future changes must preserve a hard safety gate until their adapters are complete.
- README/agent text previously claimed 61 tests and broad cross-platform protection. The verified baseline is 156 tests and Windows-first support.
- Product branding (`PrivGvard`) and implementation identity (`PrivLock`) are not yet unified.

Full severity, evidence and sequencing are in [AUDIT-ROADMAP.md](AUDIT-ROADMAP.md).

## 7. Documentation precedence

When context conflicts, use this order:

1. Current source code and passing tests.
2. This file and the security rules in the root `AGENTS.md`.
3. [AUDIT-ROADMAP.md](AUDIT-ROADMAP.md) and [recovery-validation.md](../recovery-validation.md).
4. README marketing or historical descriptions.

An AI must flag a contradiction rather than silently choosing the more optimistic statement.

## 8. Required engineering loop

For each requested change:

1. Classify it as documentation/UI/application/native/high-risk.
2. Identify the affected resource identity and exact original state.
3. Explain the mutation, observation and rollback paths before editing native code.
4. Implement the smallest reversible change; keep unsupported platforms read-only.
5. Add or update tests for success, partial failure, external conflict, missing device, permission denial and recovery.
6. Run the proportional build/test/publish validation and report what was not executable on the current host.
7. Update this context or the roadmap when the verified platform boundary changes.

## 9. Non-goals

Do not add a kernel driver, firmware integration, permanent privileged service, security bypass, remote-control channel, telemetry that identifies devices, or claims that a software mute is equivalent to physically disconnecting a sensor.
