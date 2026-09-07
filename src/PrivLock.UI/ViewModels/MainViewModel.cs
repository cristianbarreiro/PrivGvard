using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using Serilog;

namespace PrivLock.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainViewModel>();

    private readonly ProtectionService _protectionService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;

    private bool _isUpdating;

    [ObservableProperty]
    private string _appTitle = "PrivLock";

    [ObservableProperty]
    private string _appSubtitle = "Camera & Microphone Blocker";

    // --- Advanced Protection (Global Extension) ---
    [ObservableProperty]
    private bool _isAdvancedProtectionEnabled;

    [ObservableProperty]
    private string _advancedProtectionStatusText = "";

    [ObservableProperty]
    private string _advancedProtectionStatusColor = "#888888";

    // --- Camera States ---
    [ObservableProperty]
    private bool _isCameraBlocked;

    [ObservableProperty]
    private string _cameraStatusText = "";

    [ObservableProperty]
    private string _cameraStatusColor = "#81C784";

    [ObservableProperty]
    private bool _isCameraStandardActive;

    [ObservableProperty]
    private string _cameraStandardText = "";

    [ObservableProperty]
    private string _cameraStandardColor = "#81C784";

    [ObservableProperty]
    private string _cameraSecureText = "";

    [ObservableProperty]
    private string _cameraSecureBadgeColor = "#555555";

    [ObservableProperty]
    private string _cameraSecureButtonText = "";

    [ObservableProperty]
    private bool _isCameraSecureButtonEnabled;

    [ObservableProperty]
    private string _cameraSecureHint = "";

    [ObservableProperty]
    private bool _isCameraSecureHintVisible;

    // --- Microphone States ---
    [ObservableProperty]
    private bool _isMicBlocked;

    [ObservableProperty]
    private string _micStatusText = "";

    [ObservableProperty]
    private string _micStatusColor = "#81C784";

    [ObservableProperty]
    private bool _isMicStandardActive;

    [ObservableProperty]
    private string _micStandardText = "";

    [ObservableProperty]
    private string _micStandardColor = "#81C784";

    [ObservableProperty]
    private string _micSecureText = "";

    [ObservableProperty]
    private string _micSecureBadgeColor = "#555555";

    [ObservableProperty]
    private string _micSecureButtonText = "";

    [ObservableProperty]
    private bool _isMicSecureButtonEnabled;

    [ObservableProperty]
    private string _micSecureHint = "";

    [ObservableProperty]
    private bool _isMicSecureHintVisible;

    // --- Unified (Both) States ---
    [ObservableProperty]
    private bool _isBothBlocked;

    [ObservableProperty]
    private string _bothStatusText = "";

    [ObservableProperty]
    private string _bothStatusColor = "#81C784";

    [ObservableProperty]
    private bool _isBothStandardActive;

    [ObservableProperty]
    private string _bothStandardText = "";

    [ObservableProperty]
    private string _bothStandardColor = "#81C784";

    [ObservableProperty]
    private string _bothSecureText = "";

    [ObservableProperty]
    private string _bothSecureBadgeColor = "#555555";

    [ObservableProperty]
    private string _bothSecureButtonText = "";

    [ObservableProperty]
    private bool _isBothSecureButtonEnabled;

    [ObservableProperty]
    private string _bothSecureHint = "";

    [ObservableProperty]
    private bool _isBothSecureHintVisible;

    // --- Common & Settings ---
    [ObservableProperty]
    private bool _isSpanishSelected = true;

    [ObservableProperty]
    private bool _isEnglishSelected;

    [ObservableProperty]
    private bool _isAutostartEnabled;

    [ObservableProperty]
    private string _platformName = "";

    [ObservableProperty]
    private string _capabilitiesSummary = "";

    [ObservableProperty]
    private string _securityBadgeText = "Standard";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    // Localized Labels
    public string CameraTitle => _localizationService.GetString("CameraTitle");
    public string CameraSubtitle => _localizationService.GetString("CameraSubtitle");
    public string MicrophoneTitle => _localizationService.GetString("MicrophoneTitle");
    public string MicrophoneSubtitle => _localizationService.GetString("MicrophoneSubtitle");
    public string StandardProtectionTitle => _localizationService.GetString("StandardProtectionTitle");
    public string StandardProtectionDesc => _localizationService.GetString("StandardProtectionDesc");
    public string SecureProtectionTitle => _localizationService.GetString("SecureProtectionTitle");
    public string SecureProtectionDesc => _localizationService.GetString("SecureProtectionDesc");
    public string AdvancedProtectionTitle => _localizationService.GetString("AdvancedProtectionTitle");
    public string AdvancedProtectionSubtitle => _localizationService.GetString("AdvancedProtectionSubtitle");
    public string AdvancedProtectionDesc => _localizationService.GetString("AdvancedProtectionDesc");
    public string DetectedDevicesLabel => _localizationService.GetString("DetectedDevices");
    public string StartWithSystemLabel => _localizationService.GetString("StartWithSystem");
    public string LanguageLabel => _localizationService.GetString("Language");
    public string CapabilitiesTitle => _localizationService.GetString("CapabilitiesTitle");
    public string UnifiedTitle => _localizationService.GetString("UnifiedTitle");
    public string UnifiedSubtitle => _localizationService.GetString("UnifiedSubtitle");
    public string UnifiedStandardDesc => _localizationService.GetString("UnifiedStandardDesc");
    public string UnifiedSecureDesc => _localizationService.GetString("UnifiedSecureDesc");

    public MainViewModel(
        ProtectionService protectionService,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _protectionService = protectionService;
        _settingsService = settingsService;
        _localizationService = localizationService;

        _protectionService.StateChanged += OnProtectionStateChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;

        _isSpanishSelected = _localizationService.CurrentLanguage == "es";
        _isEnglishSelected = !_isSpanishSelected;
        _isAutostartEnabled = _settingsService.IsAutostartEnabled();

        var info = _protectionService.PlatformInfo;
        PlatformName = $"{info.OperatingSystemName} ({info.Architecture}) - {(info.IsElevated ? "Admin/Root" : "Standard User")}";
        CapabilitiesSummary = $"Cam: {_protectionService.Capabilities.CameraProtectionLevel} | Mic: {_protectionService.Capabilities.MicrophoneProtectionLevel}";

        _ = RefreshStateAsync();
    }

    public async Task RefreshStateAsync()
    {
        try
        {
            var state = await _protectionService.GetCurrentStateAsync();
            var devices = await _protectionService.GetDetectedDevicesAsync();
            UpdateUi(state, devices);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh UI state");
        }
    }

    private void OnProtectionStateChanged(FullProtectionState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var devices = await _protectionService.GetDetectedDevicesAsync();
                UpdateUi(state, devices);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to refresh detected devices after a protection-state change");
                ShowError($"Could not verify the current device state: {ex.Message}");
            }
        });
    }

    private void OnLanguageChanged(string lang)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CameraTitle));
            OnPropertyChanged(nameof(CameraSubtitle));
            OnPropertyChanged(nameof(MicrophoneTitle));
            OnPropertyChanged(nameof(MicrophoneSubtitle));
            OnPropertyChanged(nameof(StandardProtectionTitle));
            OnPropertyChanged(nameof(StandardProtectionDesc));
            OnPropertyChanged(nameof(SecureProtectionTitle));
            OnPropertyChanged(nameof(SecureProtectionDesc));
            OnPropertyChanged(nameof(AdvancedProtectionTitle));
            OnPropertyChanged(nameof(AdvancedProtectionSubtitle));
            OnPropertyChanged(nameof(AdvancedProtectionDesc));
            OnPropertyChanged(nameof(DetectedDevicesLabel));
            OnPropertyChanged(nameof(StartWithSystemLabel));
            OnPropertyChanged(nameof(LanguageLabel));
            OnPropertyChanged(nameof(CapabilitiesTitle));
            OnPropertyChanged(nameof(UnifiedTitle));
            OnPropertyChanged(nameof(UnifiedSubtitle));
            OnPropertyChanged(nameof(UnifiedStandardDesc));
            OnPropertyChanged(nameof(UnifiedSecureDesc));
            _ = RefreshStateAsync();
        });
    }

    private void UpdateUi(FullProtectionState state, IReadOnlyList<DeviceInfo> devices)
    {
        _isUpdating = true;
        try
        {
            // === 1. Advanced Protection (Global Extension) ===
            IsAdvancedProtectionEnabled = state.AdvancedProtectionEnabled;
            AdvancedProtectionStatusText = state.AdvancedProtectionEnabled
                ? _localizationService.GetString("StatusAdvancedActive", "🛡️ Hardened (Advanced)")
                : _localizationService.GetString("StatusAdvancedInactive", "○ Inactive");
            AdvancedProtectionStatusColor = state.AdvancedProtectionEnabled ? "#007ACC" : "#888888";

            // === 2. Camera UI ===
            var camBlocked = state.Camera.IsProtected;
            IsCameraBlocked = camBlocked;
            IsCameraStandardActive = camBlocked;

            if (!camBlocked)
            {
                CameraStatusText = _localizationService.GetString("StatusUnblocked", "✅ Allowed");
                CameraStatusColor = "#81C784";
                CameraStandardText = _localizationService.GetString("StatusStandardInactive", "○ Standard Inactive");
                CameraStandardColor = "#81C784";
            }
            else if (state.Camera.SecureState == SecureProtectionState.Active)
            {
                CameraStatusText = _localizationService.GetString("StatusBlockedAdvanced", "🛡️ Blocked (Advanced)");
                CameraStatusColor = "#D32F2F";
                CameraStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                CameraStandardColor = "#E57373";
            }
            else
            {
                CameraStatusText = _localizationService.GetString("StatusBlockedStandard", "🔒 Blocked (Standard)");
                CameraStatusColor = "#E57373";
                CameraStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                CameraStandardColor = "#E57373";
            }

            // Camera Secure Compatibility fields
            switch (state.Camera.SecureState)
            {
                case SecureProtectionState.Active:
                    CameraSecureText = _localizationService.GetString("StatusSecureActive", "🛡️ Secure Active (Hardened)");
                    CameraSecureBadgeColor = "#D32F2F";
                    CameraSecureButtonText = _localizationService.GetString("DisableSecure", "Disable Secure");
                    IsCameraSecureButtonEnabled = true;
                    IsCameraSecureHintVisible = false;
                    break;
                case SecureProtectionState.Available:
                    CameraSecureText = _localizationService.GetString("StatusSecureAvailable", "○ Available to enable");
                    CameraSecureBadgeColor = "#F57C00";
                    CameraSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                    IsCameraSecureButtonEnabled = true;
                    IsCameraSecureHintVisible = false;
                    break;
                default:
                    CameraSecureText = _localizationService.GetString("StatusSecureUnavailable", "🔒 Unavailable");
                    CameraSecureBadgeColor = "#555555";
                    CameraSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                    IsCameraSecureButtonEnabled = false;
                    CameraSecureHint = _localizationService.GetString("SecureRequirementHint", "Enable Standard Protection first");
                    IsCameraSecureHintVisible = !camBlocked;
                    break;
            }

            // === 3. Microphone UI ===
            var micBlocked = state.Microphone.IsProtected;
            IsMicBlocked = micBlocked;
            IsMicStandardActive = micBlocked;

            if (!micBlocked)
            {
                MicStatusText = _localizationService.GetString("StatusUnblocked", "✅ Allowed");
                MicStatusColor = "#81C784";
                MicStandardText = _localizationService.GetString("StatusStandardInactive", "○ Standard Inactive");
                MicStandardColor = "#81C784";
            }
            else if (state.Microphone.SecureState == SecureProtectionState.Active)
            {
                MicStatusText = _localizationService.GetString("StatusBlockedAdvanced", "🛡️ Blocked (Advanced)");
                MicStatusColor = "#D32F2F";
                MicStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                MicStandardColor = "#E57373";
            }
            else
            {
                MicStatusText = _localizationService.GetString("StatusBlockedStandard", "🔒 Blocked (Standard)");
                MicStatusColor = "#E57373";
                MicStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                MicStandardColor = "#E57373";
            }

            // Microphone Secure Compatibility fields
            switch (state.Microphone.SecureState)
            {
                case SecureProtectionState.Active:
                    MicSecureText = _localizationService.GetString("StatusSecureActive", "🛡️ Secure Active (Hardened)");
                    MicSecureBadgeColor = "#D32F2F";
                    MicSecureButtonText = _localizationService.GetString("DisableSecure", "Disable Secure");
                    IsMicSecureButtonEnabled = true;
                    IsMicSecureHintVisible = false;
                    break;
                case SecureProtectionState.Available:
                    MicSecureText = _localizationService.GetString("StatusSecureAvailable", "○ Available to enable");
                    MicSecureBadgeColor = "#F57C00";
                    MicSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                    IsMicSecureButtonEnabled = true;
                    IsMicSecureHintVisible = false;
                    break;
                default:
                    MicSecureText = _localizationService.GetString("StatusSecureUnavailable", "🔒 Unavailable");
                    MicSecureBadgeColor = "#555555";
                    MicSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                    IsMicSecureButtonEnabled = false;
                    MicSecureHint = _localizationService.GetString("SecureRequirementHint", "Enable Standard Protection first");
                    IsMicSecureHintVisible = !micBlocked;
                    break;
            }

            // === 4. Unified (Both) UI ===
            var bothBlocked = camBlocked && micBlocked;
            IsBothBlocked = bothBlocked;
            IsBothStandardActive = bothBlocked;

            if (!bothBlocked)
            {
                BothStatusText = (camBlocked || micBlocked)
                    ? _localizationService.GetString("StatusBlockedStandard", "🔒 Partially Blocked")
                    : _localizationService.GetString("StatusUnblocked", "✅ Allowed");
                BothStatusColor = (camBlocked || micBlocked) ? "#FFA726" : "#81C784";
                BothStandardText = _localizationService.GetString("StatusStandardInactive", "○ Standard Inactive");
                BothStandardColor = "#81C784";
            }
            else if (state.BothSecure)
            {
                BothStatusText = _localizationService.GetString("StatusBlockedAdvanced", "🛡️ Blocked (Advanced)");
                BothStatusColor = "#D32F2F";
                BothStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                BothStandardColor = "#E57373";
            }
            else
            {
                BothStatusText = _localizationService.GetString("StatusBlockedStandard", "🔒 Blocked (Standard)");
                BothStatusColor = "#E57373";
                BothStandardText = _localizationService.GetString("StatusStandardActive", "● Standard Active");
                BothStandardColor = "#E57373";
            }

            var camSecure = state.Camera.SecureState;
            var micSecure = state.Microphone.SecureState;
            var bothSecureActive = camSecure == SecureProtectionState.Active && micSecure == SecureProtectionState.Active;
            var bothSecureAvailable = bothBlocked &&
                (camSecure is SecureProtectionState.Available or SecureProtectionState.Active) &&
                (micSecure is SecureProtectionState.Available or SecureProtectionState.Active);

            if (bothSecureActive)
            {
                BothSecureText = _localizationService.GetString("StatusSecureActive", "🛡️ Secure Active (Hardened)");
                BothSecureBadgeColor = "#D32F2F";
                BothSecureButtonText = _localizationService.GetString("DisableSecure", "Disable Secure");
                IsBothSecureButtonEnabled = true;
                IsBothSecureHintVisible = false;
            }
            else if (bothSecureAvailable)
            {
                BothSecureText = _localizationService.GetString("StatusSecureAvailable", "○ Available to enable");
                BothSecureBadgeColor = "#F57C00";
                BothSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                IsBothSecureButtonEnabled = true;
                IsBothSecureHintVisible = false;
            }
            else
            {
                BothSecureText = _localizationService.GetString("StatusSecureUnavailable", "🔒 Unavailable");
                BothSecureBadgeColor = "#555555";
                BothSecureButtonText = _localizationService.GetString("EnableSecure", "🔒 Enable Secure Protection");
                IsBothSecureButtonEnabled = false;
                BothSecureHint = _localizationService.GetString("SecureRequirementHint", "Enable Standard Protection first");
                IsBothSecureHintVisible = !bothBlocked;
            }

            // Overall Badge
            if (state.BothSecure)
                SecurityBadgeText = "Hardened (Secure)";
            else if (state.BothProtected)
                SecurityBadgeText = "Protected (Standard)";
            else if (state.Camera.IsProtected || state.Microphone.IsProtected)
                SecurityBadgeText = "Partially Protected";
            else
                SecurityBadgeText = "Unprotected";

            // Devices list
            var enabledStr = _localizationService.GetString("DeviceEnabled", "ENABLED");
            var disabledStr = _localizationService.GetString("DeviceDisabled", "BLOCKED");

            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new DeviceItemViewModel(d, enabledStr, disabledStr));
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    // --- Property Changed Event Handlers for ToggleSwitches ---

    partial void OnIsCameraBlockedChanged(bool value)
    {
        if (_isUpdating) return;
        _ = ExecuteCameraToggleAsync(value);
    }

    private async Task ExecuteCameraToggleAsync(bool enable)
    {
        ClearError();
        Log.Information("User toggled Camera to {Enable}", enable);

        var result = enable
            ? await _protectionService.BlockDeviceAsync(BlockTarget.Camera)
            : await _protectionService.UnblockDeviceAsync(BlockTarget.Camera);

        if (!result.Success)
        {
            ShowError(result.ErrorMessage ?? "Failed to update Camera protection");
            await RefreshStateAsync();
        }
    }

    partial void OnIsCameraStandardActiveChanged(bool value)
    {
        if (_isUpdating) return;
        IsCameraBlocked = value;
    }

    partial void OnIsMicBlockedChanged(bool value)
    {
        if (_isUpdating) return;
        _ = ExecuteMicToggleAsync(value);
    }

    private async Task ExecuteMicToggleAsync(bool enable)
    {
        ClearError();
        Log.Information("User toggled Microphone to {Enable}", enable);

        var result = enable
            ? await _protectionService.BlockDeviceAsync(BlockTarget.Microphone)
            : await _protectionService.UnblockDeviceAsync(BlockTarget.Microphone);

        if (!result.Success)
        {
            ShowError(result.ErrorMessage ?? "Failed to update Microphone protection");
            await RefreshStateAsync();
        }
    }

    partial void OnIsMicStandardActiveChanged(bool value)
    {
        if (_isUpdating) return;
        IsMicBlocked = value;
    }

    partial void OnIsBothBlockedChanged(bool value)
    {
        if (_isUpdating) return;
        _ = ExecuteBothToggleAsync(value);
    }

    private async Task ExecuteBothToggleAsync(bool enable)
    {
        ClearError();
        Log.Information("User toggled Unified Control to {Enable}", enable);

        var result = enable
            ? await _protectionService.BlockDeviceAsync(BlockTarget.Both)
            : await _protectionService.UnblockDeviceAsync(BlockTarget.Both);

        if (!result.Success)
        {
            ShowError(result.ErrorMessage ?? "Failed to update Unified protection");
            await RefreshStateAsync();
        }
    }

    partial void OnIsBothStandardActiveChanged(bool value)
    {
        if (_isUpdating) return;
        IsBothBlocked = value;
    }

    partial void OnIsAdvancedProtectionEnabledChanged(bool value)
    {
        if (_isUpdating) return;
        _ = ExecuteAdvancedProtectionToggleAsync(value);
    }

    private async Task ExecuteAdvancedProtectionToggleAsync(bool enable)
    {
        ClearError();
        Log.Information("User toggled Advanced Protection to {Enable}", enable);

        var result = await _protectionService.SetAdvancedProtectionAsync(enable);
        if (!result.Success)
        {
            ShowError(result.ErrorMessage ?? "Failed to update Advanced Protection");
            await RefreshStateAsync();
        }
    }

    // --- Legacy Relay Commands for compatibility ---

    [RelayCommand]
    private async Task ToggleCameraSecureAsync()
    {
        if (_isUpdating) return;
        await ToggleSecureAsync(
            BlockTarget.Camera,
            state => state.Camera.SecureState == SecureProtectionState.Active,
            "Camera Secure Protection error");
    }

    [RelayCommand]
    private async Task ToggleMicSecureAsync()
    {
        if (_isUpdating) return;
        await ToggleSecureAsync(
            BlockTarget.Microphone,
            state => state.Microphone.SecureState == SecureProtectionState.Active,
            "Microphone Secure Protection error");
    }

    [RelayCommand]
    private async Task ToggleBothSecureAsync()
    {
        if (_isUpdating) return;
        await ToggleSecureAsync(
            BlockTarget.Both,
            state => state.Camera.SecureState == SecureProtectionState.Active &&
                     state.Microphone.SecureState == SecureProtectionState.Active,
            "Both Secure Protection error");
    }

    private async Task ToggleSecureAsync(
        BlockTarget target,
        Func<FullProtectionState, bool> isActive,
        string fallbackError)
    {
        ClearError();
        try
        {
            var state = await _protectionService.GetCurrentStateAsync();
            var result = isActive(state)
                ? await _protectionService.DisableSecureProtectionAsync(target)
                : await _protectionService.EnableSecureProtectionAsync(target);
            if (!result.Success)
                ShowError(result.ErrorMessage ?? fallbackError);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Secure protection toggle failed before state could be verified");
            ShowError($"{fallbackError}: {ex.Message}");
        }

        await RefreshStateAsync();
    }

    [RelayCommand]
    private void SelectLanguage(string lang)
    {
        if (_isUpdating) return;
        var result = _localizationService.SetLanguage(lang);
        IsSpanishSelected = lang == "es";
        IsEnglishSelected = lang == "en";
        if (!result.Success)
            ShowError(result.ErrorMessage ?? "Failed to save language preference");
    }

    [RelayCommand]
    private void ToggleAutostart(bool enable)
    {
        if (_isUpdating) return;
        var result = _settingsService.SetAutostart(enable);
        IsAutostartEnabled = _settingsService.IsAutostartEnabled();
        if (!result.Success)
        {
            ShowError(result.ErrorMessage ?? "Failed to update autostart setting");
        }
    }

    private void ShowError(string msg)
    {
        ErrorMessage = msg;
        HasError = true;
    }

    public void ReportExternalError(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowError(message));
    }

    public event Action<SettingsSection>? OpenSettingsRequested;

    [RelayCommand]
    public void OpenSettings(string? sectionName)
    {
        var section = SettingsSection.General;
        if (!string.IsNullOrWhiteSpace(sectionName) &&
            Enum.TryParse<SettingsSection>(sectionName, true, out var parsed))
        {
            section = parsed;
        }

        OpenSettingsRequested?.Invoke(section);
    }

    [RelayCommand]
    private void ClearError()
    {
        ErrorMessage = "";
        HasError = false;
    }
}
