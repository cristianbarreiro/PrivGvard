# PrivGvard — Gemini Repository Instructions

Antes de responder o editar, lee en este orden:

1. `docs/ai/PROJECT_CONTEXT.md` — verdad canónica del proyecto (OKF).
2. `docs/ai/AUDIT-ROADMAP.md` — roadmap, prioridades y estado de auditoría.
3. `docs/ai/SENIOR-ENGINEER-PROMPT.md` — prompt reutilizable y contrato de salida.
4. `AGENTS.md` — reglas de seguridad y arquitectura del repositorio.

Reglas esenciales:

- **Identidad del Producto**: El producto oficial es **PrivGvard** (ejecutable `PrivGvard.exe` / `PrivGvard`). Los espacios de nombres internos (`PrivLock.*`) y la solución (`CamMicBlocker.sln`) se conservan estables por compatibilidad histórica; no renombres código internamente sin una instrucción explícita.
- **Soporte de Plataforma**: Windows es la única plataforma con mutación y recuperación persistente en producción. Linux y macOS son scaffolds de descubrimiento e interfaz (`CapabilityLevel.None`); mantén sus mutaciones bloqueadas o explícitamente `Unsupported`.
- **Topología de CI y Pruebas**: La suite se divide en pruebas compartidas (Dominio, Aplicación, Infraestructura) que corren en Windows/Ubuntu/macOS, y pruebas específicas de plataforma que corren únicamente en Windows. No hardcodees recuentos fijos de pruebas; consulta CI o el ejecutor local.
- **Invariante de Verificación**: Nunca reportes una cámara o un micrófono como protegido sin una observación nativa posterior (`EffectiveStatus`). `OperationResult.Ok()` o desired state no equivalen a protección efectiva.
- **Seguridad y Mínimo Privilegio**: No uses firmware, drivers propios, bypasses de seguridad, servicios elevados permanentes ni restauraciones con valores fijos. La elevación es estrictamente bajo demanda mediante worker efímero autenticado (`--privileged-worker`).
- **Ciclo de Vida de Ventana y Tray**: El botón cerrar (X) y Alt+F4 ocultan la ventana principal al System Tray sin terminar el proceso. El cierre definitivo se realiza desde la opción Salir del menú de bandeja mediante `ShutdownCoordinator`.

Para una tarea concreta, aplica el prompt de `docs/ai/SENIOR-ENGINEER-PROMPT.md` sustituyendo `{{TASK}}` por la solicitud del usuario y devuelve resultado, evidencia, riesgos residuales y próximo paso seguro.
