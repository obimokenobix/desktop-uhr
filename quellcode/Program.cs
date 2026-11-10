using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;


namespace DesktopUhr
{
    public static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void Run(string scriptPath)
        {
            ClockForm.ScriptPath = scriptPath ?? "";
            bool created;
            using (Mutex mutex = new Mutex(true, "DesktopUhr_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Die Desktop-Uhr läuft bereits.\nRechtsklick auf die Uhr (oder das Symbol im Infobereich) öffnet die Einstellungen.",
                        "Desktop-Uhr", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
                Application.Run(new ClockForm(Settings.Load()));
            }
        }
    }
}
