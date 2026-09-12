# PrivGvard — Agent Guidelines & Architecture Summary

This file serves as a lightweight workspace adapter. Refer to the canonical documentation and primary guidelines:

- **Canonical Context (OKF)**: [docs/ai/PROJECT_CONTEXT.md](../docs/ai/PROJECT_CONTEXT.md)
- **Repository Safety & Security Rules**: [AGENTS.md](../AGENTS.md)
- **Current Roadmap & Audit**: [docs/ai/AUDIT-ROADMAP.md](../docs/ai/AUDIT-ROADMAP.md)

---

## Quick Reference Summary

- **Product Identity**: **PrivGvard** (produces `PrivGvard.exe` / `PrivGvard`). Internal namespaces and solution retain `PrivLock.*` and `CamMicBlocker.sln`.
- **Single-Binary & Least Privilege**: The application runs unprivileged (`asInvoker`). Privileged actions execute transiently via self-invocation as an authenticated worker (`--privileged-worker`) with named-pipe IPC and 256-bit nonces. The legacy public `--privileged-exec` dispatcher is permanently removed.
- **Platform Boundaries**:
  - **Windows**: Full protection supported (CfgMgr32 PnP node toggle, AppPrivacy Group Policies, Core Audio mute lock, durable WAL session journal recovery).
  - **Linux & macOS**: Discovery and UI scaffolds (`CapabilityLevel.None`, `UnsupportedPrivacySessionPlatformAdapter`). Mutation is not supported in production.
- **Testing Topology**: Shared tests (Domain, Application, Infrastructure) execute across all platforms; Windows-specific tests execute only on Windows runners. Volatile test numbers must not be hardcoded into documentation.
- **Storage & Logs**: `%LOCALAPPDATA%\PrivGvard\` (automatically migrates legacy `%LOCALAPPDATA%\PrivLock\` data via `StorageMigrationHelper`).
- **UI Lifecycle**: Title-bar close button (`X`) and `Alt+F4` hide the main window to the System Tray; full exit with coordinated journal restoration is executed from the System Tray menu.
- **Active Host vs. Legacy**: Active code is in `src/PrivLock.*` with host `src/PrivLock.Desktop`. Legacy code is quarantined in `legacy/CamMicBlocker`. Normal builds must never touch legacy code.
