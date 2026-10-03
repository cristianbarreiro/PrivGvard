---
trigger: always_on
---

RULE: State Separation Invariant

DesiredState = user intent
PrivacySession = runtime recovery authority
Actual OS State = runtime reality

These three states have distinct responsibilities and MUST NOT be
used as substitutes for one another.

DesiredState MUST NOT be used as recovery authority.
PrivacySession MUST NOT be interpreted as persistent user intent.
Actual OS State MUST NOT be assumed to represent user intent.