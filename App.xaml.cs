using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Hadaf
{
    public partial class App : Application
    {
        // A custom data folder (HADAF_DATA_DIR) gets its own instance lock. The id must be the same in
        // every process, so it can't use string.GetHashCode (randomized per process in .NET).
        static readonly string Suffix = Environment.GetEnvironmentVariable("HADAF_DATA_DIR") is string d
            ? "." + StableId(d) : "";
        static readonly string MutexName = "Hadaf.SingleInstance.v1" + Suffix;
        static readonly string ShowEventName = "Hadaf.ShowWindow.v1" + Suffix;

        static Forms.NotifyIcon tray;
        static MainWindow main;
        Mutex mutex;
        EventWaitHandle showEvent;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, a) => LogCrash(a.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, a) => LogCrash(a.Exception);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // Already running: ask the other instance to show itself.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() => main?.ShowFromTray()));
            }) { IsBackground = true }.Start();

            // CPU rendering skips the Direct3D device and roughly halves committed memory. HADAF_GPU=1 opts back in.
            if (Environment.GetEnvironmentVariable("HADAF_GPU") != "1")
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            // A start-with-Windows entry must follow the app if its folder moved (test runs leave it alone).
            if (Environment.GetEnvironmentVariable("HADAF_DATA_DIR") == null) AutoStart.Repair();
            base.OnStartup(e);
            main = new MainWindow();
            MainWindow = main;
            CreateTray();
            if (e.Args.Contains("--tray")) main.StartHidden();
            else main.Show();
        }

        static void CreateTray()
        {
            tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "Hadaf", Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) main?.ShowFromTray(); };
            tray.BalloonTipClicked += (s, e) => main?.ShowFromTray();
            UpdateTrayMenu();
        }

        public static void UpdateTrayMenu()
        {
            if (tray == null) return;
            var menu = new Forms.ContextMenuStrip
            {
                RightToLeft = Loc.I.IsAr ? Forms.RightToLeft.Yes : Forms.RightToLeft.No,
                ShowImageMargin = false,
            };
            menu.Items.Add(Loc.I["tray_open"], null, (s, e) => main?.ShowFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Loc.I["tray_exit"], null, (s, e) => ExitApp());
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        static Drawing.Icon LoadTrayIcon()
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/hadaf.png"));
            using var src = new Drawing.Bitmap(info.Stream);
            var size = Forms.SystemInformation.SmallIconSize;
            using var bmp = new Drawing.Bitmap(src, size);
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        public static void Notify(string title, string text)
        {
            tray?.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.None);
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Store.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }

        /// <summary>
        /// Quits the app. Unless the window already confirmed (it was closed directly), the window gets a
        /// chance to keep unsaved changes, and the user can choose to stay.
        /// </summary>
        public static void ExitApp(bool confirmed = false)
        {
            if (!confirmed && main != null && !main.PrepareExit()) return;
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            Current.Shutdown();
        }

        /// <summary>FNV-1a: a short id that is identical in every process for the same text.</summary>
        static string StableId(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }
}
