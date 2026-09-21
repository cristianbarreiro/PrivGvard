# Contexto para asistentes de IA — PrivGvard

Estos archivos separan el conocimiento canónico del proyecto de las instrucciones específicas para cada agente de IA:

| Archivo | Función |
|---|---|
| [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) | Fuente canónica, neutral y estructurada del proyecto (OKF) |
| [AUDIT-ROADMAP.md](AUDIT-ROADMAP.md) | Estado de auditoría, riesgos residuales y roadmap por fases |
| [SENIOR-ENGINEER-PROMPT.md](SENIOR-ENGINEER-PROMPT.md) | Prompt completo reutilizable y contrato de salida de ingeniería |
| [../recovery-validation.md](../recovery-validation.md) | Metodología de validación de recuperación y aceptación en Windows |
| [../../AGENTS.md](../../AGENTS.md) | Reglas operativas y de seguridad para Codex/GPT y agentes compatibles |
| [../../GEMINI.md](../../GEMINI.md) | Adaptador corto para Gemini |
| [../../CLAUDE.md](../../CLAUDE.md) | Adaptador corto para Claude |
| [../../.agents/AGENTS.md](../../.agents/AGENTS.md) | Adaptador de compatibilidad para herramientas que leen `.agents/` |

## Regla de mantenimiento

No dupliques la arquitectura en múltiples archivos. La fuente técnica canónica es `PROJECT_CONTEXT.md`. Si cambia el comportamiento del sistema, actualiza primero `PROJECT_CONTEXT.md` y `AUDIT-ROADMAP.md`; los archivos adaptadores solo deben mantener punteros ligeros hacia la verdad canónica.

## Orden mínimo de lectura

```text
PROJECT_CONTEXT.md → AUDIT-ROADMAP.md → SENIOR-ENGINEER-PROMPT.md → AGENTS/GEMINI/CLAUDE.md
```

La documentación no puede convertir una capacidad no implementada en una capacidad soportada. El código fuente y las pruebas son la autoridad final.
