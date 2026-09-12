# PrivGvard — Auditoría de contexto, seguridad y plan de evolución

**Fecha de auditoría:** 2026-09-06  
**Repositorio auditado:** `Cam&MicroBlocker`  
**Rama observada:** `dev`  
**Producto solicitado:** PrivGvard  
**Identidad actual del código:** PrivLock  
**Resultado ejecutivo:** Windows es la única ruta que debe tratarse como candidata a producción. Linux y macOS deben permanecer en modo descubrimiento/UI hasta contar con captura exacta, verificación efectiva, recuperación persistente y pruebas nativas aisladas.

## 1. Alcance y método

Se revisaron:

- `AGENTS.md`, `README.md` y `docs/recovery-validation.md`.
- La solución `CamMicBlocker.sln`, el host `src/PrivLock.Desktop` y la composición de dependencias.
- Servicios de aplicación, modelos de estado, almacenamiento, journal/recovery y cierre coordinado.
- Proveedores Windows, Linux y macOS, con especial atención a cualquier mutación del sistema.
- Tests, workflow de CI y la separación del árbol heredado `src/CamMicBlocker`.
- Compilación y suite local.

Comandos de validación ejecutados:

```text
dotnet build CamMicBlocker.sln --no-restore       -> correcto, 0 advertencias, 0 errores
dotnet test CamMicBlocker.sln --no-restore        -> 156 correctas, 0 errores, 0 omitidas
```

El primer `dotnet build` con restore se ejecutó en paralelo con `dotnet test` y recibió un bloqueo temporal de `NuGetScratch`. No se considera un fallo del producto porque la compilación serial sin restore terminó correctamente.

## 2. Hallazgos de documentación

| Prioridad | Hallazgo | Evidencia | Acción |
|---|---|---|---|
| P1 | El README presenta Windows, Linux y macOS como protección reversible disponible | `README.md` afirma bloqueo “100% reversible” y describe mutaciones para los tres sistemas | Cambiar a Windows-first; marcar Linux/macOS como roadmap y no soporte de seguridad |
| P1 | El README y el AGENTS antiguo indicaban 61 tests | La suite real suma 25 + 30 + 58 + 43 = 156 | Usar 156 como baseline y evitar números manuales sin actualizar |
| P1 | El nombre solicitado es PrivGvard, pero la implementación sigue siendo PrivLock/CamMicBlocker | Solution, namespaces, assembly, ejecutable, assets y paths lo confirman | Mantener alias documentado hasta aprobar una migración de branding |
| P1 | `docs/recovery-validation.md` es sólido para Windows, pero no identifica con suficiente visibilidad que otros proveedores son scaffolds | DI registra `UnsupportedPrivacySessionPlatformAdapter` en Linux/macOS | Añadir matriz de capacidades y una regla de no-dispatch |
| P2 | El workflow publica artefactos Linux/macOS aunque su capacidad de privacidad es `None` | `.github/workflows/ci.yml` compila/publica por RID, no valida seguridad nativa | Separar “artefacto compilable” de “plataforma soportada” en release gates |
| P2 | README contiene enlaces y nombres de release que no fueron verificados como parte de esta auditoría | Auditoría local, sin comprobación de remoto | No presentar descargas como evidencia de soporte hasta tener release checklist |

## 3. Hallazgos técnicos y de seguridad

### P0 — No liberar soporte de privacidad Linux/macOS todavía

Los capability providers de Linux y macOS exponen `CapabilityLevel.None` y la composición usa `UnsupportedPrivacySessionPlatformAdapter`. Eso es coherente con no prometer seguridad. Sin embargo, las clases de protección siguen conteniendo métodos de mutación que una futura refactorización podría activar accidentalmente.

**Regla inmediata:** mientras no exista un adaptador de recuperación exacta, cualquier operación de cámara/micrófono en Linux/macOS debe ser explícitamente `Unsupported` o de solo lectura. No basta con desactivar un botón en la UI.

### P0 — Linux: permisos y restauración no son reversibles

`src/PrivLock.Platform.Linux/Devices/LinuxDeviceController.cs`:

- Bloquea con `chmod 000` y restaura con `chmod 660` sin capturar modo, propietario, grupo, ACL, etiqueta de seguridad ni reglas de udev originales.
- Marca cada recurso como exitoso aunque el código de proceso no haya devuelto éxito.
- El helper de procesos tiene `catch` silencioso.
- La detección de accesibilidad del nodo no prueba que el dispositivo esté habilitado ni que la aplicación haya eliminado el acceso efectivo.

Esto puede dejar el sistema con permisos modificados o comunicar una falsa protección. Debe eliminarse o aislarse detrás de una implementación nueva que modele el estado original y verifique la observación posterior.

### P0 — Linux: mutaciones de audio sin recuperación exacta

