# PrivGvard — Roadmap de Evolución y Estado de Auditoría

**Estado:** Activo / PrivGvard 2.0<br />
**Fecha de actualización:** 2026-09-12<br />
**Repositorio:** `Cam&MicroBlocker`<br />
**Rama activa:** `dev`<br />
**Identidad del producto:** PrivGvard (ejecutable: `PrivGvard.exe` / `PrivGvard`)<br />
**Postura de soporte:** Windows es la plataforma principal soportada para protección y recuperación de privacidad. Linux y macOS permanecen como scaffolds de descubrimiento/interfaz (`CapabilityLevel.None`) hasta que cuenten con adaptadores de persistencia y rollback reversibles.

---

## 1. Resumen de Estado del Sistema

- **Arquitectura**: Clean Architecture en C# / .NET 10 con Avalonia UI 11. Host unificado en `src/PrivLock.Desktop`.
- **Suite de Pruebas**: Suite activa completa ejecutada en CI multi-plataforma. Pruebas de Dominio, Aplicación e Infraestructura se ejecutan en Windows, Ubuntu y macOS; pruebas de la plataforma Windows se ejecutan condicionalmente en Windows.
- **Identidad**: El producto compila como `PrivGvard.exe` con metadatos oficiales, instalador Inno Setup (`PrivGvard-Setup-2.0.0.exe`) y distribución portable (`PrivGvard-Portable-2.0.0.zip`). Los espacios de nombres internos (`PrivLock.*`) y solución (`CamMicBlocker.sln`) se mantienen estables por compatibilidad.

---

## 2. Trabajo Pendiente y Deuda Técnica (Roadmap)

### P0 — Salvaguardas de Seguridad Multiplataforma (Linux / macOS)

*Estado: BLOQUEO DE CAPACIDAD ACTIVO (`CapabilityLevel.None`)*

- **Linux (Controlador experimental de hardware)**:
  - `src/PrivLock.Platform.Linux/Devices/LinuxDeviceController.cs` contiene código experimental que utiliza `chmod 000` y `chmod 660` sin capturar modo, permisos POSIX, ACL, dueño ni reglas udev originales.
  - La manipulación de audio mediante `wpctl` y `pactl` no captura endpoints específicos, volumen original ni dependencias de sesión.
  - *Acción requerida*: Mantener `CapabilityLevel.None` y `UnsupportedPrivacySessionPlatformAdapter` en producción. No habilitar mutaciones en Linux hasta diseñar un contrato de snapshot reversible y verificación de estado efectivo.
- **macOS (Controlador experimental de hardware y límites TCC)**:
  - `src/PrivLock.Platform.MacOS/Devices/MacOSDeviceController.cs` restaura el volumen de entrada a un valor fijo (`75`) en vez de restituir el valor original capturado.
  - Los métodos de protección segura retornan éxito sin aplicar una barrera nativa efectiva.
  - TCC (Transparency, Consent, and Control) es una frontera de seguridad del SO; PrivGvard no puede revocar silenciosamente permisos de otras aplicaciones.
  - *Acción requerida*: Mantener `CapabilityLevel.None` y `UnsupportedPrivacySessionPlatformAdapter`. Tratar TCC como guía de usuario/inspección y no como mutación silenciosa.

---

### P1 — Endurecimiento y Validación de Entorno Físico (Windows)

- **Matriz de aceptación en hardware / VM aislada**:
  - Validar los escenarios definidos en [docs/recovery-validation.md](../recovery-validation.md) en una máquina virtual Windows dedicada con periféricos USB reales/desechables:
    - Desconexión y reconexión física de cámaras/micrófonos (*hotplug*) durante sesión activa de bloqueo.
    - Cierre forzado del proceso (*kill / BSOD simulated*) y verificación del rollback en el siguiente inicio.
    - Cancelación de diálogo UAC por parte del usuario o denegación de credenciales.
    - Validación en Windows 10 vs. Windows 11 (22H2 / 23H2 / 24H2) y arquitecturas x64 vs. ARM64.
