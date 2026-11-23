using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mirage.Core;

namespace Mirage.App;

/// <summary>
/// A ring of glass bubbles that pops out around the pointer. Built for touchpads:
/// glide toward a bubble (the one in that direction lights up) and tap anywhere in its slice.
/// It never takes focus, so the app underneath keeps its text selection.
/// </summary>
internal sealed class RadialMenu : Window
{
    public sealed record Item(string Label, string Glyph, string Tint, Action Run);

    const double Size = 380, Ring = 128, Bubble = 68, Hub = 58, DeadZone = 40;
    static RadialMenu? _open;

    readonly List<Item> _items;
    readonly List<(Grid Node, ScaleTransform Scale, Border Ring, Border Label)> _nodes = new();
    readonly DispatcherTimer _watch;
    readonly DateTime _shownAt = DateTime.UtcNow;
    int _hot = -1;
    bool _closing;

    public static void ShowAt(int screenX, int screenY, IReadOnlyList<Item> items)
    {
        _open?.CloseAnimated();
        var w = new RadialMenu(items.ToList());
        _open = w;
        w.Place(screenX, screenY);
        w.Show();
        w.Animate();
    }

    public static void ShowAtCursor(IReadOnlyList<Item> items)
    {
        Native.GetCursorPos(out var pt);
        ShowAt(pt.X, pt.Y, items);
    }

    RadialMenu(List<Item> items)
    {
        _items = items;
        Title = "Mirage quick menu";
        Width = Size; Height = Size;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;

        var canvas = new Canvas { Width = Size, Height = Size, Background = Brushes.Transparent };
        // soft halo so the ring reads on any wallpaper
        canvas.Children.Add(new Ellipse
        {
            Width = Size, Height = Size, IsHitTestVisible = false,
            Fill = new RadialGradientBrush(
                new GradientStopCollection { new(Color.FromArgb(0xB0, 0x08, 0x09, 0x10), 0.0), new(Color.FromArgb(0x70, 0x08, 0x09, 0x10), 0.6), new(Color.FromArgb(0x02, 0x08, 0x09, 0x10), 1.0) }),
        });

        var pos = PointerMath.RadialPositions(items.Count, Ring);
        var tint = (IValueConverter)FindResource("TintConverter");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var scale = new ScaleTransform(0.3, 0.3);
            var node = new Grid { Width = 120, Height = Bubble + 34, RenderTransformOrigin = new Point(0.5, Bubble / 2 / (Bubble + 34)), RenderTransform = new TransformGroup { Children = { scale, new TranslateTransform() } }, Opacity = 0 };
            var ring = new Border
            {
                Width = Bubble, Height = Bubble, CornerRadius = new CornerRadius(Bubble / 2), VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x24, 0x25, 0x36), Color.FromArgb(0xF0, 0x14, 0x15, 0x20), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
                Child = new Border
                {
                    Width = 40, Height = 40, CornerRadius = new CornerRadius(12),
                    Background = (Brush)tint.Convert(it.Tint, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture),
                    Child = new TextBlock { Text = it.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            var label = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 3),
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x12, 0x13, 0x1C)),
                Child = new TextBlock { Text = it.Label, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12, FontWeight = FontWeights.SemiBold },
            };
            node.Children.Add(ring);
            node.Children.Add(label);
            Canvas.SetLeft(node, Size / 2 + pos[i].X - 60);
            Canvas.SetTop(node, Size / 2 + pos[i].Y - Bubble / 2);
            canvas.Children.Add(node);
            _nodes.Add((node, scale, ring, label));
        }

        // hub: tap it to open the full launcher
        var hub = new Border
        {
            Width = Hub, Height = Hub, CornerRadius = new CornerRadius(Hub / 2), Background = (Brush)FindResource("AccentGradient"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.7, Color = Color.FromRgb(0x7C, 0x8C, 0xFF) },
            Child = new TextBlock { Text = "M", FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.2, 0.2),
        };
        Canvas.SetLeft(hub, (Size - Hub) / 2);
        Canvas.SetTop(hub, (Size - Hub) / 2);
        canvas.Children.Add(hub);
        Content = canvas;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        Loaded += (_, _) =>
        {
            var st = (ScaleTransform)hub.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        };

        MouseMove += (_, e) => SetHot(HitAt(e.GetPosition(this)));
        MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(this);
            var idx = HitAt(p);
            double dx = p.X - Size / 2, dy = p.Y - Size / 2;
            if (idx >= 0) Choose(idx);
            else if (Math.Sqrt(dx * dx + dy * dy) < DeadZone) { CloseAnimated(); Services.ShowLauncher?.Invoke(null); }
        };
        MouseRightButtonUp += (_, _) => CloseAnimated();

        // It never has focus, so watch for Esc and for clicks outside to close it
        _watch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        Closed += (_, _) => { _watch.Stop(); if (_open == this) _open = null; };
    }

    int HitAt(Point p)
    {
        double dx = p.X - Size / 2, dy = p.Y - Size / 2;
        if (Math.Sqrt(dx * dx + dy * dy) > Size / 2) return -1;
        return PointerMath.RadialHit(_items.Count, dx, dy, DeadZone);
    }

    void SetHot(int idx)
    {
        if (idx == _hot) return;
        _hot = idx;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var (node, scale, ring, label) = _nodes[i];
            double target = i == idx ? 1.14 : 1.0;
            var d = TimeSpan.FromMilliseconds(140);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, d) { EasingFunction = new CubicEase() });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, d) { EasingFunction = new CubicEase() });
            ring.BorderBrush = i == idx ? (Brush)FindResource("AccentGradient") : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            ring.BorderThickness = new Thickness(i == idx ? 2 : 1);
            label.Background = i == idx ? (Brush)FindResource("SelectionBrush") : new SolidColorBrush(Color.FromArgb(0xD8, 0x12, 0x13, 0x1C));
        }
    }

    void Animate()
    {
        var pos = PointerMath.RadialPositions(_items.Count, Ring);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var (node, scale, _, _) = _nodes[i];
            var begin = TimeSpan.FromMilliseconds(30 + i * 28);
            var dur = TimeSpan.FromMilliseconds(320);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            var shift = (TranslateTransform)((TransformGroup)node.RenderTransform).Children[1];
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-pos[i].X, 0, dur) { BeginTime = begin, EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-pos[i].Y, 0, dur) { BeginTime = begin, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, dur) { BeginTime = begin, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, dur) { BeginTime = begin, EasingFunction = ease });
            node.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = begin });
        }
    }

    void Choose(int idx)
    {
        var item = _items[idx];
        CloseAnimated();
        try { item.Run(); } catch (Exception ex) { Log.Error($"quick menu {item.Label}", ex); Toast.Show("Couldn't do that", ex.Message); }
    }

    void Watch()
    {
        if (_closing) return;
        if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) { CloseAnimated(); return; } // Esc
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 250) return;
        bool down = (Native.GetAsyncKeyState(1) & 0x8000) != 0 || (Native.GetAsyncKeyState(2) & 0x8000) != 0;
        if (down && !IsMouseOver) CloseAnimated();
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    void Place(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        double scale = Monitors.ScaleAt(x, y);
        int px = (int)Math.Round(Size * scale);
        var area = Monitors.WorkAreaAt(x, y);
        int left = Math.Clamp(x - px / 2, area.Left, Math.Max(area.Left, area.Right - px));
        int top = Math.Clamp(y - px / 2, area.Top, Math.Max(area.Top, area.Bottom - px));
        SetWindowPos(hwnd, new IntPtr(-1), left, top, px, px, 0x10 /* NOACTIVATE */);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
