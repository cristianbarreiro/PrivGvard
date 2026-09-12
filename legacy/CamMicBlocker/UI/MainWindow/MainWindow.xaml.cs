using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CamMicBlocker.Application;
using CamMicBlocker.Domain.Models;
using Serilog;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace CamMicBlocker.UI.MainWindow;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainWindow>();

    private readonly BlockingService _blockingService;
    private readonly StartupService _startupService;
    private readonly LanguageService _languageService;
    private bool _isUpdatingUi;

    public MainWindow(BlockingService blockingService, StartupService startupService, LanguageService languageService)
    {
        InitializeComponent();

        _blockingService = blockingService;
        _startupService = startupService;
        _languageService = languageService;

        // Sync Language Radios with current language state
        _isUpdatingUi = true;
        try
        {
            if (_languageService.CurrentLanguage == "en")
                LangEnRadio.IsChecked = true;
            else
                LangEsRadio.IsChecked = true;
        }
        finally
        {
            _isUpdatingUi = false;
        }

        _blockingService.StateChanged += OnStateChanged;
        _languageService.LanguageChanged += OnLanguageChanged;

        RefreshState();
    }

    public void RefreshState()
    {
        var state = _blockingService.GetCurrentState();
        UpdateUiFromState(state);
    }

    private void OnLanguageChanged(string langCode)
    {
        Dispatcher.BeginInvoke(RefreshState);
    }

    private void OnStateChanged(BlockState state)
    {
        Dispatcher.BeginInvoke(() => UpdateUiFromState(state));
    }

    private void UpdateUiFromState(BlockState state)
    {
        _isUpdatingUi = true;
        try
        {
            var blockedStr = _languageService.GetString("StatusBlocked", "🔒 Blocked");
            var allowedStr = _languageService.GetString("StatusAllowed", "✅ Allowed");

            // Camera
            bool cameraBlocked = state.Camera.EffectiveStatus == BlockStatus.Blocked;
            CameraToggle.IsChecked = cameraBlocked;
            CameraStatusText.Text = cameraBlocked ? blockedStr : allowedStr;
            CameraStatusText.Foreground = cameraBlocked ? Brushes.IndianRed : Brushes.LightGreen;

            // Microphone
            bool micBlocked = state.Microphone.EffectiveStatus == BlockStatus.Blocked;
            MicToggle.IsChecked = micBlocked;
            MicStatusText.Text = micBlocked ? blockedStr : allowedStr;
            MicStatusText.Foreground = micBlocked ? Brushes.IndianRed : Brushes.LightGreen;

            // Master (Both)
            bool bothBlocked = state.AllBlocked;
            MasterToggle.IsChecked = bothBlocked;
            MasterStatusText.Text = bothBlocked
                ? _languageService.GetString("ProtectionActive", "Protection active (Both Blocked)")
                : state.AllAllowed
                    ? _languageService.GetString("ProtectionInactive", "Protection inactive (Both Allowed)")
                    : _languageService.GetString("MixedState", "Mixed state");

            // Update detected devices list
            var enabledStr = _languageService.GetString("DeviceEnabled", "ENABLED");
            var disabledStr = _languageService.GetString("DeviceDisabled", "DISABLED");
            var devices = _blockingService.GetDetectedDevices();
            DevicesListView.ItemsSource = devices.Select(d => new DeviceItemViewModel(d, enabledStr, disabledStr)).ToList();
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void OnLangEsChecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _languageService.SetLanguage("es");
    }

    private void OnLangEnChecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _languageService.SetLanguage("en");
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Log.Debug("MainWindow hidden to tray via custom close button");
    }

    private async void OnMasterToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        bool targetBlock = MasterToggle.IsChecked == true;
        var target = BlockTarget.Both;
        Log.Information("UI Master toggle clicked: Block={TargetBlock}", targetBlock);

        var result = targetBlock
            ? await _blockingService.BlockAsync(target)
            : await _blockingService.UnblockAsync(target);

        if (!result.Success)
        {
            MessageBox.Show($"Operation failed:\n{result.ErrorMessage}", "CamMicBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshState();
        }
    }

    private async void OnCameraToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        bool targetBlock = CameraToggle.IsChecked == true;
        Log.Information("UI Camera toggle clicked: Block={TargetBlock}", targetBlock);

        var result = targetBlock
            ? await _blockingService.BlockAsync(BlockTarget.Camera)
            : await _blockingService.UnblockAsync(BlockTarget.Camera);

        if (!result.Success)
        {
            MessageBox.Show($"Operation failed:\n{result.ErrorMessage}", "CamMicBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshState();
        }
    }

    private async void OnMicToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        bool targetBlock = MicToggle.IsChecked == true;
        Log.Information("UI Microphone toggle clicked: Block={TargetBlock}", targetBlock);

        var result = targetBlock
            ? await _blockingService.BlockAsync(BlockTarget.Microphone)
            : await _blockingService.UnblockAsync(BlockTarget.Microphone);

        if (!result.Success)
        {
            MessageBox.Show($"Operation failed:\n{result.ErrorMessage}", "CamMicBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshState();
        }
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        Log.Debug("MainWindow hidden to tray");
    }

    private sealed class DeviceItemViewModel
    {
        public string FriendlyName { get; }
        public string InstanceId { get; }
        public string StatusText { get; }
        public System.Windows.Media.Brush StatusColor { get; }

        public DeviceItemViewModel(DeviceInfo device, string enabledText, string disabledText)
        {
            FriendlyName = $"[{device.DeviceType}] {device.FriendlyName}";
            InstanceId = device.InstanceId;
            StatusText = device.IsEnabled ? enabledText : disabledText;
            StatusColor = device.IsEnabled ? Brushes.LightGreen : Brushes.IndianRed;
        }
    }
}
