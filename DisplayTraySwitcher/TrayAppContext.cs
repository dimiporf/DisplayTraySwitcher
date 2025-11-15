using System;
using System.Drawing;
using System.Windows.Forms;

namespace DisplayTraySwitcher
{
    /// <summary>
    /// Application context hosting a tray icon with a context menu for
    /// switching between predefined multi-monitor layouts:
    /// - Main screen only
    /// - Main + its above screen
    /// - All screens
    /// 
    /// After each action it shows a balloon tooltip indicating whether the
    /// layout seems to have been applied successfully (based on what Windows
    /// reports afterwards).
    /// </summary>
    public class TrayAppContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly DisplayManager _displayManager;

        public TrayAppContext()
        {
            _displayManager = new DisplayManager();

            var menu = new ContextMenuStrip();
            var mainOnly = new ToolStripMenuItem("Main screen only", null, MainOnly_Click);
            var mainAndAbove = new ToolStripMenuItem("Main and above", null, MainAndAbove_Click);
            var allScreens = new ToolStripMenuItem("All screens", null, AllScreens_Click);
            var sep = new ToolStripSeparator();
            var exit = new ToolStripMenuItem("Exit", null, Exit_Click);

            menu.Items.Add(mainOnly);
            menu.Items.Add(mainAndAbove);
            menu.Items.Add(allScreens);
            menu.Items.Add(sep);
            menu.Items.Add(exit);

            _trayIcon = new NotifyIcon
            {
                Icon = new Icon("display_tray_switcher_icon.ico"),
                ContextMenuStrip = menu,
                Visible = true,
                Text = "Display Tray Switcher"
            };

            _trayIcon.MouseUp += TrayIcon_MouseUp;
        }

        private void TrayIcon_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _trayIcon.ContextMenuStrip?.Show(Cursor.Position);
            }
        }

        private async void MainOnly_Click(object sender, EventArgs e)
        {
            var result = await _displayManager.ApplyMainOnlyAsync();
            ShowResultBalloon(result);
        }

        private async void MainAndAbove_Click(object sender, EventArgs e)
        {
            var result = await _displayManager.ApplyMainAndAboveAsync();
            ShowResultBalloon(result);
        }

        private async void AllScreens_Click(object sender, EventArgs e)
        {
            var result = await _displayManager.ApplyAllScreensAsync();
            ShowResultBalloon(result);
        }

        private void ShowResultBalloon(DisplayManager.LayoutResult result)
        {
            if (result == null || _trayIcon == null)
                return;

            string title = result.LayoutName ?? "Display layout";
            string text = result.Message ?? string.Empty;
            var icon = result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning;

            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = text;
            _trayIcon.BalloonTipIcon = icon;

            _trayIcon.ShowBalloonTip(3000);
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            ExitThread();
        }
    }
}