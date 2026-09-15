# PrivGvard — Microsoft Store Restricted Capabilities Justification

**Document Version:** 1.0.0  
**Date:** September 2026  
**Application Name:** PrivGvard  
**Package Identity:** `cdevStudio.PrivGvard`  
**Publisher:** `cdev Studio`  
**Target Platform:** Windows 10 (Build 19041+) and Windows 11 (x64, ARM64)  
**Submission Contact:** `reportapp@microsoft.com` / Partner Center Restricted Capabilities Review  

---

## 1. Executive Summary

PrivGvard is a native Windows privacy and security utility that gives users deterministic, verifiable control over their camera and microphone hardware. 

To fulfill its security promise without compromising system integrity, PrivGvard requires three restricted MSIX capabilities:

1. `rescap:runFullTrust`
2. `rescap:allowElevation`
3. `rescap:unvirtualizedResources`

PrivGvard follows Microsoft's **Principle of Least Privilege**: the main user interface runs strictly as a standard unprivileged user (`asInvoker`). Administrative privileges are requested **strictly on-demand** for short-lived (sub-second) hardware and policy toggles via a transient authenticated helper process, after which elevated rights are immediately released.

---

## 2. Capability Details & Technical Justifications

### 2.1 `rescap:allowElevation`

#### Purpose
Allows the application to launch an on-demand, elevated helper process (`PrivGvard.exe --privileged-worker`) via the standard Windows UAC dialog (`Verb="runas"`).

#### Why It Is Necessary
PrivGvard operates two layers of privacy protection:

