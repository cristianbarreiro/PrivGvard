# Reversible privacy sessions: validation and limits

> **Support boundary (audited 2026-09-06):** this document describes the Windows recovery-backed path. Linux and macOS currently register `UnsupportedPrivacySessionPlatformAdapter` and expose no production privacy capability. Their experimental controllers must not be enabled or described as reversible protection until they implement the same capture/observe/restore contract.

The supported host is `src/PrivLock.Desktop`. The legacy WPF project is excluded
from the solution and installer. Windows recovery tracks original Registry value
existence, type and data, PnP instance/problem state, and per-endpoint microphone
mute state. JSON WAL commits precede native changes; a backup holds the same
committed revision. Machine changes additionally require an elevated-worker
ownership record bound to the requesting user and exact original transition.

## Termination behavior

| Event | Behavior |
| --- | --- |
| Window/tray exit | Centralized restoration; retryable failures cancel user exit. |
| Shutdown/logout | Bounded best-effort restoration; Windows may terminate first. |
| Recoverable fatal exception | Centralized best-effort restore and crash diagnostics. |
| TerminateProcess, FailFast, BSOD, power loss | No cleanup guarantee; recovery requires the next launch under the owning account, available devices/storage and any required UAC approval. |

Current state differing from both captured and applied values is preserved as a
conflict. An external write of the exact same protected value cannot always be
distinguished from PrivLock's write. Native Registry/PnP/audio APIs do not provide
an atomic transaction with the WAL or an atomic compare-exchange across these
resources; small comparison/write and durable-intent crash windows remain.

New devices are captured only by a subsequent protection operation. Unplugged
owned devices remain pending until a later recovery pass can observe them again.
No permanent elevated watchdog is installed. Linux/macOS mutations are disabled
in production until exact recovery adapters are implemented. A build or published
artifact for a Linux/macOS RID is not evidence of privacy capability.

## Nine-point risk assessment

1. Scope: Windows HKCU consent, HKLM AppPrivacy, PnP nodes and capture-endpoint mute;
   no firmware, driver binaries or security subsystem modifications.
2. Partial apply: per-resource results and WAL recovery; only newly owned changes
   participate in the failed operation's rollback.
3. Reversibility: exact captured values with final comparison/verification;
   external conflicts, missing devices and damaged journals limit restoration.
4. Reboot: durable journal and machine ownership survive; automatic recovery runs
   on next application start, not autonomously during boot.
5. Hotplug: stable resource identities; missing resources remain retryable.
6. UAC denial: ordinary operation failure; durable outstanding changes are retained.
7. Restricted token/storage: refuse unsafe writes; UI remains unelevated.
8. Existing driver problems: do not adopt or modify nodes outside clean/explicitly
   disabled states; pre-disabled devices are left disabled.
9. Diagnostics: session/operation IDs, hashed resource log IDs, structured crash
   summaries; explicit machine/user-name fields removed. Exception text may still
   contain paths and should be reviewed before sharing diagnostics.

## Windows integration acceptance (manual, not executed by automated unit tests)

Use an isolated Windows machine/VM with disposable camera/microphone devices and
record original values before each scenario. Do not run forced-kill/shutdown tests
on the development workstation.

| Scenario | Acceptance |
| --- | --- |
| Standard/Secure block then normal exit | Original Registry existence/type/data, PnP state and mute restored. |
| Pre-disabled camera/pre-muted microphone | Original disabled/muted state preserved. |
| Native partial apply or restore failure | Successful changes restored or retained as explicit retryable WAL work. |
| Kill after each apply/restore checkpoint, restart | No broad enable/delete; recover only matching recorded transitions. |
| External different-value modification | Conflict shown/logged; external value preserved. |
| Unplug/replug during protection and recovery | Missing remains pending; same identity recovers on a later pass. |
| UAC cancellation and alternate administrator credentials | No privilege retained; ownership stays with initiating account. |
| Block concurrently with window exit/logout | Operations serialized; no new block after shutdown admission. |
| Direct/wrapped uninstall with active state in another profile | Removal denied until recovery; malformed/unreadable state fails closed. |
| Installer update and visual tray/window smoke | Validate manually before release; installer compilation alone is insufficient. |

Unit tests use simulated native state and storage failures; real IPC peer PID
checks use local test pipes. They do not establish hardware/UAC/logout behavior.
