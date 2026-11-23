using Mirage.Core;
using WinForms = System.Windows.Forms;

namespace Mirage.App;

/// <summary>Notification-area icon with a small menu.</summary>
internal sealed class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _bridgeItem;

    public event Action? OpenLauncher, OpenSettings, Quit, ToggleBridge;

    public TrayService()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open Mirage", null, (_, _) => OpenLauncher?.Invoke());
        menu.Items.Add("Quick menu", null, (_, _) => Services.ShowQuickMenu?.Invoke());
        menu.Items.Add("Clipboard history", null, (_, _) => Services.ShowSection?.Invoke(HomeSection.Clipboard));
        menu.Items.Add("Notes", null, (_, _) => Services.ShowLauncher?.Invoke("notes"));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _bridgeItem = new WinForms.ToolStripMenuItem("AI agent bridge", null, (_, _) => ToggleBridge?.Invoke());
        menu.Items.Add(_bridgeItem);
        menu.Items.Add("Settings", null, (_, _) => OpenSettings?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Mirage", null, (_, _) => Quit?.Invoke());

        System.Drawing.Icon? ico = null;
        try { ico = System.Drawing.Icon.ExtractAssociatedIcon(Paths.Exe); } catch { }
        _icon = new WinForms.NotifyIcon
        {
            Icon = ico ?? System.Drawing.SystemIcons.Application,
            Text = "Mirage",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenLauncher?.Invoke(); };
    }

    public void SetBridgeState(bool on) => _bridgeItem.Checked = on;

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Balloon(string title, string message)
    {
        try { _icon.ShowBalloonTip(4000, title, string.IsNullOrWhiteSpace(message) ? " " : message, WinForms.ToolTipIcon.None); }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