1. **Standard Layer (No Elevation):**
   - Mutes Core Audio recording endpoints via Windows Core Audio COM APIs (`IMMDeviceEnumerator`, `IAudioEndpointVolume`).
   - Toggles current user privacy preferences under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore`.

2. **Secure Layer (Requires Administrator Privileges):**
   - **Hardware-Level PnP Device Disablement:** PrivGvard calls Windows Configuration Manager (`CfgMgr32.dll` APIs: `CM_Locate_DevNode_W`, `CM_Disable_DevNode`, and `CM_Enable_DevNode`) to place physical camera and microphone devices into the `DN_HAS_PROBLEM / CM_PROB_DISABLED` state. This prevents any kernel-mode driver or user-mode application from streaming sensor data.
   - **System-Wide Group Policy Enforcement:** PrivGvard writes Windows AppPrivacy Group Policies under `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` (`LetAppsAccessCamera = 2`, `LetAppsAccessMicrophone = 2`) to ensure Windows itself blocks application access across all user accounts.

Both `CfgMgr32.dll` device state manipulation and `HKLM` Group Policy modifications are privileged operations in Windows that fail with `ACCESS_DENIED` without Administrator rights.

#### Least-Privilege Architecture (Why Not a Background Service?)
Rather than installing a permanent background Windows Service running as `NT AUTHORITY\SYSTEM` (which would create an ongoing security attack surface), PrivGvard uses **transient on-demand elevation**:

- The main interactive application (`PrivGvard.exe`) runs as a standard user (`asInvoker`).
- When the user clicks "Protect" or "Unprotect" in the Secure Layer, PrivGvard launches its own executable with the argument `--privileged-worker` using `ShellExecuteEx` with `Verb="runas"`.
- The user is presented with the official Windows UAC consent dialog.
- The standard process and the elevated worker communicate over an in-memory **Named Pipe** secured with:
  - An explicit `PipeSecurity` Access Control List (ACL) granting access only to the current user SID and Administrators.
  - A cryptographically random 256-bit authentication nonce passed via CLI and validated before accepting any command.
  - Bilateral kernel process ID (PID) verification using `GetNamedPipeServerProcessId` / `GetNamedPipeClientProcessId`.
  - A strictly closed whitelist of 7 permitted operations (`apply-policy`, `restore-policy`, `apply-device`, `restore-device`, `verify-policy-ownership`, `verify-device-ownership`, `ping`).
- As soon as the hardware PnP state or policy is applied (typically under 200 ms), the worker process terminates. PrivGvard retains zero elevated privileges during normal execution.

---

### 2.2 `rescap:runFullTrust`

#### Purpose
Allows the application to run outside the AppContainer sandbox as a standard Win32 desktop bridge process.

#### Why It Is Necessary
1. **P/Invoke to Native System Libraries:** PrivGvard requires direct P/Invoke calls to `CfgMgr32.dll` (`CM_Locate_DevNode_W`, `CM_Disable_DevNode`, `CM_Enable_DevNode`, `CM_Get_DevNode_Status`) and `kernel32.dll`.
2. **COM Interop for Windows Core Audio:** PrivGvard enumerates active audio recording endpoints and toggles master mute states using native COM interfaces (`IMMDeviceEnumerator`, `IMMDevice`, `IAudioEndpointVolume`).
3. **WMI Hardware Detection:** PrivGvard queries `Win32_PnPEntity` via `System.Management` to discover plugged-in camera and microphone hardware and correlate them with PnP device IDs.
4. **Inter-Process Communication:** The application creates and manages local named pipes (`\\.\pipe\PrivLock_...`) to coordinate the standard UI process and the elevated worker.

None of these hardware management capabilities are available inside an AppContainer sandbox.

---

### 2.3 `rescap:unvirtualizedResources`

#### Purpose
Disables MSIX registry and file system virtualization for specific application paths, allowing direct access to `%LOCALAPPDATA%\PrivGvard` and machine-level state.

#### Why It Is Necessary
1. **Crash-Resilient Write-Ahead Logging (Recovery Journal):**
   PrivGvard maintains a durable, binary/JSON Write-Ahead Log in `%LOCALAPPDATA%\PrivGvard\Recovery\`. If the user's PC loses power, crashes, or reboots while devices are blocked, PrivGvard detects the pending session on next startup and automatically restores hardware devices to their normal functional state. Virtualizing this directory risks state isolation between updates or between different execution contexts (e.g., standard user vs. elevated worker during recovery), which could leave user hardware permanently disabled.
2. **Settings Persistence Across Package Updates:**
   User preferences stored in `%LOCALAPPDATA%\PrivGvard\state.json` must persist cleanly across MSIX package updates without being quarantined in an isolated container that is cleared upon package servicing.
3. **Machine Ownership Attestation:**
   PrivGvard records an ownership marker in `HKLM\SOFTWARE\PrivLock\PrivilegedOwnership\v2` so that one user cannot tamper with another user's session. This key must be real and unvirtualized in `HKLM`.

---

## 3. Threat Mitigation & Security Controls Summary

| Security Concern | PrivGvard Mitigation |
|---|---|
| Privilege Escalation | Main UI runs strictly unprivileged (`asInvoker`). Elevation requires explicit user approval on Windows UAC. Worker accepts only whitelisted commands. |
| Malicious IPC Injection | Named pipe uses random GUID name, restricted ACL, 256-bit nonce authentication, and kernel PID verification. |
| Hardware Brick / Permanent Lockout | Write-Ahead Log (WAL) journal guarantees clean state recovery even across abrupt crashes, uninstalls, or reboots. |
| Background Resource Drain | No persistent background service. Elevated worker exits immediately upon task completion. |
| Privacy & Telemetry | Zero data collection. No network sockets opened. 100% offline, local execution. |

---

## 4. Microsoft Store Certification Reviewer Notes

To test PrivGvard's elevated behavior during certification:

1. Launch PrivGvard from the Start Menu (launches as standard user).
2. Note that camera and microphone status is displayed accurately.
3. Toggle the protection switch for Camera or Microphone:
   - If using the **Standard Layer**, no UAC prompt appears; Core Audio mute is applied instantly.
   - If using the **Secure Layer**, the Windows UAC consent prompt (`PrivGvard`) will appear requesting permission to modify device hardware state.
4. Click "Yes" on UAC: The camera/microphone hardware is disabled via PnP and verified via native query.
5. Click "Unprotect": UAC prompt appears again, and hardware is safely restored.
6. Verify in Windows Device Manager (`devmgmt.msc`) that the camera device is cleanly disabled/enabled.
