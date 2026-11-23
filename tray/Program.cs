using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class TrayApp
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);

    static Icon FanIcon()
    {
        using (var bitmap = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(25, 104, 83)))
        using (var blade = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(background, 1, 1, 30, 30);
            g.TranslateTransform(16, 16);
            blade.AddBezier(0, -3, -4, -13, 5, -14, 9, -10);
            blade.AddBezier(9, -10, 6, -7, 4, -3, 1, -2);
            blade.CloseFigure();
            for (int i = 0; i < 4; i++) { g.FillPath(Brushes.White, blade); g.RotateTransform(90); }
            g.FillEllipse(Brushes.White, -2, -2, 4, 4);
            IntPtr handle = bitmap.GetHicon();
            try { using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
        }
    }

    static Process launching;
    static void OpenMonitor(NotifyIcon tray)
    {
        try
        {
            int session;
            using (var self = Process.GetCurrentProcess()) session = self.SessionId;
            foreach (var p in Process.GetProcessesByName("CaseCooling.Monitor"))
            {
                using (p)
                {
                    if (p.SessionId != session) continue;
                    var window = p.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (IsIconic(window)) ShowWindowAsync(window, 9);
                    SetForegroundWindow(window);
                    return;
                }
            }
            if (launching != null && !launching.HasExited) return;
            if (launching != null) launching.Dispose();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CaseCooling");
            launching = Process.Start(new ProcessStartInfo(Path.Combine(dir, "CaseCooling.Monitor.exe")) { WorkingDirectory = dir });
        }
        catch (Exception e) { tray.ShowBalloonTip(5000, "CaseCooling", e.Message, ToolTipIcon.Error); }
    }

    [STAThread] static void Main()
    {
        bool first;
        using (var guard = new Mutex(true, @"Local\CaseCooling.MonitorTray", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            using (var icon = FanIcon())
            using (var menu = new ContextMenuStrip())
            using (var tray = new NotifyIcon())
            {
                tray.Icon = icon;
                tray.Text = "CaseCooling · Температуры и обороты";
                menu.Items.Add("Открыть графики", null, delegate { OpenMonitor(tray); });
                menu.Items.Add("Закрыть значок", null, delegate { Application.ExitThread(); });
                tray.ContextMenuStrip = menu;
                tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenMonitor(tray); };
                tray.Visible = true;
                Application.Run();
                tray.Visible = false;
            }
        }
    }
}
