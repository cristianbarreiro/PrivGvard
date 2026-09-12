# PrivGvard — Gemini Repository Instructions

Antes de responder o editar, lee en este orden:

1. `docs/ai/PROJECT_CONTEXT.md` — verdad canónica del proyecto.
2. `docs/ai/AUDIT-ROADMAP.md` — hallazgos, prioridades y plan aprobado.
3. `docs/ai/SENIOR-ENGINEER-PROMPT.md` — prompt y contrato de salida.
4. `AGENTS.md` — reglas de seguridad y arquitectura para agentes.

Reglas esenciales:

- El producto solicitado se llama PrivGvard, pero el código activo usa PrivLock y el repositorio Cam&MicroBlocker. No renombres globalmente sin una tarea de branding explícita.
- Windows es la única plataforma con recuperación persistente implementada. Linux y macOS son scaffolds con capacidad `None`; mantén sus mutaciones bloqueadas o explícitamente `Unsupported`.
- Nunca reportes una cámara o un micrófono como protegido sin una observación nativa posterior.
- No uses firmware, drivers propios, bypasses de seguridad, servicios elevados permanentes ni restauraciones con valores fijos.
- Para cada cambio nativo explica snapshot, journal, verificación, rollback, hotplug y denegación de permisos.
- Verifica documentación contra código y tests. La línea base actual es 156 tests locales, no 61.

Para una tarea concreta, aplica el prompt de `docs/ai/SENIOR-ENGINEER-PROMPT.md` sustituyendo `{{TASK}}` por la solicitud del usuario y devuelve resultado, evidencia, riesgos residuales y próximo paso seguro.
