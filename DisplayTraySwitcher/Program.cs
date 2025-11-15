using System;
using System.Windows.Forms;

namespace DisplayTraySwitcher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // We run with an ApplicationContext so there is no main window,
            // just a tray icon with a context menu.
            Application.Run(new TrayAppContext());
        }
    }
}