El controlador intenta `wpctl` y `pactl` sobre la fuente por defecto, pero no captura el endpoint exacto, mute original, volumen, ruta ni cambios de default source. Que uno de los dos comandos termine correctamente no prueba que todos los procesos hayan perdido acceso al micrófono. Tampoco hay journal persistente de esta ruta.

La implementación futura debe elegir una API primaria, identificar recursos estables, capturar/restaurar solo el estado propio y devolver `Unknown` cuando no pueda verificarlo.

### P0 — macOS: restauración destructiva y secure no-op

`src/PrivLock.Platform.MacOS/Devices/MacOSDeviceController.cs` restaura el volumen de entrada a `75`, no al valor original capturado. `MacOSProtectionProvider.EnableSecureProtectionAsync` y `DisableSecureProtectionAsync` retornan éxito sin aplicar una barrera nativa. Ambas cosas son incompatibles con la promesa de reversibilidad y con “no informar protegido sin verificar”.

Además, TCC no debe describirse como una API que PrivGvard pueda usar para revocar silenciosamente el permiso de cámara de otras aplicaciones. En macOS la capacidad puede ser inspeccionada y guiada al usuario; el bloqueo efectivo debe definirse con límites explícitos.

### P1 — Contrato de resultado demasiado permisivo para proveedores experimentales

Los proveedores deben distinguir como mínimo: `Success`, `Unsupported`, `Denied`, `Failed`, `Unknown` y `OutcomeUncertain`. Un `OperationResult.Ok()` sin observación efectiva permite que una implementación incompleta contamine desired state y UI.

### P1 — Pruebas nativas insuficientes

Los tests actuales son valiosos para el dominio, persistencia, journal, protocolo Windows y fallos simulados, pero no prueban hardware real, UAC, Polkit, logout, shutdown, hotplug, TCC, CoreAudio, PipeWire o permisos de nodos. La documentación ya lo reconoce para Windows; debe quedar igual de visible en el release gate multiplataforma.

### P1 — Seguridad operacional del flujo de procesos externos

Linux/macOS usan argumentos de proceso como cadenas y dependen de comandos externos. Antes de usar ese patrón en producción hay que exigir resolución de ejecutable, argumentos estructurados, timeout que mate el proceso, captura acotada de stderr/stdout, códigos de salida y una lectura posterior del estado. Nunca usar shell interpolation para identificadores de dispositivo.

### P2 — Identidad y artefactos históricos

El árbol heredado `src/CamMicBlocker` convive con `src/PrivLock.*`. La solución activa excluye el legado, pero un agente podría editar el árbol equivocado. La documentación debe indicarlo y una tarea futura debe decidir si se archiva, se elimina o se marca claramente como no mantenido.

## 4. Estado de la arquitectura

### Fortalezas que conviene conservar

- Host único y proceso de usuario no elevado.
- Worker Windows autenticado, de vida corta, con whitelist y verificación de PID/nonce/pipe.
- Journal persistente con estados por recurso, conflictos y recovery pass.
- Separación de dominio, aplicación, abstracciones y plataformas.
- Barrera de serialización para operaciones, shutdown y recuperación.
- Estado efectivo separado de desired state.
- Diagnósticos estructurados y pruebas de fallos de almacenamiento/rollback.

### Deudas que bloquean soporte multiplataforma

- Falta un contrato común de “capturar → preparar → aplicar → observar → restaurar → observar”.
- Los identificadores de recurso y los snapshots no tienen una semántica equivalente entre Windows, Linux y macOS.
- Linux y macOS no tienen adaptadores persistentes registrados en producción.
- Capabilities no modela suficientemente la diferencia entre soporte de descubrimiento, mute local, bloqueo de acceso y recuperación.
- La UI puede mostrar conceptos de Standard/Secure comunes aunque el proveedor actual no tenga una observación verificable.

## 5. Plan de mejora por fases

### Fase 0 — Documentación y cortafuegos de seguridad

**Objetivo:** impedir falsas promesas y regresiones mientras se evoluciona.

- Adoptar `docs/ai/PROJECT_CONTEXT.md` como contexto canónico.
- Mantener `AGENTS.md`, `GEMINI.md` y `CLAUDE.md` como adaptadores breves que apunten al contexto canónico.
- Corregir README y recovery docs para diferenciar “compila para un RID” de “protege de forma verificable”.
- Añadir un guard de producción que rechace mutaciones Linux/macOS si `SupportsPersistentRecovery` es `false` o la capacidad es `None`.
- Añadir tests que prueben que un proveedor `Unsupported` no cambia desired state ni ejecuta mutaciones nativas.

**Salida:** ningún agente puede habilitar por accidente una ruta experimental solo porque el método existe.

### Fase 1 — Contrato de recurso y resultados

**Objetivo:** hacer imposible confundir éxito de la llamada con protección efectiva.

