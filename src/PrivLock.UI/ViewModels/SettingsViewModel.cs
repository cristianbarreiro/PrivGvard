using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using Serilog;

namespace PrivLock.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SettingsViewModel>();

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly ProtectionService _protectionService;

    [ObservableProperty]
    private SettingsSection _selectedSection = SettingsSection.General;

    public bool IsGeneralSelected => SelectedSection == SettingsSection.General;
    public bool IsHelpSelected => SelectedSection == SettingsSection.Help;
    public bool IsDiagnosticsSelected => SelectedSection == SettingsSection.Diagnostics;
    public bool IsAboutSelected => SelectedSection == SettingsSection.About;

    // --- General Section Properties ---
    [ObservableProperty]
    private bool _isAutostartEnabled;

    [ObservableProperty]
    private bool _isSpanishSelected;

    [ObservableProperty]
    private bool _isEnglishSelected;

    // --- Diagnostics Section Properties ---
    [ObservableProperty]
    private string _osName = "";

    [ObservableProperty]
    private string _architecture = "";

    [ObservableProperty]
    private string _privilegeLevel = "";

    [ObservableProperty]
    private string _cameraStatus = "";

    [ObservableProperty]
    private string _microphoneStatus = "";

    [ObservableProperty]
    private string _advancedStatus = "";

    [ObservableProperty]
    private string _capabilitiesSummary = "";

    [ObservableProperty]
    private int _devicesCount;

    [ObservableProperty]
    private string _devicesCountSummary = "";

    public ObservableCollection<string> DetectedDevicesList { get; } = [];

    // --- About Section Properties ---
    [ObservableProperty]
    private string _appName = "PrivGvard";

    [ObservableProperty]
    private string _appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "2.0.0";

    // --- Localized Titles & Labels ---
    public string WindowTitle => _localizationService.GetString("SettingsWindowTitle", "Configuración");
    public string HeaderTitle => _localizationService.GetString("SettingsHeaderTitle", "Configuración");
    public string NavGeneral => _localizationService.GetString("SettingsNavGeneral", "Ajustes");
    public string NavHelp => _localizationService.GetString("SettingsNavHelp", "Ayuda y Documentación");
    public string NavDiagnostics => _localizationService.GetString("SettingsNavDiagnostics", "Diagnóstico del Sistema");
    public string NavAbout => _localizationService.GetString("SettingsNavAbout", "Acerca de PrivGvard");

    // General
    public string AppearanceTitle => _localizationService.GetString("AppearanceTitle", "Apariencia");
    public string ThemeLabel => _localizationService.GetString("ThemeLabel", "Tema de la interfaz");
    public string ThemeDark => _localizationService.GetString("ThemeDark", "Oscuro (Predeterminado)");
    public string ThemeDescription => _localizationService.GetString("ThemeDescription", "El tema oscuro de alto contraste está optimizado para la interfaz de PrivGvard.");
    public string StartupTitle => _localizationService.GetString("StartupTitle", "Inicio del Sistema");
    public string StartupDescription => _localizationService.GetString("StartupDescription", "Iniciar PrivGvard automáticamente al iniciar sesión en el equipo.");
    public string StartWithSystemLabel => _localizationService.GetString("StartWithSystem", "Iniciar con el sistema");
    public string LanguageTitle => _localizationService.GetString("LanguageTitle", "Idioma de la interfaz");
    public string LanguageDescription => _localizationService.GetString("LanguageDescription", "Selecciona el idioma principal de la aplicación.");

    // Help
    public string HelpTitle => _localizationService.GetString("HelpTitle", "Ayuda y Documentación");
    public string HelpSubtitle => _localizationService.GetString("HelpSubtitle", "Guía de referencia rápida y funcionamiento de PrivGvard");
    public string HelpHowItWorksTitle => _localizationService.GetString("HelpHowItWorksTitle", "Cómo funciona PrivGvard");
    public string HelpHowItWorksDesc => _localizationService.GetString("HelpHowItWorksDesc", "PrivGvard aplica protección a nivel de software y hardware para garantizar que ninguna aplicación o proceso pueda acceder a tu cámara o micrófono sin tu consentimiento explícito.");
    public string HelpCameraTitle => _localizationService.GetString("HelpCameraTitle", "Protección de Cámara");
    public string HelpCameraDesc => _localizationService.GetString("HelpCameraDesc", "Deshabilita las directivas de acceso y los dispositivos de captura de video para impedir cualquier transmisión visual.");
    public string HelpMicTitle => _localizationService.GetString("HelpMicTitle", "Protección de Micrófono");
    public string HelpMicDesc => _localizationService.GetString("HelpMicDesc", "Silencia y bloquea los puntos de conexión de audio para evitar escuchas y grabaciones no autorizadas.");
    public string HelpAdvancedTitle => _localizationService.GetString("HelpAdvancedTitle", "Protección Avanzada");
    public string HelpAdvancedDesc => _localizationService.GetString("HelpAdvancedDesc", "Aplica aislamiento de hardware PnP (Plug and Play) y directivas de grupo del sistema para los dispositivos actualmente bloqueados.");
    public string HelpUnifiedTitle => _localizationService.GetString("HelpUnifiedTitle", "Control Unificado");
    public string HelpUnifiedDesc => _localizationService.GetString("HelpUnifiedDesc", "Permite bloquear o desbloquear la cámara y el micrófono simultáneamente con un solo interruptor.");
    public string HelpFaqTitle => _localizationService.GetString("HelpFaqTitle", "Preguntas Frecuentes");
    public string HelpFaq1Q => _localizationService.GetString("HelpFaq1Q", "¿Se mantienen los bloqueos tras reiniciar el equipo?");
    public string HelpFaq1A => _localizationService.GetString("HelpFaq1A", "Sí, PrivGvard cuenta con un diario de recuperación persistente para garantizar la consistencia en cada arranque.");

    // Diagnostics
    public string DiagTitle => _localizationService.GetString("DiagTitle", "Diagnóstico del Sistema");
    public string DiagSubtitle => _localizationService.GetString("DiagSubtitle", "Telemetría y estado operativo de privacidad");
    public string DiagOsLabel => _localizationService.GetString("DiagOsLabel", "Sistema Operativo");
    public string DiagArchLabel => _localizationService.GetString("DiagArchLabel", "Arquitectura");
    public string DiagPrivilegesLabel => _localizationService.GetString("DiagPrivilegesLabel", "Nivel de Privilegios");
    public string DiagCamStatusLabel => _localizationService.GetString("DiagCamStatusLabel", "Estado de Cámara");
    public string DiagMicStatusLabel => _localizationService.GetString("DiagMicStatusLabel", "Estado de Micrófono");
    public string DiagAdvancedLabel => _localizationService.GetString("DiagAdvancedLabel", "Protección Avanzada");
    public string DiagDevicesCountLabel => _localizationService.GetString("DiagDevicesCountLabel", "Dispositivos Detectados");
    public string DetectedDevicesLabel => _localizationService.GetString("DetectedDevices", "Dispositivos Detectados");
    public string DiagCapabilitiesLabel => _localizationService.GetString("DiagCapabilitiesLabel", "Nivel de Capacidades");
    public string DiagRefreshButton => _localizationService.GetString("DiagRefreshButton", "Actualizar Diagnóstico");

    // About
    public string AboutTitle => _localizationService.GetString("AboutTitle", "Acerca de PrivGvard");
    public string AboutVersionLabel => _localizationService.GetString("AboutVersionLabel", "Versión");
    public string AboutDeveloperLabel => _localizationService.GetString("AboutDeveloperLabel", "Desarrollador");
    public string AboutDeveloperValue => _localizationService.GetString("AboutDeveloperValue", "cdev Studio");
    public string AboutDescription => _localizationService.GetString("AboutDescription", "Solución de privacidad y control de hardware de cámara y micrófono para Windows.");
    public string AboutLicenseLabel => _localizationService.GetString("AboutLicenseLabel", "Licencia");
    public string AboutLicenseValue => _localizationService.GetString("AboutLicenseValue", "Licencia MIT");
    public string AboutCopyright => _localizationService.GetString("AboutCopyright", "© 2026 cdev Studio. Todos los derechos reservados.");

    public SettingsViewModel(
        SettingsService settingsService,
        LocalizationService localizationService,
        ProtectionService protectionService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _protectionService = protectionService;

        _localizationService.LanguageChanged += OnLanguageChanged;
        _protectionService.StateChanged += OnProtectionStateChanged;

        _isSpanishSelected = _localizationService.CurrentLanguage == "es";
        _isEnglishSelected = !_isSpanishSelected;
        _isAutostartEnabled = _settingsService.IsAutostartEnabled();

        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var ver = entryAssembly.GetName().Version;
        AppVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";

        _ = RefreshDiagnosticsAsync();
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsHelpSelected));
        OnPropertyChanged(nameof(IsDiagnosticsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
    }

    [RelayCommand]
    public void SelectSection(string sectionName)
    {
        if (Enum.TryParse<SettingsSection>(sectionName, true, out var section))
        {
            SelectedSection = section;
        }
    }

    [RelayCommand]
    private void SelectLanguage(string lang)
    {
        var result = _localizationService.SetLanguage(lang);
        IsSpanishSelected = lang == "es";
        IsEnglishSelected = lang == "en";
        if (!result.Success)
        {
            Log.Warning("Failed to save language preference from settings: {Error}", result.ErrorMessage);
        }
    }

    [RelayCommand]
    private void ToggleAutostart(bool enable)
    {
        var result = _settingsService.SetAutostart(enable);
        IsAutostartEnabled = _settingsService.IsAutostartEnabled();
        if (!result.Success)
        {
            Log.Warning("Failed to update autostart from settings: {Error}", result.ErrorMessage);
        }
    }

    [RelayCommand]
    public async Task RefreshDiagnosticsAsync()
    {
        try
        {
            var info = _protectionService.PlatformInfo;
            OsName = info.OsVersion.Contains(info.OperatingSystemName, StringComparison.OrdinalIgnoreCase)
                ? info.OsVersion
                : $"{info.OperatingSystemName} {info.OsVersion}";
            Architecture = $"{info.Architecture} ({(info.Is64Bit ? "64-bit" : "32-bit")})";
            PrivilegeLevel = info.IsElevated
                ? _localizationService.GetString("DiagPrivilegeAdmin", "Administrador (Elevado)")
                : _localizationService.GetString("DiagPrivilegeStandard", "Usuario estándar (asInvoker)");

            var caps = _protectionService.Capabilities;
            var camPrefix = _localizationService.GetString("SummaryCameraLabel", "Cámara");
            var micPrefix = _localizationService.GetString("SummaryMicrophoneLabel", "Micrófono");
            var hwYesNo = caps.SupportsHardwareDisable
                ? _localizationService.GetString("Yes", "Sí")
                : _localizationService.GetString("No", "No");
            CapabilitiesSummary = $"{camPrefix}: {caps.CameraProtectionLevel} | {micPrefix}: {caps.MicrophoneProtectionLevel} | Hardware: {hwYesNo}";

            var state = await _protectionService.GetCurrentStateAsync();
            CameraStatus = state.Camera.IsProtected
                ? (state.Camera.SecureState == SecureProtectionState.Active
                    ? _localizationService.GetString("StatusBlockedAdvanced", "🛡️ Bloqueado (Avanzado)")
                    : _localizationService.GetString("StatusBlockedStandard", "🔒 Bloqueado (Estándar)"))
                : _localizationService.GetString("StatusUnblocked", "✅ Permitido");
            MicrophoneStatus = state.Microphone.IsProtected
                ? (state.Microphone.SecureState == SecureProtectionState.Active
                    ? _localizationService.GetString("StatusBlockedAdvanced", "🛡️ Bloqueado (Avanzado)")
                    : _localizationService.GetString("StatusBlockedStandard", "🔒 Bloqueado (Estándar)"))
                : _localizationService.GetString("StatusUnblocked", "✅ Permitido");
            AdvancedStatus = state.AdvancedProtectionEnabled
                ? _localizationService.GetString("StatusActive", "🛡️ Activa")
                : _localizationService.GetString("StatusInactive", "○ Inactiva");

            var devices = await _protectionService.GetDetectedDevicesAsync();
            DevicesCount = devices.Count;
            DevicesCountSummary = string.Format(
                _localizationService.GetString("DevicesCountFormat", "{0} dispositivos detectados en total"),
                devices.Count);

            DetectedDevicesList.Clear();
            var enabledStatus = _localizationService.GetString("DeviceEnabled", "Habilitado");
            var disabledStatus = _localizationService.GetString("DeviceDisabled", "Deshabilitado");
            foreach (var d in devices)
            {
                var typeIcon = d.DeviceType == DeviceType.Camera ? "📷" : "🎙️";
                var statusStr = d.IsEnabled ? enabledStatus : disabledStatus;
                DetectedDevicesList.Add($"{typeIcon} {d.FriendlyName} [{statusStr}]");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh system diagnostics in SettingsViewModel");
        }
    }

    private void OnProtectionStateChanged(FullProtectionState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshDiagnosticsAsync());
    }

    private void OnLanguageChanged(string lang)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsSpanishSelected = lang == "es";
            IsEnglishSelected = lang == "en";

            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(NavGeneral));
            OnPropertyChanged(nameof(NavHelp));
            OnPropertyChanged(nameof(NavDiagnostics));
            OnPropertyChanged(nameof(NavAbout));

            OnPropertyChanged(nameof(AppearanceTitle));
            OnPropertyChanged(nameof(ThemeLabel));
            OnPropertyChanged(nameof(ThemeDark));
            OnPropertyChanged(nameof(ThemeDescription));
            OnPropertyChanged(nameof(StartupTitle));
            OnPropertyChanged(nameof(StartupDescription));
            OnPropertyChanged(nameof(StartWithSystemLabel));
            OnPropertyChanged(nameof(LanguageTitle));
            OnPropertyChanged(nameof(LanguageDescription));

            OnPropertyChanged(nameof(HelpTitle));
            OnPropertyChanged(nameof(HelpSubtitle));
            OnPropertyChanged(nameof(HelpHowItWorksTitle));
            OnPropertyChanged(nameof(HelpHowItWorksDesc));
            OnPropertyChanged(nameof(HelpCameraTitle));
            OnPropertyChanged(nameof(HelpCameraDesc));
            OnPropertyChanged(nameof(HelpMicTitle));
            OnPropertyChanged(nameof(HelpMicDesc));
            OnPropertyChanged(nameof(HelpAdvancedTitle));
            OnPropertyChanged(nameof(HelpAdvancedDesc));
            OnPropertyChanged(nameof(HelpUnifiedTitle));
            OnPropertyChanged(nameof(HelpUnifiedDesc));
            OnPropertyChanged(nameof(HelpFaqTitle));
            OnPropertyChanged(nameof(HelpFaq1Q));
            OnPropertyChanged(nameof(HelpFaq1A));

            OnPropertyChanged(nameof(DiagTitle));
            OnPropertyChanged(nameof(DiagSubtitle));
            OnPropertyChanged(nameof(DiagOsLabel));
            OnPropertyChanged(nameof(DiagArchLabel));
            OnPropertyChanged(nameof(DiagPrivilegesLabel));
            OnPropertyChanged(nameof(DiagCamStatusLabel));
            OnPropertyChanged(nameof(DiagMicStatusLabel));
            OnPropertyChanged(nameof(DiagAdvancedLabel));
            OnPropertyChanged(nameof(DiagDevicesCountLabel));
            OnPropertyChanged(nameof(DetectedDevicesLabel));
            OnPropertyChanged(nameof(DiagCapabilitiesLabel));
            OnPropertyChanged(nameof(DiagRefreshButton));

            OnPropertyChanged(nameof(AboutTitle));
            OnPropertyChanged(nameof(AboutVersionLabel));
            OnPropertyChanged(nameof(AboutDeveloperLabel));
            OnPropertyChanged(nameof(AboutDeveloperValue));
            OnPropertyChanged(nameof(AboutDescription));
            OnPropertyChanged(nameof(AboutLicenseLabel));
            OnPropertyChanged(nameof(AboutLicenseValue));
            OnPropertyChanged(nameof(AboutCopyright));

            _ = RefreshDiagnosticsAsync();
        });
    }
}
