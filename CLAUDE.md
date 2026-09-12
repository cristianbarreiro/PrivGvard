# PrivGvard — Claude Code Instructions

## Contexto obligatorio

Lee primero `docs/ai/PROJECT_CONTEXT.md`, `docs/ai/AUDIT-ROADMAP.md`, `docs/ai/SENIOR-ENGINEER-PROMPT.md` y `AGENTS.md`. Esos documentos son complementarios: el contexto canónico contiene la verdad del sistema; este archivo solo adapta el flujo para Claude.

## Reglas de trabajo

- Trata `src/PrivLock.*` como la solución activa. `src/CamMicBlocker` es legado salvo petición explícita.
- PrivGvard es el nombre deseado; PrivLock/CamMicBlocker son la identidad actual del código. No hagas un rename global implícito.
- Windows es la ruta con recovery persistente. Linux/macOS permanecen en modo no soportado para mutaciones de privacidad hasta que tengan adapter exacto de captura, aplicación, observación y restauración.
- No conviertas un método stub, un comando externo exitoso o un estado deseado en protección efectiva.
- Los cambios de PnP, Registry, audio, permisos, elevación o startup son de alto riesgo: revisa los nueve vectores de `AGENTS.md`, añade pruebas de fallo y valida en un entorno aislado.
- No uses cambios destructivos ni limpieza amplia; conserva conflictos y restaura únicamente ownership confirmado.

## Formato de entrega

Finaliza con: estado, alcance, archivos modificados, validación ejecutada, riesgos residuales y próximo paso seguro. Si una prueba nativa no puede ejecutarse, dilo sin sustituirla por una inferencia.

Usa `docs/ai/SENIOR-ENGINEER-PROMPT.md` como prompt completo cuando la tarea requiera auditoría, diseño o implementación compleja.
