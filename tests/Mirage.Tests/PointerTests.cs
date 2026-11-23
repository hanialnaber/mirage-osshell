using Mirage.Core;
using Xunit;

namespace Mirage.Tests;

public class PointerTests
{
    [Theory]
    [InlineData(0, 0, Corner.TopLeft, true)]
    [InlineData(2, 1, Corner.TopLeft, true)]
    [InlineData(5, 0, Corner.TopLeft, false)]
    [InlineData(1919, 0, Corner.TopRight, true)]
    [InlineData(1919, 0, Corner.TopLeft, false)]
    [InlineData(0, 1079, Corner.BottomLeft, true)]
    [InlineData(1918, 1078, Corner.BottomRight, true)]
    [InlineData(0, 0, Corner.Off, false)]
    public void Corners_on_a_single_monitor(int x, int y, Corner c, bool expected) =>
        Assert.Equal(expected, PointerMath.InCorner(0, 0, 1920, 1080, x, y, c));

    [Fact]
    public void Corners_on_a_second_monitor_use_its_own_rectangle()
    {
        Assert.True(PointerMath.InCorner(1920, -200, 4480, 1240, 1920, -200, Corner.TopLeft));
        Assert.False(PointerMath.InCorner(1920, -200, 4480, 1240, 1919, -200, Corner.TopLeft));
    }

    [Fact]
    public void Radial_positions_start_at_the_top_and_go_clockwise()
    {
        var p = PointerMath.RadialPositions(4, 100);
        Assert.Equal((0.0, -100.0), (Math.Round(p[0].X, 6) + 0.0, p[0].Y));
        Assert.Equal(100.0, p[1].X, 3);
        Assert.Equal(100.0, p[2].Y, 3);
        Assert.Equal(-100.0, p[3].X, 3);
    }

    [Theory]
    [InlineData(0, -80, 0)]
    [InlineData(80, 0, 1)]
    [InlineData(0, 80, 2)]
    [InlineData(-80, 0, 3)]
    [InlineData(70, -60, 1)]
    [InlineData(5, 5, -1)]
    public void Radial_hit_picks_the_nearest_item(double dx, double dy, int expected) =>
        Assert.Equal(expected, PointerMath.RadialHit(4, dx, dy, 30));

    [Fact]
    public void Sections_wrap_around()
    {
        Assert.Equal(HomeSection.Workspaces, PointerMath.Next(HomeSection.Apps, 1));
        Assert.Equal(HomeSection.Ai, PointerMath.Next(HomeSection.Apps, -1));
        Assert.Equal(HomeSection.Apps, PointerMath.Next(HomeSection.Ai, 1));
    }

    [Fact]
    public void Swipe_fires_once_per_gesture()
    {
        var s = new SwipeTracker(240, 450, 180);
        var t = new DateTime(2026, 1, 1);
        int fired = 0;
        for (int i = 0; i < 20; i++) fired += s.Feed(40, t.AddMilliseconds(i * 10));
        Assert.Equal(1, fired);
        // after the cooldown a new swipe the other way fires again
        int back = 0;
        for (int i = 0; i < 10; i++) back += s.Feed(-60, t.AddMilliseconds(800 + i * 10));
        Assert.Equal(-1, back);
    }

    [Fact]
    public void Small_jitter_does_not_switch_sections()
    {
        var s = new SwipeTracker();
        var t = new DateTime(2026, 1, 1);
        int fired = 0;
        for (int i = 0; i < 30; i++) fired += s.Feed(i % 2 == 0 ? 30 : -30, t.AddMilliseconds(i * 10));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Settings_round_trip_pointer_options()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mirage-ptr-{Guid.NewGuid():N}.json");
        var s = Settings.CreateDefault();
        s.HotCorner = Corner.BottomRight; s.QuickMenuButton = PointerButton.Middle; s.HomeView = HomeView.List; s.HotCornerDelayMs = 99999;
        s.Normalize();
        s.Save(path);
        var back = Settings.Load(path);
        File.Delete(path);
        Assert.Equal(Corner.BottomRight, back.HotCorner);
        Assert.Equal(PointerButton.Middle, back.QuickMenuButton);
        Assert.Equal(HomeView.List, back.HomeView);
        Assert.Equal(3000, back.HotCornerDelayMs);
        Assert.Equal(Corner.TopLeft, Settings.CreateDefault().HotCorner);
    }
}
