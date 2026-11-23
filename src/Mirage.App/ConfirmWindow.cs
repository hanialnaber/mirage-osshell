using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mirage.Core.Agent;

namespace Mirage.App;

/// <summary>Asks the user to approve an AI agent's action. Denies by itself after 60 seconds.</summary>
internal sealed class ConfirmWindow : Window
{
    public static bool IsOpen { get; private set; }
    readonly TaskCompletionSource<ConfirmResult> _tcs = new();
    readonly DispatcherTimer _timer;
    int _secondsLeft = 60;
    readonly TextBlock _countdown;

    public static Task<ConfirmResult> AskAsync(string client, string tool, string summary)
    {
        var w = new ConfirmWindow(client, tool, summary);
        IsOpen = true;
        w.Show();
        w.Activate();
        Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(w).Handle);
        return w._tcs.Task;
    }

    ConfirmWindow(string client, string tool, string summary)
    {
        Title = "Mirage: approve agent action";
        Width = 460; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");

        var text = (Brush)FindResource("TextBrush");
        var subtle = (Brush)FindResource("SubtleTextBrush");
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("AccentGradient"), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = Brushes.White, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "An AI agent wants to act", Foreground = text, FontSize = 15, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = $"{client} · {tool}", Foreground = subtle, FontSize = 12 });
        head.Children.Add(titles);
        stack.Children.Add(head);

        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock { Text = summary, Foreground = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 },
        });

        _countdown = new TextBlock { Foreground = (Brush)FindResource("FaintTextBrush"), FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0) };
        stack.Children.Add(_countdown);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button B(string label, string style, ConfirmResult r)
        {
            var b = new Button { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(8, 0, 0, 0), MinWidth = 96 };
            b.Click += (_, _) => Finish(r);
            buttons.Children.Add(b);
            return b;
        }
        B("Deny", "GhostButton", ConfirmResult.Deny);
        B("Always allow", "GhostButton", ConfirmResult.AlwaysAllow);
        var once = B("Allow once", "AccentButton", ConfirmResult.AllowOnce);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(16), Padding = new Thickness(20), CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(Color.FromArgb(0xFA, 0x1C, 0x1D, 0x2B), Color.FromArgb(0xFA, 0x10, 0x11, 0x19), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Color = Colors.Black },
            Child = stack,
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(ConfirmResult.Deny); };
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        Loaded += (_, _) => Keyboard.Focus(once);
        Closed += (_, _) => { IsOpen = false; _timer!.Stop(); _tcs.TrySetResult(ConfirmResult.Deny); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (--_secondsLeft <= 0) Finish(ConfirmResult.Deny); else UpdateCountdown(); };
        UpdateCountdown();
        _timer.Start();
    }

    void UpdateCountdown() => _countdown.Text = $"Denied automatically in {_secondsLeft}s. Change what agents may do in Settings > AI agents.";

    void Finish(ConfirmResult r)
    {
        _tcs.TrySetResult(r);
        Close();
    }
}