- **Propagación de contratos de resultado**:
  - Asegurar que la UI refleje fielmente estados intermedios o degradados (`Unsupported`, `Denied`, `Failed`, `Unknown`, `OutcomeUncertain`) sin simplificarlos a fallos genéricos.

---

### P2 — Mejoras Post-2.0 y Roadmap Futuro

- **Adaptador nativo de audio en Linux**:
  - Diseñar un proveedor PipeWire / WirePlumber nativo que capture nodos de audio estables, volumen y estado de mute con rollback exacto.
- **Adaptador nativo de audio en macOS**:
  - Implementar captura y restauración de volumen/mute por AudioDeviceID exacto mediante CoreAudio HAL.
- **Migración de identificadores internos**:
  - Cuando se apruebe una migración interna de código, planificar la renonmbrada coordinada de espacios de nombres `PrivLock.*` y la solución `CamMicBlocker.sln` hacia `PrivGvard`, garantizando la migración de llaves de registro de propiedad y mutex.

---

## 3. Elementos Resueltos y Consolidados (Histórico de Auditoría)

| Elemento / Problema | Resolución | Evidencia |
|---|---|---|
| **Topología de CI y fallo en Ubuntu** | **RESUELTO** | CI en `.github/workflows/ci.yml` divide la ejecución: pruebas de Dominio, Aplicación e Infraestructura corren en todas las plataformas; pruebas de Windows se ejecutan únicamente si `runner.os == 'Windows'`. |
| **Localización del menú de System Tray** | **RESUELTO** | `App.axaml.cs` actualiza dinámicamente las cabeceras de `NativeMenuItem` ("Abrir PrivGvard" / "Open PrivGvard", "Salir" / "Exit") al alternar idioma en `LocalizationCatalog`. |
| **Hover y pulsado del botón Cerrar (X)** | **RESUELTO** | Estilo en `App.axaml` (`Button.titlebar-close`) asegura fondo rojo y glifo 'X' blanco en estados hover y pressed. |
| **Identidad de Ejecutable y Metadatos** | **RESUELTO** | `PrivLock.Desktop.csproj` define `<AssemblyName>PrivGvard</AssemblyName>`, produciendo `PrivGvard.exe` con metadatos de producto `PrivGvard 2.0.0`. |
| **Generación de Instalador y Portable** | **RESUELTO** | `build-installer.ps1` e `installer/setup.iss` compilan `PrivGvard-Setup-2.0.0.exe` y `PrivGvard-Portable-2.0.0.zip` en `installer_out/`. |
| **Migración Segura de Datos y Logs** | **RESUELTO** | `StorageMigrationHelper` migra automáticamente configuraciones y registros de `%LOCALAPPDATA%\PrivLock` hacia `%LOCALAPPDATA%\PrivGvard`. |
| **Cuarentena del Código Heredado (WPF)** | **RESUELTO** | Código 1.x aislado en `legacy/CamMicBlocker` con guardas de compilación para evitar compilación accidental. |
| **Eliminación de Recuentos Fijos de Tests** | **RESUELTO** | Recuentos volátiles (como 61 o 156 tests) eliminados de la documentación permanente para evitar obsolescencia rápida. |

---

## 4. Definition of Done para Soporte de una Plataforma

Para que una plataforma sea considerada **Soportada** (más allá de compilación o UI), debe cumplir:

1. Detección nativa estable del recurso físico o directiva del SO.
2. Captura exacta (*snapshot*) del estado previo (permisos, volumen, existencia de clave, etc.).
3. Mutación acotada exclusivamente al recurso especificado.
4. Verificación nativa posterior al bloqueo y posterior a la restauración (`EffectiveStatus`).
5. Registro persistente en diario Write-Ahead Log (WAL) con atestación de propiedad.
6. Reversibilidad garantizada ante fallo parcial, cancelación, reinicio o caída del proceso.
7. Tratamiento adecuado de dispositivos desconectados (*hotplug*).
8. Ejecución bajo mínimo privilegio sin retención innecesaria de permisos.
9. Pruebas unitarias de rollback y aceptación manual en hardware real.
