using System.Drawing;
using System.Windows.Forms;

namespace Notchle.Windows.Shell;

/// The notification-area icon: the only way to reach Notchle when the island is collapsed.
/// WinForms NotifyIcon (UseWindowsForms), menu callbacks arrive on the WPF UI thread because
/// the WinForms message loop is the WPF dispatcher's.
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startWithWindows;

    public TrayIcon(
        Action newLink,
        Action connectSpotify,
        Action resetProgress,
        Func<bool> isStartWithWindows,
        Action<bool> setStartWithWindows,
        Action quit)
    {
        _startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _startWithWindows.Click += (_, _) =>
        {
            setStartWithWindows(!_startWithWindows.Checked);
            _startWithWindows.Checked = isStartWithWindows();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("New link…", null, (_, _) => newLink());
        menu.Items.Add("Connect Spotify…", null, (_, _) => connectSpotify());
        menu.Items.Add("Reset progress", null, (_, _) => resetProgress());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Notchle", null, (_, _) => quit());
        menu.Opening += (_, _) => _startWithWindows.Checked = isStartWithWindows();

        _icon = new NotifyIcon
        {
            Text = "Notchle",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) newLink(); };
    }

    public void ShowBalloon(string title, string text, bool error = false) =>
        _icon.ShowBalloonTip(5000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    /// The exe's own icon when it has one, else the stock application icon.
    private static Icon LoadIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(AppPaths.ExecutablePath) ?? SystemIcons.Application;
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }
}
