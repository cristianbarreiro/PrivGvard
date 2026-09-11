# PrivGvard 2.0.0 — Release Notes

**PrivGvard 2.0.0** es un Major Release que consolida la nueva generación de la aplicación, formalizando la arquitectura, identidad visual, resiliencia y mecanismos de protección auditados.

---

## Principales Novedades y Consolidación

- **Identidad Oficial PrivGvard**:
  - Consolidación del producto bajo el nombre oficial **PrivGvard** en ejecutables (`PrivGvard.exe`), metadatos de ensamblado, instalador y empaquetado portable.
  - Conservación de compatibilidad con directorios de persistencia previos (`StorageMigrationHelper`).

- **Protección de Cámara y Micrófono**:
  - Control de directivas de privacidad de Windows (`AppPrivacy`) con captura exacta del estado previo.
  - Aislamiento y control a nivel de dispositivos de hardware PnP mediante `CfgMgr32.dll`.
  - Silenciado y control de endpoints de captura de audio.

- **Protección Avanzada**:
  - Modo reforzado para control coordinado de directivas del sistema y deshabilitación física a nivel de nodo de dispositivo.
  - Interruptor global de protección unificada para cámara y micrófono simultáneos.

- **Restauración Segura y Crash Recovery**:
  - Diario de sesión transaccional (`privacy-session-v1.json`) con WAL (Write-Ahead Logging).
  - Verificación estricta de propiedad de cambios (`OwnershipAttestation` con hashes SHA-256).
  - Restauración automática garantizada al inicio si se detecta una sesión interrumpida o caída del sistema.
  - Restitución limpia ante cierre de sesión de Windows o apagado del equipo (`ShutdownCoordinator`).

- **Lifecycle y Elevación Bajo Demanda**:
  - Modelo de mínimo privilegio: la aplicación principal se ejecuta siempre sin privilegios (`asInvoker`).
  - Proceso trabajador privilegiado transaccional y efímero (`--privileged-worker`) invocado exclusivamente bajo demanda vía UAC seguro con named pipes autenticados y nonces de 256 bits.
  - Eliminación de binarios elevados permanentes o servicios en segundo plano innecesarios.

- **Interfaz y Experiencia de Usuario**:
  - Interfaz moderna en Avalonia UI 11 con tema Fluent Dark integrado.
  - Soporte dinámico multilingüe (Español / English) en tiempo real sin requerir reinicio.
  - Vista de diagnóstico del sistema y estado en vivo de capacidades de la plataforma.

- **Iconografía Dedicada por Superficie**:
  - `app.ico`: Recurso de aplicación oficial para el ejecutable, Explorador de Windows e instalador.
  - `tray.ico`: Recurso optimizado con escalado óptico y alineación de píxeles para el System Tray (16x16 a 48x48).
  - `taskbar.ico`: Recurso multi-resolución de alta fidelidad (16x16 hasta 256x256) derivado del diseño maestro para la barra de tareas de Windows y Alt+Tab.

- **Estabilidad y Cobertura de Tests**:
  - Suite integral de pruebas unitarias y de integración que validan el dominio, infraestructura, orquestación de aplicación, recuperación y plataforma Windows.
