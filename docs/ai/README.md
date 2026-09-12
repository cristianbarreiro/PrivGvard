# Contexto para asistentes de IA

Estos archivos separan el conocimiento del proyecto de las instrucciones específicas de cada agente:

| Archivo | Función |
|---|---|
| [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) | Fuente canónica, neutral y estructurada del proyecto (OKF) |
| [AUDIT-ROADMAP.md](AUDIT-ROADMAP.md) | Auditoría de evidencia, riesgos y plan por fases |
| [SENIOR-ENGINEER-PROMPT.md](SENIOR-ENGINEER-PROMPT.md) | Prompt completo reutilizable y contrato de salida |
| [../../AGENTS.md](../../AGENTS.md) | Reglas operativas para Codex/GPT y agentes compatibles |
| [../../GEMINI.md](../../GEMINI.md) | Adaptador corto para Gemini |
| [../../CLAUDE.md](../../CLAUDE.md) | Adaptador corto para Claude |

## Regla de mantenimiento

No dupliques la arquitectura en tres prompts. Si cambia el comportamiento real, actualiza primero `PROJECT_CONTEXT.md` y `AUDIT-ROADMAP.md`; después revisa los adaptadores solo si cambia la forma de carga del agente.

## Orden mínimo de lectura

```text
PROJECT_CONTEXT.md → AUDIT-ROADMAP.md → SENIOR-ENGINEER-PROMPT.md → AGENTS/GEMINI/CLAUDE.md
```

La documentación no puede convertir una capacidad no implementada en una capacidad soportada. El código y las pruebas siguen siendo la autoridad final.
