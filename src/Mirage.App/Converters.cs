using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Mirage.App;

/// <summary>Maps a tint key to the gradient used for a result's glyph tile.</summary>
internal sealed class TintConverter : IValueConverter
{
    static readonly Dictionary<string, (string A, string B)> Map = new()
    {
        ["blue"] = ("#5B8CFF", "#3A5BD9"),
        ["violet"] = ("#9D7CFF", "#6A4CE0"),
        ["teal"] = ("#34D3C0", "#1C8F8A"),
        ["amber"] = ("#FFB547", "#D9822B"),
        ["rose"] = ("#FF6B9A", "#C93B6E"),
        ["green"] = ("#4ADE80", "#1F9D57"),
        ["slate"] = ("#7C8398", "#4A5063"),
    };
    static readonly Dictionary<string, Brush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "violet";
        if (Cache.TryGetValue(key, out var b)) return b;
        var (a, c) = Map.TryGetValue(key, out var v) ? v : Map["violet"];
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(c), 45);
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
