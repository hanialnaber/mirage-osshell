namespace Mirage.Core;

public enum Corner { Off, TopLeft, TopRight, BottomLeft, BottomRight }
public enum PointerButton { None, Middle, Back, Forward }
public enum HomeView { Tiles, List }
public enum HomeSection { Apps, Workspaces, Clipboard, Notes, Ai }

/// <summary>Pure geometry and gesture math behind the touchpad and mouse features, kept here so it can be tested.</summary>
public static class PointerMath
{
    /// <summary>True when (x, y) is within <paramref name="tolerance"/> pixels of the given corner of a monitor rectangle (right/bottom exclusive).</summary>
    public static bool InCorner(int left, int top, int right, int bottom, int x, int y, Corner corner, int tolerance = 2)
    {
        if (corner == Corner.Off) return false;
        bool nearLeft = x <= left + tolerance && x >= left;
        bool nearRight = x >= right - 1 - tolerance && x <= right - 1;
        bool nearTop = y <= top + tolerance && y >= top;
        bool nearBottom = y >= bottom - 1 - tolerance && y <= bottom - 1;
        return corner switch
        {
            Corner.TopLeft => nearLeft && nearTop,
            Corner.TopRight => nearRight && nearTop,
            Corner.BottomLeft => nearLeft && nearBottom,
            Corner.BottomRight => nearRight && nearBottom,
            _ => false,
        };
    }

    /// <summary>Centers of <paramref name="count"/> items evenly spaced on a circle, starting at <paramref name="startDegrees"/> (−90 is straight up), clockwise.</summary>
    public static (double X, double Y)[] RadialPositions(int count, double radius, double startDegrees = -90)
    {
        var result = new (double, double)[Math.Max(0, count)];
        for (int i = 0; i < result.Length; i++)
        {
            var a = (startDegrees + 360.0 * i / count) * Math.PI / 180;
            result[i] = (Math.Round(Math.Cos(a) * radius, 3), Math.Round(Math.Sin(a) * radius, 3));
        }
        return result;
    }

    /// <summary>Index of the radial item nearest to an offset from the center, or −1 inside the dead zone.</summary>
    public static int RadialHit(int count, double dx, double dy, double deadZone, double startDegrees = -90)
    {
        if (count <= 0 || Math.Sqrt(dx * dx + dy * dy) < deadZone) return -1;
        var deg = Math.Atan2(dy, dx) * 180 / Math.PI - startDegrees;
        var step = 360.0 / count;
        var idx = (int)Math.Floor(((deg % 360 + 360) % 360 + step / 2) / step) % count;
        return idx;
    }

    public static HomeSection Next(HomeSection s, int delta)
    {
        var n = Enum.GetValues<HomeSection>().Length;
        return (HomeSection)((((int)s + delta) % n + n) % n);
    }
}

/// <summary>
/// Turns a stream of horizontal scroll deltas (two-finger touchpad swipes arrive as WM_MOUSEHWHEEL)
/// into discrete "next" / "previous" steps, with a cooldown so one swipe moves one section.
/// </summary>
public sealed class SwipeTracker
{
    readonly int _threshold;
    readonly TimeSpan _cooldown, _idleReset;
    double _acc;
    DateTime _last = DateTime.MinValue, _fired = DateTime.MinValue;

    public SwipeTracker(int threshold = 240, int cooldownMs = 450, int idleResetMs = 180)
    {
        _threshold = threshold;
        _cooldown = TimeSpan.FromMilliseconds(cooldownMs);
        _idleReset = TimeSpan.FromMilliseconds(idleResetMs);
    }

    /// <summary>Feed one delta (positive = fingers moving right / content left). Returns +1, −1 or 0.</summary>
    public int Feed(int delta, DateTime now)
    {
        if (now - _last > _idleReset) _acc = 0;
        _last = now;
        if (now - _fired < _cooldown) { _acc = 0; return 0; }
        if (_acc != 0 && Math.Sign(delta) != Math.Sign(_acc)) _acc = 0;
        _acc += delta;
        if (Math.Abs(_acc) < _threshold) return 0;
        var dir = Math.Sign(_acc);
        _acc = 0;
        _fired = now;
        return dir;
    }
}
