# PrivGvard — Claude Code Instructions

## Contexto obligatorio

Lee primero en este orden:
1. `docs/ai/PROJECT_CONTEXT.md` — Fuente canónica de verdad técnica (OKF).
2. `docs/ai/AUDIT-ROADMAP.md` — Estado de auditoría, deudas técnicas y roadmap.
3. `docs/ai/SENIOR-ENGINEER-PROMPT.md` — Contrato y formato de entrega de ingeniería.
4. `AGENTS.md` — Reglas de seguridad y arquitectura del repositorio.

Este archivo actúa exclusivamente como adaptador de flujo ligero para Claude.

## Reglas de trabajo

- **Solución activa**: `src/PrivLock.*` y host `src/PrivLock.Desktop` (compila como `PrivGvard.exe` en Windows, `PrivGvard` en Linux/macOS). El código heredado está archivado y protegido bajo `legacy/CamMicBlocker`.
- **Identidad del producto**: **PrivGvard** es la marca y nombre de producto oficial. Los espacios de nombres `PrivLock.*` y la solución `CamMicBlocker.sln` se mantienen por estabilidad arquitectónica y compatibilidad. No realices renames globales.
- **Fronteras de plataforma**: Windows es la única plataforma con mutación y recuperación persistente en producción. Linux y macOS son scaffolds de enumeración con `CapabilityLevel.None`; mantén sus operaciones de mutación bloqueadas o como `Unsupported`.
- **Invariante de protección**: No conviertas llamadas exitosas a APIs, comandos de terminal o desired state en protección efectiva. Exige siempre una observación nativa posterior (`EffectiveStatus`).
- **Cambios de alto riesgo**: Cualquier cambio en PnP, Registry, Audio HAL, elevación o arranque requiere la evaluación de 9 puntos de `AGENTS.md`.
- **Topología de pruebas**: Pruebas de Dominio, Aplicación e Infraestructura corren en todas las plataformas; pruebas de Windows corren solo en Windows. No hardcodees recuentos fijos de pruebas.
- **Ciclo de vida UI**: El botón cerrar (X) y Alt+F4 minimizan/ocultan al System Tray; la salida definitiva ocurre vía menú de bandeja con restauración coordinada de sesión.

## Formato de entrega

Finaliza con: estado, alcance, archivos modificados, validación ejecutada, riesgos residuales y próximo paso seguro (utiliza `docs/ai/SENIOR-ENGINEER-PROMPT.md` para tareas complejas de auditoría o implementación).
