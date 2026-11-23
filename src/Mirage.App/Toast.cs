using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mirage.App;

/// <summary>A small glass pill near the bottom of the screen that confirms an action, then fades.</summary>
internal static class Toast
{
    static Window? _win;
    static TextBlock? _title, _msg;
    static DispatcherTimer? _timer;

    public static void Show(string title, string message)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Show(title, message)); return; }
        try
        {
            Ensure();
            _title!.Text = title;
            _msg!.Text = message;
            _msg.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            var area = SystemParameters.WorkArea;
            _win!.Opacity = 0;
            _win.Show();
            _win.UpdateLayout();
            _win.Left = area.Left + (area.Width - _win.ActualWidth) / 2;
            _win.Top = area.Bottom - _win.ActualHeight - 48;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            _win.BeginAnimation(Window.TopProperty, new DoubleAnimation(_win.Top + 14, _win.Top, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _timer!.Stop();
            _timer.Start();
        }
        catch (Exception ex) { Log.Error("toast", ex); }
    }

    static void Ensure()
    {
        if (_win != null) return;
        _title = new TextBlock { Foreground = Brushes.White, FontSize = 13.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        _msg = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xA7, 0xBC)), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.FindResource("AccentGradient"),
            Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_msg);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(text);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 10, 20, 10), Margin = new Thickness(18),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x17, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black },
            Child = row,
        };
        _win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Content = pill,
        };
        _win.MouseLeftButtonUp += (_, _) => Hide();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _timer.Tick += (_, _) => Hide();
    }

    static void Hide()
    {
        _timer?.Stop();
        if (_win == null || !_win.IsVisible) return;
        var anim = new DoubleAnimation(_win.Opacity, 0, TimeSpan.FromMilliseconds(180));
        anim.Completed += (_, _) => { if (_win.Opacity < 0.05) _win.Hide(); };
        _win.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
