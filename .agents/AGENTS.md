# PrivLock — Project Guidelines & Security Rules

Refer to the primary project documentation in `AGENTS.md` at the workspace root for complete architectural guidelines, build instructions, 9-point risk assessment checklists, capabilities matrix, and security policies.

## Quick Architecture Summary
- **Single Application Model**: One single binary (`PrivLock.exe` / `PrivLock`) running as standard user (`asInvoker`). No separate elevated executable.
- **Dynamic On-Demand Elevation**: Administrative rights requested strictly at the moment a privileged operation is executed (via short-lived self-invocation `--privileged-exec`), or directly in-process if already elevated.
- **Target**: .NET 10 / C# (Cross-Platform)
- **UI Framework**: Avalonia UI 11+ (Fluent Dark Theme, System Tray, Custom Window Chrome)
- **Architecture**: Clean Architecture (Domain, Platform.Abstractions, Infrastructure.Common, Application, Native Platform Adapters, Avalonia UI, Desktop Host)
- **Platform Implementations**:
  - **Windows**: `CfgMgr32.dll` PnP Hardware Controller + `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` Group Policies + WMI GUID Detection.
  - **Linux**: V4L2/sysfs device node control & ACLs + PipeWire (`wpctl`) / PulseAudio (`pactl`) sound server source lock + Polkit (`pkexec`).
  - **macOS**: CoreAudio HAL Hardware Input Mute (`AudioObjectSetPropertyData`) + AVFoundation state inspection + LaunchAgents plist.
- **Security & Capabilities**: Declarative `PlatformCapabilities` (honest security reporting, fail securely, verified state).
- **Logging & Diagnostics**: Serilog rolling logs + structured JSON crash reports in `%LOCALAPPDATA%\PrivLock\` (Windows), `~/.local/share/PrivLock/` (Linux), `~/Library/Application Support/PrivLock/` (macOS).
- **Localization**: Pure C# `LocalizationCatalog` (`StringsEn`/`StringsEs`) with dynamic UI binding.
