# PrivGvard — Privacy Policy

**Effective Date:** September 15, 2026  
**Last Updated:** September 15, 2026  
**Developer:** cdev Studio  
**Application:** PrivGvard (Camera & Microphone Privacy Guard)  
**Website / Repository:** [https://github.com/cristianbarreiro/PrivGvard](https://github.com/cristianbarreiro/PrivGvard)  

---

## 1. Introduction

At **cdev Studio**, we believe privacy is a fundamental human right. **PrivGvard** is designed from the ground up to protect your privacy by giving you complete, transparent, and verifiable control over your camera and microphone hardware.

This Privacy Policy explains what information PrivGvard accesses, how that information is handled, and why your data never leaves your device.

---

## 2. Information We Do NOT Collect

PrivGvard is an offline, privacy-first desktop utility. **We do not collect, store, transmit, or share any personal information whatsoever.**

Specifically:
- **No Audio Recording or Listening:** PrivGvard does not record, intercept, process, sample, or transmit audio from your microphone. It only toggles the system-level mute state or disables the physical device node in Windows Configuration Manager.
- **No Video Capture or Photo Taking:** PrivGvard does not capture, record, preview, or stream video or images from your webcam. It controls only the operating system's device driver state and privacy policies.
- **No Personal Identifiers:** We do not collect names, email addresses, IP addresses, hardware serial numbers, or online identifiers.
- **No Telemetry or Usage Analytics:** PrivGvard contains zero telemetry frameworks, zero tracking pixels, zero analytics SDKs, and zero crash-reporting services that phone home.
- **No Advertising:** PrivGvard contains no ads and connects to no advertising networks.
- **No Network Traffic:** PrivGvard makes zero outbound network requests. It operates completely offline.

---

## 3. Operating System Permissions & Local Data

To function as a privacy guard, PrivGvard requires specific Windows system permissions and creates local files strictly on your device:

### 3.1 Device Enumeration and Hardware Control
PrivGvard inspects Windows Device Manager (`Win32_PnPEntity` and `CfgMgr32.dll`) to list available camera and microphone devices so you can see their current hardware status. With your consent (via Windows User Account Control), PrivGvard can disable or enable these device nodes at the PnP driver level.

### 3.2 Local Configuration Files
PrivGvard stores your preferences and session state locally on your computer in:
```text
%LOCALAPPDATA%\PrivGvard\state.json
```
This file contains only:
- Your selected UI language (e.g., English, Spanish).
- Your desired protection modes (Standard or Secure layer).
- Autostart preferences.

### 3.3 Recovery Journal (Crash Resilience)
PrivGvard stores an active session journal in:
```text
%LOCALAPPDATA%\PrivGvard\Recovery\
```
This journal records which devices were blocked by PrivGvard during the current session. If your computer crashes, experiences a power failure, or reboots while devices are blocked, PrivGvard reads this local journal upon startup to automatically unblock and restore your hardware to normal operation. This file is never shared or transmitted.

### 3.4 Diagnostic Logs
Local rolling diagnostic logs and crash reports may be saved in:
```text
%LOCALAPPDATA%\PrivGvard\Logs\
%LOCALAPPDATA%\PrivGvard\CrashReports\
```
These logs contain non-sensitive technical event information (such as timestamps, PnP operation return codes, and error messages) for troubleshooting. They are stored only on your machine and are never uploaded automatically.

---

## 4. Children’s Privacy

PrivGvard does not collect any data from any users, including children under the age of 13.

---

## 5. Open Source Transparency

PrivGvard is open-source software. You and independent security researchers can inspect, audit, and compile the entire source code at:
[https://github.com/cristianbarreiro/PrivGvard](https://github.com/cristianbarreiro/PrivGvard)

---

## 6. Contact Us

If you have questions or feedback regarding this Privacy Policy, you may reach us at:
- **GitHub Issues:** [https://github.com/cristianbarreiro/PrivGvard/issues](https://github.com/cristianbarreiro/PrivGvard/issues)
- **Publisher:** cdev Studio
