using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CamMicBlocker.UI.Notification;

/// <summary>
/// Modern overlay notification window.
/// Appears near the bottom-right corner of the primary screen,
/// fades in, stays visible briefly, then fades out and closes.
/// 
/// All animations use WPF Storyboards (not WinForms Timers) for
/// proper resource management and smooth rendering.
/// </summary>
public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    public NotificationWindow(string message, bool isBlocked)
    {
        InitializeComponent();

        MessageText.Text = message;
        MessageText.Foreground = new SolidColorBrush(
            isBlocked ? System.Windows.Media.Color.FromRgb(205, 92, 92) : System.Windows.Media.Color.FromRgb(144, 238, 144));

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            FadeOutAndClose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position near bottom-right of primary screen
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;

        // Fade in
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        fadeIn.Completed += (_, _) => _closeTimer.Start();
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void FadeOutAndClose()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Static helper to show a notification on the UI thread.
    /// </summary>
    public static void Show(string message, bool isBlocked)
    {
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var notification = new NotificationWindow(message, isBlocked);
                notification.Show();
            });
        }
    }
}
