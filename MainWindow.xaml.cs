using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Quest
{
    public partial class MainWindow : Window
    {

        readonly AppData data;
        Settings S => data.Settings;
        Loc L => Loc.I;

        readonly List<NavItem> nav = new();
        readonly List<NavItem> cats = new();
        List<object> entries = new();
        string view = "all";
        string cat;
        bool doneExpanded, searchOpen, drawerOpen;
        FrameworkElement page;

        Goal draft, original;
        DateTime calMonth;
        bool syncing;               // editor fields are being filled from code, not by the user
        Recurrence shownRepeat;     // what the editor's repeat picker currently shows
        bool shownAnyTime;

        Goal undoGoal;
        int undoIndex;
        Action snackAction;
        readonly DispatcherTimer snackTimer;
        readonly DispatcherTimer ticker;
        bool exiting, suppress;
        bool exitFromTray;          // App.ExitApp is driving the shutdown
        Exception saveError;        // last failed save; retried every tick until it goes through
        string noticeKey;           // which message the notice bar is showing ("load" / "save")
        Action noticeAction;
        AppData importFile;         // the file the import page lists
        List<ImportRow> importRows;

        public MainWindow()
        {
            data = Store.Load();
            Loc.I.Set(S.Lang);
            InitializeComponent();
            page = HomePage;

            nav.Add(new NavItem { Key = "all", Icon = "" });
            nav.Add(new NavItem { Key = "today", Icon = "" });
            nav.Add(new NavItem { Key = "daily", Icon = "" });
            nav.Add(new NavItem { Key = "upcoming", Icon = "" });
            nav.Add(new NavItem { Key = "overdue", Icon = "" });
            nav.Add(new NavItem { Key = "done", Icon = "" });
            nav.Add(new NavItem { Key = "stats", Icon = "", ShowCount = false });
            Planner.Prepare(data);
            NavList.ItemsSource = nav;
            suppress = true;
            NavList.SelectedIndex = 0;
            suppress = false;

            LoadBounds();
            Theme.Apply(this, S);
            SourceInitialized += (s, e) =>
            {
                Theme.HideNativeCaptionButtons(this);
                Theme.Apply(this, S);
            };
            StateChanged += OnStateChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

            snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            snackTimer.Tick += (s, e) => HideSnack();

            // One cheap tick every 30 s: countdowns, daily renewals, reminders.
            ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            ticker.Tick += (s, e) => Tick();
            ticker.Start();

            if (Planner.RollDailies(data.Goals, DateTime.Now)) Save();
            ApplyLanguage();
            ApplyTextScale();
            ReportLoadIssue();
            WarnIfInsideAnotherApp();
            OfferStartWithWindows();
            Dispatcher.BeginInvoke(new Action(CheckReminders), DispatcherPriority.ApplicationIdle);
        }

        // ================= window / tray =================

        void LoadBounds()
        {
            var work = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, work.Height - 24);
            if (S.WinWidth >= MinWidth && S.WinHeight >= MinHeight)
            {
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                // Only reuse the saved spot if the title bar is still on a visible screen.
                if (screen.Contains(new Point(S.WinLeft + 60, S.WinTop + 20)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = S.WinLeft;
                    Top = S.WinTop;
                    Width = S.WinWidth;
                    Height = S.WinHeight;
                    return;
                }
            }
            // First run: a tall column that always fits the work area (small laptops included).
            Height = Math.Max(MinHeight, Math.Min(760, work.Height - 64));
        }

        void RememberBounds()
        {
            if (!IsLoaded) return;
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (r.IsEmpty || r.Width <= 0) return;
            S.WinLeft = r.Left;
            S.WinTop = r.Top;
            S.WinWidth = r.Width;
            S.WinHeight = r.Height;
        }

        public void StartHidden()
        {
            // Window stays hidden; reminders keep running from the tray. A data problem still needs telling,
            // and so does running inside another app.
            if (Store.Issue != null || HostPackage.Name != null) App.Notify(NoticeTitle.Text, NoticeText.Text);
            Native.Trim();
        }

        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Tick();
        }

        /// <summary>Called by the tray's Exit. False when the user chose to stay because changes are unsaved.</summary>
        public bool PrepareExit()
        {
            RememberBounds();
            if (!ConfirmQuitWithUnsaved()) return false;
            exiting = exitFromTray = true;
            StopForExit();
            Close();
            return true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            RememberBounds();
            if (!exiting && S.CloseToTray)
            {
                e.Cancel = true;
                Save();
                Hide();
                if (!S.TrayHintShown)
                {
                    App.Notify(L["app"], L["tray_running"]);
                    S.TrayHintShown = true;
                    Save();
                }
                Native.Trim();
                return;
            }
            if (!exiting)
            {
                // Closing the window quits the app (run in background is off).
                if (!ConfirmQuitWithUnsaved())
                {
                    e.Cancel = true;
                    return;
                }
                exiting = true;
                StopForExit();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (!exitFromTray) App.ExitApp(confirmed: true);
        }

        void StopForExit()
        {
            ticker.Stop();
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            Save();
        }

        /// <summary>Last chance to keep changes that could not be saved; true means go ahead and quit.</summary>
        bool ConfirmQuitWithUnsaved()
        {
            Save();
            if (saveError == null) return true;
            var options = L.IsAr ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
            return MessageBox.Show(L.F("exit_unsaved_text", Describe(saveError)), L["save_failed_title"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, options) == MessageBoxResult.Yes;
        }

        void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                var b = SystemParameters.WindowResizeBorderThickness;
                Root.Margin = new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4);
                MaxBtn.Content = "";
            }
            else
            {
                Root.Margin = new Thickness(0);
                MaxBtn.Content = "";
            }
            if (WindowState == WindowState.Minimized) Native.Trim();
        }

        void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color
                && e.Category != UserPreferenceCategory.Accessibility) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Theme.Apply(this, S);
                ApplyTextScale();
                RefreshCards();
                FillAccentChips(); // the Windows colour may have changed too
                SyncSettingsUi();
            }));
        }

        /// <summary>
        /// Follows Windows' "Text size" accessibility setting (WPF ignores it on its own) by zooming the whole
        /// window content, so larger text never gets clipped.
        /// </summary>
        void ApplyTextScale()
        {
            double factor = 1;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                if (k?.GetValue("TextScaleFactor") is int percent && percent > 100) factor = Math.Min(2.25, percent / 100.0);
            }
            catch { }
            SetTextScale(factor);
        }

        double baseMinWidth, baseMinHeight; // the window's smallest size at 100% text

        void SetTextScale(double factor)
        {
            Root.LayoutTransform = factor > 1 ? new ScaleTransform(factor, factor) : Transform.Identity;
            // Zooming takes room from the layout, so the smallest window grows with it (as far as the screen allows);
            // otherwise cards would be cut off at the edge.
            if (baseMinWidth == 0) (baseMinWidth, baseMinHeight) = (MinWidth, MinHeight);
            var work = SystemParameters.WorkArea;
            MinWidth = Math.Min(baseMinWidth * factor, work.Width);
            MinHeight = Math.Min(baseMinHeight * factor, work.Height - 24);
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Escape)
            {
                if (drawerOpen) CloseDrawer();
                else if (page == EditorPage) CloseEditor();
                else if (page == ImportPage) CloseImport();
                else if (page == SettingsPage) CloseSettings();
                else if (searchOpen) ToggleSearch(false);
                else return;
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.N && page == HomePage) { OpenEditor(null); e.Handled = true; }
            else if (ctrl && e.Key == Key.F && page == HomePage) { ToggleSearch(true); e.Handled = true; }
            else if (ctrl && e.Key == Key.Enter && page == EditorPage) { SaveDraft(); e.Handled = true; }
        }

        // ================= pages =================

        /// <summary>Slides the next page in from the reading direction and fades the current one out.</summary>
        void ShowPage(FrameworkElement next)
        {
            if (next == page) return;
            var prev = page;
            // Pages open from the list, and the import page from settings; anything else is a way back.
            bool forward = prev == HomePage || next == ImportPage;
            page = next;
            // The menu belongs to the list; while editing or in settings, leaving goes through Back/Close
            // (so a half-written goal can't be dropped by jumping elsewhere).
            MenuBtn.IsEnabled = next == HomePage;
            next.Visibility = Visibility.Visible;
            Slide(next, 0, 1, forward ? 32 : -32, 0, null);
            Slide(prev, 1, 0, 0, forward ? -32 : 32, () =>
            {
                if (page == prev) return;
                prev.Visibility = Visibility.Collapsed;
                // Unhook the editor from the goal it showed, so its pickers can no longer write into it.
                if (prev == EditorPage) EditorForm.DataContext = null;
            });
            PlaceSnack();
        }

        // Each animation frame repaints whatever changed, and with CPU rendering that repaint is the whole cost
        // of a transition. 30 fps over ~150 ms still reads as smooth, for a fraction of the work of 60 fps.
        const int AnimFps = 30;

        static void Slide(FrameworkElement el, double fromOpacity, double toOpacity, double fromX, double toX, Action done)
        {
            var time = TimeSpan.FromMilliseconds(150);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(fromOpacity, toOpacity, time) { EasingFunction = ease };
            var move = new DoubleAnimation(fromX, toX, time) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, AnimFps);
            Timeline.SetDesiredFrameRate(move, AnimFps);
            if (done != null) fade.Completed += (s, e) => done();
            el.RenderTransform.BeginAnimation(TranslateTransform.XProperty, move);
            el.BeginAnimation(OpacityProperty, fade);
        }

        // ================= menu & search =================

        void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (drawerOpen) CloseDrawer();
            else OpenDrawer();
        }

        void OpenDrawer()
        {
            drawerOpen = true;
            // The dim layer appears at once: fading it would repaint the whole window on every frame. The
            // blurred shadow is the costliest thing to repaint, so it only comes in once the sheet has stopped.
            DrawerOverlay.Visibility = Visibility.Visible;
            DrawerShadow.Visibility = Visibility.Hidden;
            var slide = new DoubleAnimation(-(Drawer.Width + 16), 0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(slide, AnimFps);
            slide.Completed += (s, e) => { if (drawerOpen) DrawerShadow.Visibility = Visibility.Visible; };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        void CloseDrawer()
        {
            if (!drawerOpen) return;
            drawerOpen = false;
            DrawerShadow.Visibility = Visibility.Hidden;
            var slide = new DoubleAnimation(-(Drawer.Width + 16), TimeSpan.FromMilliseconds(130))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Timeline.SetDesiredFrameRate(slide, AnimFps);
            slide.Completed += (s, e) => { if (!drawerOpen) DrawerOverlay.Visibility = Visibility.Collapsed; };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        void DrawerScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseDrawer();

        void SearchToggle_Click(object sender, RoutedEventArgs e) => ToggleSearch(!searchOpen);

        void ToggleSearch(bool open)
        {
            searchOpen = open;
            SearchRow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()), DispatcherPriority.Input);
            else if (SearchBox.Text.Length > 0) SearchBox.Clear();
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        // ================= language & theme =================

        void Lang_Click(object sender, RoutedEventArgs e)
        {
            S.Lang = L.IsAr ? "en" : "ar";
            ApplyLanguage();
            Save();
        }

        void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            S.Theme = Theme.IsDark ? "light" : "dark";
            Theme.Apply(this, S);
            RefreshCards();
            FillAccentChips();
            SyncSettingsUi();
            Save();
        }

        void ApplyLanguage()
        {
            Loc.I.Set(S.Lang);
            Root.FlowDirection = L.IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Title = L["app"];
            SettingsBack.Content = L.IsAr ? "" : "";
            foreach (var n in nav) n.Name = L["nav_" + n.Key];
            FillCategoryLists();
            FillCategoryEditor();

            // Swapping a picker's items briefly clears its selection, and a two-way binding would write
            // that into the goal being edited; detach the editor while the lists are rebuilt.
            var editing = EditorForm.DataContext;
            syncing = true;
            EditorForm.DataContext = null;
            PrioChips.ItemsSource = new[] { Priority.Low, Priority.Medium, Priority.High }
                .Select(p => new Opt { Key = p, Name = L["p_" + p.ToString().ToLowerInvariant()], Brush = new Goal { Priority = p }.PriorityBrush })
                .ToList();
            RecChips.ItemsSource = Enum.GetValues<Recurrence>()
                .Select(r => new Opt { Key = r, Name = L["r_" + r.ToString().ToLowerInvariant()] }).ToList();
            RemChips.ItemsSource = new[] { 0, 15, 60, 1440 }.Select(m => new Opt { Key = m, Name = L["rm_s_" + m] }).ToList();
            TimeModeChips.ItemsSource = new[] { new Opt { Key = true, Name = L["any_time"] }, new Opt { Key = false, Name = L["at_time"] } };
            EditorForm.DataContext = editing;
            syncing = false;
            if (draft != null) UpdateDueUi(true);

            suppress = true;
            LangChips.ItemsSource = new[] { new Opt { Key = "ar", Name = "العربية" }, new Opt { Key = "en", Name = "English" } };
            ThemeChips.ItemsSource = new[] { "system", "dark", "light" }.Select(k => new Opt { Key = k, Name = L["th_" + k] }).ToList();
            FxChips.ItemsSource = new[] { "acrylic", "mica", "solid" }.Select(k => new Opt { Key = k, Name = L["fx_" + k] }).ToList();
            suppress = false;
            FillAccentChips();
            SyncSettingsUi();

            App.UpdateTrayMenu();
            RefreshCards();
            Refresh();
        }
    }
}
