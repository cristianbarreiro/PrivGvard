namespace PrivLock.Infrastructure.Common.Localization;

/// <summary>
/// Cross-platform in-memory dictionary catalogs for UI localization (ES and EN).
/// Completely decoupled from WPF ResourceDictionaries or OS-specific formats.
/// </summary>
public static class LocalizationCatalog
{
    public static readonly IReadOnlyDictionary<string, string> StringsEs = new Dictionary<string, string>
    {
        ["AppTitle"] = "PrivGvard",
        ["AppSubtitle"] = "Bloqueador de Cámara y Micrófono",

        // Sections
        ["CameraTitle"] = "Cámara",
        ["CameraSubtitle"] = "Protección de webcam y sensores de video",
        ["MicrophoneTitle"] = "Micrófono",
        ["MicrophoneSubtitle"] = "Protección de micrófonos y captura de audio",

        // Status
        ["StatusBlocked"] = "🔒 Bloqueado",
        ["StatusAllowed"] = "✅ Permitido",

        // Standard Protection
        ["StandardProtectionTitle"] = "Protección Estándar",
        ["StandardProtectionDesc"] = "Protección cotidiana sin permisos elevados",
        ["EnableStandard"] = "Activar Estándar",
        ["DisableStandard"] = "Desactivar Estándar",
        ["StatusStandardActive"] = "● Estándar Activa",
        ["StatusStandardInactive"] = "○ Estándar Inactiva",

        // Secure Protection
        ["SecureProtectionTitle"] = "Protección Segura (Admin)",
        ["SecureProtectionDesc"] = "Aislamiento físico y directivas de sistema de bajo nivel",
        ["EnableSecure"] = "🔒 Activar Protección Segura",
        ["DisableSecure"] = "Desactivar Segura",
        ["StatusSecureUnavailable"] = "🔒 No disponible (Activa estándar primero)",
        ["StatusSecureAvailable"] = "○ Disponible para activar",
        ["StatusSecureActive"] = "🛡️ Segura Activa (Reforzada)",
        ["SecureRequirementHint"] = "Activa primero la Protección Estándar",

        // Devices
        ["DetectedDevices"] = "Dispositivos de Hardware Detectados",
        ["DeviceEnabled"] = "Habilitado",
        ["DeviceDisabled"] = "Deshabilitado",

        // Settings & Footers
        ["StartWithSystem"] = "Iniciar con el sistema",
        ["Language"] = "Idioma",
        ["CapabilitiesTitle"] = "Capacidades del Sistema",

        // Unified Control
        ["UnifiedTitle"] = "Control Unificado",
        ["UnifiedSubtitle"] = "Controlar cámara y micrófono simultáneamente",
        ["UnifiedStandardDesc"] = "Proteger ambos dispositivos a la vez",
        ["UnifiedSecureDesc"] = "Reforzar protección en ambos dispositivos",

        // Advanced Protection (Global Extension)
        ["AdvancedProtectionTitle"] = "Protección Avanzada",
        ["AdvancedProtectionSubtitle"] = "Refuerzo físico y directivas para dispositivos bloqueados",
        ["AdvancedProtectionDesc"] = "Aplica aislamiento de hardware automáticamente a los dispositivos bloqueados",
        ["StatusAdvancedActive"] = "🛡️ Reforzada (Avanzada)",
        ["StatusAdvancedInactive"] = "○ Inactiva",
        ["StatusBlockedAdvanced"] = "🛡️ Bloqueado (Avanzado)",
        ["StatusBlockedStandard"] = "🔒 Bloqueado (Estándar)",
        ["StatusUnblocked"] = "✅ Permitido",

        // Notifications & Errors
        ["ElevationCancelled"] = "Operación cancelada: Se denegaron los permisos de administrador.",
        ["StandardRequiredFirst"] = "Debes activar la Protección Estándar antes de activar la Protección Segura.",

        // Settings Window Navigation
        ["SettingsWindowTitle"] = "Configuración",
        ["SettingsHeaderTitle"] = "Configuración",
        ["SettingsNavGeneral"] = "Ajustes",
        ["SettingsNavHelp"] = "Ayuda y Documentación",
        ["SettingsNavDiagnostics"] = "Diagnóstico del Sistema",
        ["SettingsNavAbout"] = "Acerca de PrivGvard",

        // Section: General Settings
        ["AppearanceTitle"] = "Apariencia",
        ["ThemeLabel"] = "Tema de la interfaz",
        ["ThemeDark"] = "Oscuro (Predeterminado)",
        ["ThemeDescription"] = "El tema oscuro de alto contraste está optimizado para la interfaz de PrivGvard.",
        ["StartupTitle"] = "Inicio del Sistema",
        ["StartupDescription"] = "Iniciar PrivGvard automáticamente al iniciar sesión en el equipo.",
        ["LanguageTitle"] = "Idioma de la interfaz",
        ["LanguageDescription"] = "Selecciona el idioma principal de la aplicación.",

        // Section: Help & Documentation
        ["HelpTitle"] = "Ayuda y Documentación",
        ["HelpSubtitle"] = "Guía de referencia rápida y funcionamiento de PrivGvard",
        ["HelpHowItWorksTitle"] = "Cómo funciona PrivGvard",
        ["HelpHowItWorksDesc"] = "PrivGvard aplica protección a nivel de software y hardware para garantizar que ninguna aplicación o proceso pueda acceder a tu cámara o micrófono sin tu consentimiento explícito.",
        ["HelpCameraTitle"] = "Protección de Cámara",
        ["HelpCameraDesc"] = "Deshabilita las directivas de acceso y los dispositivos de captura de video para impedir cualquier transmisión visual.",
        ["HelpMicTitle"] = "Protección de Micrófono",
        ["HelpMicDesc"] = "Silencia y bloquea los puntos de conexión de audio para evitar escuchas y grabaciones no autorizadas.",
        ["HelpAdvancedTitle"] = "Protección Avanzada",
        ["HelpAdvancedDesc"] = "Aplica aislamiento de hardware PnP (Plug and Play) y directivas de grupo del sistema para los dispositivos actualmente bloqueados.",
        ["HelpUnifiedTitle"] = "Control Unificado",
        ["HelpUnifiedDesc"] = "Permite bloquear o desbloquear la cámara y el micrófono simultáneamente con un solo interruptor.",
        ["HelpFaqTitle"] = "Preguntas Frecuentes",
        ["HelpFaq1Q"] = "¿Se mantienen los bloqueos tras reiniciar el equipo?",
        ["HelpFaq1A"] = "Sí, PrivGvard cuenta con un diario de recuperación persistente para garantizar la consistencia en cada arranque.",

        // Section: System Diagnostics
        ["DiagTitle"] = "Diagnóstico del Sistema",
        ["DiagSubtitle"] = "Telemetría y estado operativo de privacidad",
        ["DiagOsLabel"] = "Sistema Operativo",
        ["DiagArchLabel"] = "Arquitectura",
        ["DiagPrivilegesLabel"] = "Nivel de Privilegios",
        ["DiagCamStatusLabel"] = "Estado de Cámara",
        ["DiagMicStatusLabel"] = "Estado de Micrófono",
        ["DiagAdvancedLabel"] = "Protección Avanzada",
        ["DiagDevicesCountLabel"] = "Dispositivos Detectados",
        ["DiagCapabilitiesLabel"] = "Nivel de Capacidades",
        ["DiagRefreshButton"] = "Actualizar Diagnóstico",

        // Section: About
        ["AboutTitle"] = "Acerca de PrivGvard",
        ["AboutVersionLabel"] = "Versión",
        ["AboutDeveloperLabel"] = "Desarrollador",
        ["AboutDeveloperValue"] = "cdev Studio",
        ["AboutDescription"] = "Solución de privacidad y control de hardware de cámara y micrófono para Windows.",
        ["AboutLicenseLabel"] = "Licencia",
        ["AboutLicenseValue"] = "Licencia MIT",
        ["AboutCopyright"] = "© 2026 cdev Studio. Todos los derechos reservados.",

        // Device Types & Labels
        ["DeviceTypeCamera"] = "Cámara",
        ["DeviceTypeMicrophone"] = "Micrófono",
        ["SummaryCameraLabel"] = "Cámara",
        ["SummaryMicrophoneLabel"] = "Micrófono",

        // User & Privilege Levels
        ["UserStandard"] = "Usuario estándar",
        ["UserAdmin"] = "Administrador",
        ["DiagPrivilegeStandard"] = "Usuario estándar (asInvoker)",
        ["DiagPrivilegeAdmin"] = "Administrador (Elevado)",

        // Overall Badges & Protection Statuses
        ["BadgeHardened"] = "Reforzado",
        ["BadgeProtected"] = "Protegido",
        ["BadgePartiallyProtected"] = "Parcialmente protegido",
        ["BadgeUnprotected"] = "Sin protección",
        ["StatusPartiallyBlocked"] = "🔒 Parcialmente bloqueado",
        ["StatusActive"] = "🛡️ Activa",
        ["StatusInactive"] = "○ Inactiva",

        // Canonical Semantic Statuses
        ["Enabled"] = "Habilitado",
        ["Disabled"] = "Deshabilitado",
        ["Protected"] = "Protegido",
        ["Unprotected"] = "Sin protección",
        ["Active"] = "Activo",
        ["Inactive"] = "Inactivo",
        ["Allowed"] = "Permitido",
        ["Blocked"] = "Bloqueado",

        // Diagnostics Formatting & Hardware States
        ["DevicesCountFormat"] = "{0} dispositivos detectados en total",
        ["Yes"] = "Sí",
        ["No"] = "No",

        // Main Window Menu
        ["MenuToolTip"] = "Menú y Ajustes",
        ["MenuGeneral"] = "⚙️  Ajustes",
        ["MenuHelp"] = "📖  Ayuda y Documentación",
        ["MenuDiagnostics"] = "🛡️  Diagnóstico del Sistema",
        ["MenuAbout"] = "ℹ️  Acerca de PrivGvard",

        // System Tray Menu & Tooltip
        ["TrayOpen"] = "Abrir PrivGvard",
        ["TrayExit"] = "Salir",
        ["AppSubtitleLong"] = "PrivGvard - Bloqueador de Cámara y Micrófono",

        // Common Error Fallbacks
        ["ErrorUpdateCamera"] = "No se pudo actualizar la protección de la cámara",
        ["ErrorUpdateMic"] = "No se pudo actualizar la protección del micrófono",
        ["ErrorUpdateUnified"] = "No se pudo actualizar la protección unificada",
        ["ErrorUpdateAdvanced"] = "No se pudo actualizar la Protección Avanzada",
        ["ErrorCameraSecure"] = "Error de Protección Segura de Cámara",
        ["ErrorMicSecure"] = "Error de Protección Segura de Micrófono",
        ["ErrorBothSecure"] = "Error de Protección Segura Unificada",
        ["ErrorLanguagePreference"] = "No se pudo guardar la preferencia de idioma",
        ["ErrorAutostart"] = "No se pudo actualizar la configuración de inicio automático",
        ["StartupRecoveryFailed"] = "No se pudo restaurar completamente una sesión de privacidad anterior.",
        ["ErrorShutdownRestore"] = "PrivGvard no pudo restaurar con seguridad todos los cambios. Se canceló la salida.",
        ["ErrorShutdownFailed"] = "Falló la restauración de cierre: {0}"
    };

