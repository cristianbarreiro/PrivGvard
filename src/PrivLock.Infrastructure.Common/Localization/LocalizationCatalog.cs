namespace PrivLock.Infrastructure.Common.Localization;

/// <summary>
/// Cross-platform in-memory dictionary catalogs for UI localization (ES and EN).
/// Completely decoupled from WPF ResourceDictionaries or OS-specific formats.
/// </summary>
public static class LocalizationCatalog
{
    public static readonly IReadOnlyDictionary<string, string> StringsEs = new Dictionary<string, string>
    {
        ["AppTitle"] = "PrivLock",
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
        ["DeviceEnabled"] = "HABILITADO",
        ["DeviceDisabled"] = "BLOQUEADO",

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
        ["StandardRequiredFirst"] = "Debes activar la Protección Estándar antes de activar la Protección Segura."
    };

    public static readonly IReadOnlyDictionary<string, string> StringsEn = new Dictionary<string, string>
    {
        ["AppTitle"] = "PrivLock",
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
        ["DeviceEnabled"] = "ENABLED",
        ["DeviceDisabled"] = "BLOCKED",

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
        ["StandardRequiredFirst"] = "You must enable Standard Protection before enabling Secure Protection."
    };

    public static string Get(string key, string culture = "es", string fallback = "")
    {
        var dict = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? StringsEn : StringsEs;
        return dict.TryGetValue(key, out var val) ? val : (string.IsNullOrEmpty(fallback) ? key : fallback);
    }

    public static string GetString(string key, string culture = "es", string fallback = "") =>
        Get(key, culture, fallback);

    public static IReadOnlyDictionary<string, string> GetAll(string culture = "es") =>
        culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? StringsEn : StringsEs;
}