- Definir un `PrivacyResourceSnapshot` por plataforma con identidad estable, estado original completo, estado protegido esperado y metadata de ownership.
- Definir observaciones explícitas: `Original`, `Protected`, `Missing`, `Conflict`, `Unknown`, `Error`.
- Hacer que cada operación de aplicación exija una observación posterior antes de persistir `Active`.
- Propagar `Unsupported`, `Denied`, `Failed` y `OutcomeUncertain` hasta UI y logs.
- Mantener el journal monotónico y sin limpieza global.

**Salida:** todas las plataformas comparten semántica, no implementación nativa.

### Fase 2 — Endurecimiento Windows

**Objetivo:** cerrar la ruta candidata a producción.

- Ejecutar la matriz manual de `docs/recovery-validation.md` en una VM Windows aislada con dispositivos desechables.
- Añadir pruebas de publicación, instalador, actualización y desinstalación segura.
- Validar diferencias Windows 10/11, x64/ARM64, dispositivos pre-desactivados y endpoints múltiples.
- Auditar que las policies no sean globales fuera del scope autorizado y que un conflicto externo nunca sea sobrescrito.
- Añadir un checklist de firma de código y procedencia de artefactos antes de distribuir.

**Salida:** release Windows con evidencia reproducible de recuperación y límites declarados.

### Fase 3 — Linux, primero observación y audio

**Objetivo:** implementar una capacidad pequeña y demostrable sin chmod destructivo.

- Empezar con detección y lectura efectiva de estado mediante PipeWire/WirePlumber o PulseAudio, eligiendo una ruta primaria por entorno.
- Capturar endpoint/node estable, mute, volumen y relación con la default source.
- Aplicar mute solo después de snapshot; restaurar únicamente el snapshot propio.
- Verificar después de aplicar y después de restaurar; si el servidor cambia, reportar conflicto.
- Cubrir reinicio del servidor, hotplug, cambio de default source, ausencia de DBus, permisos de sesión y comandos no instalados.
- Dejar la cámara fuera de soporte hasta definir una estrategia segura de sesión/udev/V4L2 que preserve permisos y no dependa de `chmod 660`.

**Salida:** soporte experimental documentado por entorno, nunca una promesa genérica “Linux”.

### Fase 4 — macOS, límites TCC y CoreAudio

**Objetivo:** ofrecer control honesto y reversible.

- Implementar enumeración real de dispositivos y endpoint IDs, no dispositivos built-in ficticios.
- Capturar/restaurar mute y volumen por dispositivo exacto; no usar valores fijos.
- Declarar TCC como inspección/guía al usuario, no como revocación silenciosa de permisos de terceros.
- Determinar si existe un mecanismo soportado para cámara; si no, mantener cámara en `Unsupported` con explicación clara.
- Probar cambios de dispositivo, sleep/wake, cambio de usuario, permisos de micrófono y CoreAudio daemon restart.

**Salida:** solo las capacidades que tengan semántica verificable reciben `CapabilityLevel.Software`.

### Fase 5 — UX, CI y release governance

**Objetivo:** que la interfaz y la distribución no sobreprometan.

- Mostrar por objetivo y plataforma: `Supported`, `Read-only`, `Unsupported`, `Unknown`, `Pending recovery`.
- No activar toggles cuando la capacidad no tenga adapter de recovery.
- Separar jobs de build/publish de jobs de soporte nativo y smoke tests.
- Publicar una matriz de evidencia por versión, no solo binarios.
- Automatizar el recuento de tests o eliminar el número fijo del README.
- Resolver la migración de marca PrivLock → PrivGvard con un plan de compatibilidad de rutas, datos y desinstalador.

## 6. Definition of Done para una plataforma

Una plataforma no puede declararse soportada solo porque compile. Debe cumplir todo lo siguiente:

- Detección real y estable del recurso.
- Snapshot exacto del estado original.
- Mutación oficial y acotada al recurso seleccionado.
- Observación efectiva posterior a aplicar y restaurar.
- Journal durable y ownership verificable.
- Rollback de fallo parcial, cancelación, timeout, crash y reinicio.
- Hotplug y recurso ausente tratados como estados pendientes, no como permiso para actuar ampliamente.
- Denegación de privilegios limpia y sin privilegios persistentes.
- Tests unitarios, simulación de fallos y aceptación nativa aislada.
- UI y README con claims iguales a la capacidad real.

## 7. Orden recomendado para el próximo trabajo

1. Implementar el guard de no-dispatch Linux/macOS y sus tests.
2. Corregir README y recovery-validation para que no prometan soporte que no existe.
3. Endurecer el contrato de resultado/observación.
4. Completar la matriz de aceptación Windows en VM.
5. Diseñar el adapter Linux de audio con snapshot exacto.
6. Diseñar el adapter macOS de audio y documentar el límite TCC.
7. Recién entonces evaluar cámara Linux/macOS y la migración de branding.

No se recomienda comenzar por una reescritura global, un driver propio o una renombrada masiva: aumentarían el riesgo sin resolver los límites de verificación y recuperación.