    public static readonly IReadOnlyDictionary<string, string> StringsEn = new Dictionary<string, string>
    {
        ["AppTitle"] = "PrivGvard",
        ["AppSubtitle"] = "Camera & Microphone Blocker",

        // Sections
        ["CameraTitle"] = "Camera",
        ["CameraSubtitle"] = "Webcam and video capture protection",
        ["MicrophoneTitle"] = "Microphone",
        ["MicrophoneSubtitle"] = "Microphone and audio capture protection",

        // Status
        ["StatusBlocked"] = "🔒 Blocked",
        ["StatusAllowed"] = "✅ Allowed",

        // Standard Protection
        ["StandardProtectionTitle"] = "Standard Protection",
        ["StandardProtectionDesc"] = "Everyday protection without elevated permissions",
        ["EnableStandard"] = "Enable Standard",
        ["DisableStandard"] = "Disable Standard",
        ["StatusStandardActive"] = "● Standard Active",
        ["StatusStandardInactive"] = "○ Standard Inactive",

        // Secure Protection
        ["SecureProtectionTitle"] = "Secure Protection (Admin)",
        ["SecureProtectionDesc"] = "Physical isolation and low-level system policies",
        ["EnableSecure"] = "🔒 Enable Secure Protection",
        ["DisableSecure"] = "Disable Secure",
        ["StatusSecureUnavailable"] = "🔒 Unavailable (Enable standard first)",
        ["StatusSecureAvailable"] = "○ Available to enable",
        ["StatusSecureActive"] = "🛡️ Secure Active (Hardened)",
        ["SecureRequirementHint"] = "Enable Standard Protection first",

        // Devices
        ["DetectedDevices"] = "Detected Hardware Devices",
        ["DeviceEnabled"] = "Enabled",
        ["DeviceDisabled"] = "Disabled",

        // Settings & Footers
        ["StartWithSystem"] = "Start with system",
        ["Language"] = "Language",
        ["CapabilitiesTitle"] = "Platform Capabilities",

        // Unified Control
        ["UnifiedTitle"] = "Unified Control",
        ["UnifiedSubtitle"] = "Control camera and microphone simultaneously",
        ["UnifiedStandardDesc"] = "Protect both devices at once",
        ["UnifiedSecureDesc"] = "Harden protection on both devices",

        // Advanced Protection (Global Extension)
        ["AdvancedProtectionTitle"] = "Advanced Protection",
        ["AdvancedProtectionSubtitle"] = "Hardware isolation and system policies for blocked devices",
        ["AdvancedProtectionDesc"] = "Automatically applies hardware-level isolation to blocked devices",
        ["StatusAdvancedActive"] = "🛡️ Hardened (Advanced)",
        ["StatusAdvancedInactive"] = "○ Inactive",
        ["StatusBlockedAdvanced"] = "🛡️ Blocked (Advanced)",
        ["StatusBlockedStandard"] = "🔒 Blocked (Standard)",
        ["StatusUnblocked"] = "✅ Allowed",

        // Notifications & Errors
        ["ElevationCancelled"] = "Operation cancelled: Administrator permissions were denied.",
        ["StandardRequiredFirst"] = "You must enable Standard Protection before enabling Secure Protection.",

        // Settings Window Navigation
        ["SettingsWindowTitle"] = "Settings",
        ["SettingsHeaderTitle"] = "Settings",
        ["SettingsNavGeneral"] = "General",
        ["SettingsNavHelp"] = "Help & Documentation",
        ["SettingsNavDiagnostics"] = "System Diagnostics",
        ["SettingsNavAbout"] = "About PrivGvard",

        // Section: General Settings
        ["AppearanceTitle"] = "Appearance",
        ["ThemeLabel"] = "Interface Theme",
        ["ThemeDark"] = "Dark (Default)",
        ["ThemeDescription"] = "High-contrast dark theme optimized for the PrivGvard interface.",
        ["StartupTitle"] = "System Startup",
        ["StartupDescription"] = "Automatically start PrivGvard when logging into your computer.",
        ["LanguageTitle"] = "Interface Language",
        ["LanguageDescription"] = "Select the primary application language.",

        // Section: Help & Documentation
        ["HelpTitle"] = "Help & Documentation",
        ["HelpSubtitle"] = "Quick reference and operational guide for PrivGvard",
        ["HelpHowItWorksTitle"] = "How PrivGvard Works",
        ["HelpHowItWorksDesc"] = "PrivGvard applies software and hardware level protection to ensure no application or process can access your camera or microphone without your explicit consent.",
        ["HelpCameraTitle"] = "Camera Protection",
        ["HelpCameraDesc"] = "Disables access policies and video capture devices to prevent any visual transmission.",
        ["HelpMicTitle"] = "Microphone Protection",
        ["HelpMicDesc"] = "Mutes and blocks audio capture endpoints to prevent unauthorized recording and eavesdropping.",
        ["HelpAdvancedTitle"] = "Advanced Protection",
        ["HelpAdvancedDesc"] = "Applies PnP (Plug and Play) hardware isolation and system group policies to currently blocked devices.",
        ["HelpUnifiedTitle"] = "Unified Control",
        ["HelpUnifiedDesc"] = "Allows blocking or unblocking both camera and microphone simultaneously with a single toggle.",
        ["HelpFaqTitle"] = "Frequently Asked Questions",
        ["HelpFaq1Q"] = "Are device blocks retained after rebooting?",
        ["HelpFaq1A"] = "Yes, PrivGvard features a persistent recovery journal ensuring consistency across every reboot.",

        // Section: System Diagnostics
        ["DiagTitle"] = "System Diagnostics",
        ["DiagSubtitle"] = "Telemetry and operational privacy status",
        ["DiagOsLabel"] = "Operating System",
        ["DiagArchLabel"] = "Architecture",
        ["DiagPrivilegesLabel"] = "Privilege Level",
        ["DiagCamStatusLabel"] = "Camera Status",
        ["DiagMicStatusLabel"] = "Microphone Status",
        ["DiagAdvancedLabel"] = "Advanced Protection",
        ["DiagDevicesCountLabel"] = "Detected Devices",
        ["DiagCapabilitiesLabel"] = "Capability Level",
        ["DiagRefreshButton"] = "Refresh Diagnostics",

        // Section: About
        ["AboutTitle"] = "About PrivGvard",
        ["AboutVersionLabel"] = "Version",
        ["AboutDeveloperLabel"] = "Developer",
        ["AboutDeveloperValue"] = "cdev Studio",
        ["AboutDescription"] = "Hardware privacy and camera/microphone security solution for Windows.",
        ["AboutLicenseLabel"] = "License",
        ["AboutLicenseValue"] = "MIT License",
        ["AboutCopyright"] = "© 2026 cdev Studio. All rights reserved.",

        // Device Types & Labels
        ["DeviceTypeCamera"] = "Camera",
        ["DeviceTypeMicrophone"] = "Microphone",
        ["SummaryCameraLabel"] = "Cam",
        ["SummaryMicrophoneLabel"] = "Mic",

        // User & Privilege Levels
        ["UserStandard"] = "Standard User",
        ["UserAdmin"] = "Administrator",
        ["DiagPrivilegeStandard"] = "Standard User (asInvoker)",
        ["DiagPrivilegeAdmin"] = "Administrator (Elevated)",

        // Overall Badges & Protection Statuses
        ["BadgeHardened"] = "Hardened",
        ["BadgeProtected"] = "Protected",
        ["BadgePartiallyProtected"] = "Partially Protected",
        ["BadgeUnprotected"] = "Unprotected",
        ["StatusPartiallyBlocked"] = "🔒 Partially Blocked",
        ["StatusActive"] = "🛡️ Active",
        ["StatusInactive"] = "○ Inactive",

        // Canonical Semantic Statuses
        ["Enabled"] = "Enabled",
        ["Disabled"] = "Disabled",
        ["Protected"] = "Protected",
        ["Unprotected"] = "Unprotected",
        ["Active"] = "Active",
        ["Inactive"] = "Inactive",
        ["Allowed"] = "Allowed",
        ["Blocked"] = "Blocked",

        // Diagnostics Formatting & Hardware States
        ["DevicesCountFormat"] = "{0} devices detected in total",
        ["Yes"] = "Yes",
        ["No"] = "No",

        // Main Window Menu
        ["MenuToolTip"] = "Menu and Settings",
        ["MenuGeneral"] = "⚙️  General Settings",
        ["MenuHelp"] = "📖  Help & Documentation",
        ["MenuDiagnostics"] = "🛡️  System Diagnostics",
        ["MenuAbout"] = "ℹ️  About PrivGvard",

        // System Tray Menu & Tooltip
        ["TrayOpen"] = "Open PrivGvard",
        ["TrayExit"] = "Exit",
        ["AppSubtitleLong"] = "PrivGvard - Camera & Microphone Blocker",

        // Common Error Fallbacks
        ["ErrorUpdateCamera"] = "Failed to update Camera protection",
        ["ErrorUpdateMic"] = "Failed to update Microphone protection",
        ["ErrorUpdateUnified"] = "Failed to update Unified protection",
        ["ErrorUpdateAdvanced"] = "Failed to update Advanced Protection",
        ["ErrorCameraSecure"] = "Camera Secure Protection error",
        ["ErrorMicSecure"] = "Microphone Secure Protection error",
        ["ErrorBothSecure"] = "Both Secure Protection error",
        ["ErrorLanguagePreference"] = "Failed to save language preference",
        ["ErrorAutostart"] = "Failed to update autostart setting",
        ["StartupRecoveryFailed"] = "A previous privacy session could not be fully restored.",
        ["ErrorShutdownRestore"] = "PrivGvard could not safely restore every owned change. Exit was cancelled.",
        ["ErrorShutdownFailed"] = "Shutdown restoration failed: {0}"
    };

    public static string Get(string key, string culture = "es", string fallback = "")
    {
        var dict = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? StringsEn : StringsEs;
        if (dict.TryGetValue(key, out var val))
            return val;

        if (key.Equals("Tray.Open", StringComparison.OrdinalIgnoreCase) && dict.TryGetValue("TrayOpen", out val))
            return val;
        if (key.Equals("Tray.Exit", StringComparison.OrdinalIgnoreCase) && dict.TryGetValue("TrayExit", out val))
            return val;
        if ((key.Equals("Tray.ToolTip", StringComparison.OrdinalIgnoreCase) || key.Equals("TrayToolTip", StringComparison.OrdinalIgnoreCase))
            && dict.TryGetValue("AppSubtitleLong", out val))
            return val;

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    public static string GetString(string key, string culture = "es", string fallback = "") =>
        Get(key, culture, fallback);

    public static IReadOnlyDictionary<string, string> GetAll(string culture = "es") =>
        culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? StringsEn : StringsEs;
}
