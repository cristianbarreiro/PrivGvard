<p align="center">
  <img src="assets/logo/cammicroblocker_logo.png" alt="PrivGvard Logo" width="128" />
  <h1 align="center">PrivGvard — Privacy Control for Camera &amp; Microphone</h1>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Platform Support" />
  <img src="https://img.shields.io/badge/Framework-.NET%2010.0%20%7C%20Avalonia%20UI-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10 & Avalonia UI" />
  <img src="https://img.shields.io/badge/Security-Least%20Privilege%20%7C%20On--Demand%20Elevation-4CAF50?style=for-the-badge&logo=security&logoColor=white" alt="Security" />
  <img src="https://img.shields.io/badge/License-GNU%20GPLv3-0078D4?style=for-the-badge&logo=gnu" alt="GPLv3 License" />
  <img src="https://img.shields.io/badge/Language-Español%20%7C%20English-007ACC?style=for-the-badge" alt="i18n Support" />
</p>

**PrivGvard** is intended to be a native, transparent privacy utility for **Windows**, **Linux**, and **macOS** (C# / .NET 10 and Avalonia UI). The current verified implementation is Windows-first: it focuses on exact state capture, on-demand authorization, effective-state verification and journaled recovery. Linux and macOS remain discovery/UI scaffolds until their native recovery adapters are complete.

The product name is **PrivGvard** (executable: `PrivGvard.exe`, storage: `%LOCALAPPDATA%\PrivGvard`). The internal C# namespaces and solution retain historical identifiers for architectural stability and seamless upgrade compatibility with previous versions.

PrivGvard follows the **Principle of Least Privilege**: it runs as **one single application** with standard user permissions by default, elevating privileges **only on-demand** for an explicitly authorized operation.

> **Support boundary:** compiling a Linux or macOS artifact does not mean that camera/microphone mutation is supported on that platform. The capability providers currently report `None`, and persistent recovery is not implemented there.

---

## 📦 Releases & Downloads

<div align="center">

| Platform | Format | Architecture | Download Link |
| :--- | :---: | :---: | :---: |
| 🪟 **Windows** | Portable Single-File / Setup target | `win-x64`, `win-arm64` | Release support candidate; validate the published release notes |
| 🐧 **Linux** | Build/publish target | `linux-x64`, `linux-arm64` | Discovery/UI only; privacy mutation not supported yet |
| 🍎 **macOS** | Build/publish target | `osx-arm64`, `osx-x64` | Discovery/UI only; privacy mutation not supported yet |

</div>

---

## ✨ Key Features

- 🛡️ **Capability-aware protection**:
  - **Windows (current primary target)**:
    1. *Policy layer*: captures and controls the supported AppPrivacy values with exact original value/type/existence tracking.
    2. *Device/audio layer*: uses verified PnP and capture-endpoint state through the authenticated, short-lived elevated worker.
    3. *Recovery layer*: persists per-resource intent and restores only confirmed PrivGvard-owned changes.
  - **Linux (roadmap)**: discovery and experimental controller code exist, but production capabilities are `None`. No camera/microphone privacy mutation should be advertised until exact PipeWire/V4L2 state capture and recovery are implemented.
  - **macOS (roadmap)**: discovery and experimental CoreAudio code exist, but production capabilities are `None`. TCC is an OS privacy boundary, not a silent revoke API; only verified, reversible capabilities may be enabled.
- ⚡ **Single Application & Dynamic On-Demand Elevation**:
  - Starts as a standard user process (`asInvoker`).
  - On Windows, the authenticated worker requests UAC **strictly on-demand** for the exact journaled operation. Linux/macOS authorization is roadmap work and must not be inferred from the presence of an elevation provider.
  - No separate `PrivLock.Elevated.exe` binary — everything is self-contained.
- 🎯 **Transparent Capabilities Model**:
  - PrivGvard exposes supported, read-only, unknown and unsupported states rather than inferring protection from a requested setting.
- 🎨 **Modern Fluent Dark UI Design**:
  - Avalonia UI 11 with integrated custom title bar (38px), rounded card containers, and responsive layout.
- 🌐 **Dynamic Multilingual Support (ES / EN)**:
  - Real-time segmented `[ ES | EN ]` language switcher with zero app restart needed.
- ⌨️ **Global Keyboard Shortcut**: Toggle instant protection at any time with **`Ctrl + Alt + B`**.
- 📌 **System Tray & Autostart Integration**:
  - Minimizes seamlessly to the system notification area on close `(X)`.
  - Native autostart support across Windows (`Run` key), Linux (`~/.config/autostart`), and macOS (`LaunchAgents`).

---

## 📐 Clean Architecture & Project Structure

The codebase is organized following **Clean Architecture (Domain-Driven Design + Strategy Pattern)**:

```text
PrivLock/
├── src/
│   ├── PrivLock.Domain/                  # Pure C# domain models, capabilities, and value objects
│   ├── PrivLock.Platform.Abstractions/   # Platform contracts (IDeviceProtectionProvider, IDeviceDetector, etc.)
│   ├── PrivLock.Infrastructure.Common/   # Cross-platform JSON state store, Serilog logging, CrashReporter
│   ├── PrivLock.Application/             # Orchestration services (ProtectionService, Settings, Localization)
│   ├── PrivLock.Platform.Windows/        # Windows CfgMgr32 PnP, WMI GUIDs, HKLM Registry policies, on-demand UAC
│   ├── PrivLock.Platform.Linux/          # Linux V4L2 device nodes, PipeWire/PulseAudio source control
│   ├── PrivLock.Platform.MacOS/          # macOS CoreAudio HAL input mute, AVFoundation, LaunchAgents
│   ├── PrivLock.UI/                      # Multiplatform Avalonia UI 11 Views & ViewModels
│   └── PrivLock.Desktop/                 # Single Executable Host & Platform Dependency Injection
│
├── tests/
│   ├── PrivLock.Domain.Tests/            # Domain unit tests
│   ├── PrivLock.Infrastructure.Tests/    # Storage, crash reporting & localization tests
│   ├── PrivLock.Application.Tests/       # Orchestration, on-demand elevation & business logic tests
│   └── CamMicBlocker.Tests/              # Legacy compatibility tests; not part of the active solution
│
└── .github/workflows/
    └── ci.yml                            # GitHub Actions CI matrix (Windows, Ubuntu, macOS)
```

---

## 💻 Building from Source

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Clone & Build Solution
```powershell
git clone https://github.com/cristianbarreiro/PrivGvard.git
cd PrivGvard
dotnet build CamMicBlocker.sln
```

### 2. Run Test Suite (171 tests in the current local baseline)
```powershell
dotnet test CamMicBlocker.sln
```

### 3. Run Application (Debug)
```powershell
dotnet run --project src/PrivLock.Desktop/PrivLock.Desktop.csproj
```

### 4. Publish Single-File Executables

```powershell
# Windows x64:
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish_out/win-x64

# Linux x64:
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish_out/linux-x64

# macOS ARM64 (Apple Silicon):
dotnet publish src/PrivLock.Desktop/PrivLock.Desktop.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish_out/osx-arm64
```

---

## 🛡️ Security & Observability

- **Least Privilege Architecture**: Runs with standard user permissions by default (`asInvoker`), elevating privileges transiently only during an authorized Windows operation.
- **Structured Diagnostic Logs**:
  - Windows: `%LOCALAPPDATA%\PrivGvard\Logs\PrivGvard-yyyyMMdd.log` (automatically migrates legacy `%LOCALAPPDATA%\PrivLock`)
  - Linux: `~/.local/share/PrivGvard/Logs/privgvard-yyyyMMdd.log`
  - macOS: `~/Library/Application Support/PrivGvard/Logs/privgvard-yyyyMMdd.log`
- **Post-Mortem Crash Reports**: Structured JSON reports generated in `.../PrivGvard/CrashReports/` on unhandled exceptions.
- **Fail-Secure Architecture**: The application must not report a device as protected without a fresh effective-state observation. Windows has the current recovery-backed path; Linux/macOS remain unsupported for production privacy mutation.

For the full audit, capability matrix and staged roadmap, see [docs/ai/PROJECT_CONTEXT.md](docs/ai/PROJECT_CONTEXT.md) and [docs/ai/AUDIT-ROADMAP.md](docs/ai/AUDIT-ROADMAP.md).

---

## 📄 License

PrivGvard is free and open-source software released under the **GNU General Public License v3.0 (GPL-3.0)**.

```text
Copyright (C) 2026 Cristian Barreiro
Repository: https://github.com/cristianbarreiro/PrivGvard.git
```

For complete license terms, legal notices, and third-party dependency disclosures, see [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
