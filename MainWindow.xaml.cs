using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Hadaf
{
    public class Opt
    {
        public object Key { get; set; }
        public string Name { get; set; }
        public Brush Brush { get; set; }
    }

    public class NavItem : Notify
    {
        string _name;
        int _count;
        bool _alert;
        public string Key { get; set; }
        public string Icon { get; set; }
        public Brush Brush { get; set; }
        public bool IsCat { get; set; }
        public bool ShowCount { get; set; } = true;
        public string Name { get => _name; set => Set(ref _name, value); }
        public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(HasCount)); } }
        public bool HasCount => ShowCount && Count > 0;
        public bool IsAlert { get => _alert; set => Set(ref _alert, value); }
    }

    /// <summary>A row of the category editor in Settings.</summary>
    public class CatRow : Notify
    {
        bool _picking;
        public CategoryDef Def { get; init; }
        public List<Opt> Colors { get; init; }
        public string Name => Categories.NameOf(Def);
        /// <summary>Shown when the box is emptied: a built-in category goes back to its own name.</summary>
        public string Placeholder => Categories.IsBuiltIn(Def.Id) ? Categories.DefaultName(Def.Id) : "";
        public Brush Brush => Categories.BrushOf(Def.Id);
        public bool CanDelete => Def.Id != Categories.Fallback;
        /// <summary>The colour choices are open.</summary>
        public bool Picking { get => _picking; set => Set(ref _picking, value); }
        public string ColorLabel => Loc.I.F("a11y_cat_color", Name);
        public string DeleteLabel => Loc.I.F("a11y_cat_delete", Name);
    }

    /// <summary>Section title inside the goal list ("Overdue 2", "Daily 1/3" …).</summary>
    public class GroupHeader
    {
        public string Key { get; set; }
        public string Title { get; set; }
        /// <summary>Shown next to the title: a count, or "done/planned today" for daily tasks.</summary>
        public string CountText { get; set; }
        public bool Collapsible { get; set; }
        public bool Expanded { get; set; } = true;
        public bool IsFirst { get; set; }
        public string Chevron { get; set; }
    }

    public record Tile(string Icon, string Value, string Label, Brush Brush);
    public record BarItem(string Label, int Count, double Height, Brush Brush);
    public record CatStat(string Name, Brush Brush, double Pct, int Count);

    public partial class MainWindow : Window
    {
        static readonly string[] GroupOrder = { "overdue", "daily", "today", "tomorrow", "week", "later", "none", "done" };

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
            PrepareData();
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

            if (RollDailies()) Save();
            ApplyLanguage();
            ApplyTextScale();
            ReportLoadIssue();
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
            // Window stays hidden; reminders keep running from the tray. A data problem still needs telling.
            if (Store.Issue != null) App.Notify(NoticeTitle.Text, NoticeText.Text);
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
            bool forward = next != HomePage;
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

        // ================= list =================

        void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || NavList.SelectedItem is not NavItem n) return;
            view = n.Key;
            cat = null;
            suppress = true;
            CatList.SelectedItem = null;
            suppress = false;
            Refresh();
            CloseDrawer();
        }

        void Cat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || CatList.SelectedItem is not NavItem n) return;
            cat = n.Key;
            view = "all";
            suppress = true;
            NavList.SelectedItem = null;
            suppress = false;
            Refresh();
            CloseDrawer();
        }

        IEnumerable<Goal> Filter()
        {
            var now = DateTime.Now;
            var today = DateTime.Today;
            IEnumerable<Goal> q = data.Goals;
            if (cat != null) q = q.Where(g => g.Category == cat);
            q = view switch
            {
                // Today: everything still open that is due today or already late, plus what was finished today.
                "today" => q.Where(g => g.DoneToday || (g.Done && g.CompletedAt?.Date == today)
                                     || (!g.IsChecked && g.Due.HasValue && g.Due.Value.Date <= today)),
                "daily" => q.Where(g => g.IsDaily),
                "upcoming" => q.Where(g => !g.Done && !g.IsDaily && g.Due > now && g.Due <= now.AddDays(7)),
                "overdue" => q.Where(g => g.IsOverdue),
                "done" => q.Where(g => g.Done),
                _ => q,
            };
            var term = SearchBox.Text.Trim();
            if (term.Length > 0)
                q = q.Where(g => g.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                              || (g.Notes ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase));
            return q;
        }

        static string GroupOf(Goal g)
        {
            if (g.Done) return "done";
            if (g.IsDaily) return "daily";
            if (!g.Due.HasValue) return "none";
            var due = g.Due.Value;
            var today = DateTime.Today;
            if (due < DateTime.Now) return "overdue";
            if (due.Date == today) return "today";
            if (due.Date == today.AddDays(1)) return "tomorrow";
            if (due.Date <= today.AddDays(7)) return "week";
            return "later";
        }

        static IEnumerable<Goal> Sorted(IEnumerable<Goal> goals) =>
            goals.OrderBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority).ThenBy(g => g.CreatedAt);

        /// <summary>Still-open daily tasks first, by time; the ones done today sink to the bottom.</summary>
        static IEnumerable<Goal> SortedDaily(IEnumerable<Goal> goals) =>
            goals.OrderBy(g => g.DoneToday).ThenBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority);

        static int PlannedToday(IEnumerable<Goal> dailies) =>
            dailies.Count(g => g.DoneToday || g.Due?.Date == DateTime.Today);

        /// <summary>
        /// "All" and category views are split into buckets (daily, then by time); the dedicated
        /// views (today, daily, overdue …) already are one bucket, so they stay a flat list.
        /// </summary>
        List<object> BuildEntries(List<Goal> goals)
        {
            var list = new List<object>();
            if (view != "all")
            {
                list.AddRange(view switch
                {
                    "done" => goals.OrderByDescending(g => g.CompletedAt),
                    "daily" => SortedDaily(goals),
                    "today" => goals.OrderBy(g => g.IsChecked).ThenBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority),
                    _ => Sorted(goals),
                });
                return list;
            }
            bool searching = SearchBox.Text.Trim().Length > 0;
            foreach (var key in GroupOrder)
            {
                var items = goals.Where(g => GroupOf(g) == key).ToList();
                if (items.Count == 0) continue;
                bool isDone = key == "done", isDaily = key == "daily";
                int planned = isDaily ? PlannedToday(items) : 0;
                var header = new GroupHeader
                {
                    Key = key,
                    Title = L["g_" + key],
                    CountText = planned > 0 ? $"{items.Count(g => g.DoneToday)}/{planned}" : items.Count.ToString(),
                    IsFirst = list.Count == 0,
                    Collapsible = isDone,
                    Expanded = !isDone || doneExpanded || searching,
                };
                header.Chevron = header.Expanded ? "" : L.IsAr ? "" : "";
                list.Add(header);
                if (header.Expanded)
                    list.AddRange(isDone ? items.OrderByDescending(g => g.CompletedAt) : isDaily ? SortedDaily(items) : Sorted(items));
            }
            return list;
        }

        void Refresh()
        {
            OfferStartWithWindows();
            UpdateStreaks();
            UpdateCounts();
            bool stats = view == "stats", daily = view == "daily";
            ViewTitle.Text = cat != null ? Categories.NameOf(cat) : L["nav_" + view];
            StatsView.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
            GoalList.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
            SearchToggle.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
            SearchRow.Visibility = !stats && searchOpen ? Visibility.Visible : Visibility.Collapsed;
            // Nothing typed in Overdue could ever show up there (a new goal isn't late yet), so no composer.
            Composer.Visibility = stats || view == "done" || view == "overdue" ? Visibility.Collapsed : Visibility.Visible;
            QuickAdd.Tag = L[daily ? "composer_daily_ph" : "composer_ph"];
            EmptyIcon.Text = daily ? "" : "";
            EmptyTitle.Text = L[daily ? "empty_daily_title" : "empty_title"];
            EmptySub.Text = L[daily ? "empty_daily_sub" : "empty_sub"];
            PlaceSnack();
            if (stats)
            {
                ViewSub.Text = L.LongDate(DateTime.Now);
                EmptyState.Visibility = Visibility.Collapsed;
                BuildStats();
                return;
            }
            var goals = Filter().ToList();
            string tail = daily
                ? L.F("daily_progress", goals.Count(g => g.DoneToday), PlannedToday(goals))
                : L.Goals(view == "done" ? goals.Count : goals.Count(g => !g.IsChecked));
            ViewSub.Text = $"{L.LongDate(DateTime.Now)}  ·  {tail}";
            entries = BuildEntries(goals);
            GoalList.ItemsSource = entries;
            EmptyState.Visibility = goals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshCards()
        {
            foreach (var g in data.Goals) g.RefreshAll();
            if (view == "stats") BuildStats();
        }

        void UpdateCounts()
        {
            var now = DateTime.Now;
            var today = DateTime.Today;
            var active = data.Goals.Where(g => !g.Done).ToList();
            int overdue = active.Count(g => g.IsOverdue);
            foreach (var n in nav)
            {
                n.Count = n.Key switch
                {
                    "all" => active.Count,
                    "today" => active.Count(g => !g.IsChecked && g.Due.HasValue && g.Due.Value.Date <= today),
                    "daily" => active.Count(g => g.IsDaily && !g.DoneToday && g.Due?.Date == today),
                    "upcoming" => active.Count(g => !g.IsDaily && g.Due > now && g.Due <= now.AddDays(7)),
                    "overdue" => overdue,
                    "done" => data.Goals.Count(g => g.Done),
                    _ => 0,
                };
                n.IsAlert = n.Key == "overdue" && n.Count > 0;
            }
            foreach (var c in cats) c.Count = active.Count(g => g.Category == c.Key);
            DrawerSummary.Text = overdue > 0
                ? $"{L.Goals(active.Count)}  ·  {L.F("overdue_n", overdue)}"
                : L.Goals(active.Count);
        }

        void Tick()
        {
            if (saveError != null) Save();
            bool rolled = RollDailies();
            if (rolled) Save();
            CheckReminders();
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            UpdateCounts();
            if (page != HomePage || view == "stats") return;
            if (rolled) { Refresh(); return; }
            var fresh = BuildEntries(Filter().ToList());
            if (SameShape(fresh, entries))
            {
                foreach (var g in entries.OfType<Goal>()) g.RefreshAll();
            }
            else Refresh();
        }

        static bool SameShape(List<object> a, List<object> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First is GroupHeader x
                ? p.Second is GroupHeader y && x.Key == y.Key && x.CountText == y.CountText
                : ReferenceEquals(p.First, p.Second));

        void GroupHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GroupHeader h || !h.Collapsible) return;
            doneExpanded = !doneExpanded;
            Refresh();
        }

        // ---- daily tasks ----

        /// <summary>
        /// Daily tasks never pile up as overdue: once their day is over they move on to the next run
        /// with their steps cleared, like a checklist that starts fresh every morning.
        /// </summary>
        bool RollDailies()
        {
            bool changed = false;
            var today = DateTime.Today;
            foreach (var g in data.Goals.Where(g => g.IsDaily && !g.Done && !g.DoneToday))
            {
                if (g.Due.HasValue && g.Due.Value.Date >= today) continue;
                g.Due = g.NextRun(today, false);
                foreach (var s in g.SubTasks) s.Done = false;
                g.NotifiedBefore = g.NotifiedDue = g.Due <= DateTime.Now;
                g.RefreshAll();
                changed = true;
            }
            return changed;
        }

        /// <summary>Consecutive scheduled days each daily task was done, ending today (or yesterday while today is still open).</summary>
        void UpdateStreaks()
        {
            var doneDays = data.History.Where(h => h.GoalId != null).GroupBy(h => h.GoalId)
                .ToDictionary(x => x.Key, x => x.Select(h => h.At.Date).ToHashSet());
            foreach (var g in data.Goals.Where(g => g.IsDaily))
            {
                int streak = doneDays.TryGetValue(g.Id, out var days) ? StreakOf(g, days) : 0;
                if (streak == g.Streak) continue;
                g.Streak = streak;
                g.RefreshAll();
            }
        }

        static int StreakOf(Goal g, HashSet<DateTime> days)
        {
            var day = days.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
            int n = 0;
            for (int i = 0; i < 20000 && day >= g.CreatedAt.Date; i++, day = day.AddDays(-1))
            {
                if (!g.RunsOn(day.DayOfWeek)) continue;
                if (!days.Contains(day)) break;
                n++;
            }
            return n;
        }

        // ---- composer ----

        void QuickAdd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var title = QuickAdd.Text.Trim();
            if (title.Length == 0) return;
            var g = new Goal { Title = title, Category = cat ?? Categories.Default };
            if (view == "daily")
            {
                g.Recurrence = Recurrence.Daily;
                g.AnyTime = true;
                g.Due = g.NextRun(DateTime.Today, true);
            }
            else if (view == "today") g.Due = DateTime.Today.AddHours(23).AddMinutes(59);
            else if (view == "upcoming") g.Due = DateTime.Today.AddDays(1).AddHours(18);
            data.Goals.Add(g);
            QuickAdd.Clear();
            Save();
            Refresh();
            ShowSnack(L[g.IsDaily ? "added_daily" : "added"], L[g.Due.HasValue ? "edit" : "set_deadline"], () => OpenEditor(g));
        }

        void ComposerOpen_Click(object sender, RoutedEventArgs e)
        {
            var title = QuickAdd.Text.Trim();
            QuickAdd.Clear();
            OpenEditor(null, title);
        }

        // ---- cards ----

        static Goal GoalOf(object sender) => (sender as FrameworkElement)?.DataContext as Goal;

        void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (GoalOf(sender) is Goal g) OpenEditor(g);
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) DeleteGoal(g);
        }

        void Expand_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) g.Expanded = !g.Expanded;
        }

        void Sub_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SubTask st) return;
            data.Goals.FirstOrDefault(x => x.SubTasks.Contains(st))?.RefreshAll();
            Save();
        }

        void Done_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) ToggleDone(g);
        }

        // ---- keyboard and right-click on a card ----

        /// <summary>Enter opens, Space ticks, Delete removes; Shift+F10 or the menu key opens the card's menu.</summary>
        void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource != sender || GoalOf(sender) is not Goal g) return;
            switch (e.Key)
            {
                case Key.Enter: OpenEditor(g); break;
                case Key.Space: ToggleDone(g); break;
                case Key.Delete: DeleteGoal(g); break;
                default: return;
            }
            e.Handled = true;
        }

        void CtxEdit_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) OpenEditor(g);
        }

        void CtxDone_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) ToggleDone(g);
        }

        void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is Goal g) DeleteGoal(g);
        }

        /// <summary>Moves the deadline to tomorrow at the same time, with a way to take it back.</summary>
        void CtxSnooze_Click(object sender, RoutedEventArgs e)
        {
            if (GoalOf(sender) is not Goal g || !g.CanSnooze) return;
            var before = (g.Due, g.NotifiedBefore, g.NotifiedDue);
            g.Due = DateTime.Today.AddDays(1) + g.Due.Value.TimeOfDay;
            g.NotifiedBefore = g.NotifiedDue = false;
            g.RefreshAll();
            Save();
            Refresh();
            ShowSnack(L.F("snoozed", L.FormatDate(g.Due.Value)), L["undo"], () =>
            {
                (g.Due, g.NotifiedBefore, g.NotifiedDue) = before;
                g.RefreshAll();
                Save();
                Refresh();
            });
        }

        void ToggleDone(Goal g)
        {
            var now = DateTime.Now;
            if (g.IsDaily) ToggleDaily(g, now);
            else if (g.Done)
            {
                g.Done = false;
                g.CompletedAt = null;
                var rec = data.History.LastOrDefault(h => h.GoalId == g.Id);
                if (rec != null) data.History.Remove(rec);
            }
            else
            {
                var record = Record(g, now);
                data.History.Add(record);
                if (g.IsRecurring)
                {
                    // Remember the occurrence being closed, so an accidental tick can be undone.
                    var before = (Due: g.Due, Steps: g.SubTasks.Select(s => s.Done).ToArray(), g.NotifiedBefore, g.NotifiedDue);
                    var next = g.Due ?? now;
                    do next = g.NextAfter(next); while (next <= now);
                    g.Due = next;
                    foreach (var s in g.SubTasks) s.Done = false;
                    g.NotifiedBefore = g.NotifiedDue = false;
                    ShowSnack(L.F("next_occ", L.FormatDate(next)), L["undo"], () =>
                    {
                        if (g.Due != next) return;   // changed since; nothing left to undo
                        g.Due = before.Due;
                        for (int i = 0; i < Math.Min(before.Steps.Length, g.SubTasks.Count); i++) g.SubTasks[i].Done = before.Steps[i];
                        g.NotifiedBefore = before.NotifiedBefore;
                        g.NotifiedDue = before.NotifiedDue;
                        data.History.Remove(record);
                        g.RefreshAll();
                        Save();
                        Refresh();
                    });
                }
                else
                {
                    g.Done = true;
                    g.CompletedAt = now;
                    ShowSnack(L["great"], L["undo"], () => { if (g.Done) ToggleDone(g); });
                }
            }
            g.RefreshAll();
            Save();
            Refresh();
        }

        /// <summary>Ticking a daily task marks today as done; it comes back unticked at its next run.</summary>
        void ToggleDaily(Goal g, DateTime now)
        {
            var today = DateTime.Today;
            if (g.DoneToday)
            {
                // Untick: today's run is open again.
                g.LastDoneAt = null;
                var rec = data.History.LastOrDefault(h => h.GoalId == g.Id && h.At.Date == today);
                if (rec != null) data.History.Remove(rec);
                g.Due = g.NextRun(today, false);
                g.NotifiedBefore = g.NotifiedDue = g.Due <= now;
                return;
            }
            data.History.Add(Record(g, now));
            g.LastDoneAt = now;
            g.Due = g.NextRun(today.AddDays(1), false);
            foreach (var s in g.SubTasks) s.Done = false;
            g.NotifiedBefore = g.NotifiedDue = false;
            UpdateStreaks();
            ShowSnack(g.Streak >= 2 ? L.F("daily_done_streak", L.DaysPhrase(g.Streak)) : L["daily_done"], L["undo"], () =>
            {
                if (!g.DoneToday) return;
                ToggleDaily(g, DateTime.Now);
                g.RefreshAll();
                Save();
                Refresh();
            });
        }

        static CompletionRecord Record(Goal g, DateTime now) => new()
        {
            GoalId = g.Id, Title = g.Title, Category = g.Category, At = now,
            OnTime = !g.Due.HasValue || now <= g.Due.Value,
            HadDeadline = HasRealDeadline(g),
        };

        static bool HasRealDeadline(Goal g) => g.Due.HasValue && !(g.IsDaily && g.AnyTime);

        void DeleteGoal(Goal g)
        {
            undoIndex = data.Goals.IndexOf(g);
            undoGoal = g;
            data.Goals.Remove(g);
            Save();
            Refresh();
            ShowSnack(L["deleted"], L["undo"], UndoDelete);
        }

        void UndoDelete()
        {
            if (undoGoal == null) return;
            data.Goals.Insert(Math.Clamp(undoIndex, 0, data.Goals.Count), undoGoal);
            undoGoal = null;
            Save();
            Refresh();
        }

        // ---- snackbar ----

        void ShowSnack(string text, string actionText = null, Action action = null)
        {
            Emoji.Show(SnackText, text);
            snackAction = action;
            SnackAction.Content = actionText;
            SnackAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            PlaceSnack();
            Snack.Visibility = Visibility.Visible;
            snackTimer.Stop();
            snackTimer.Start();
        }

        void HideSnack()
        {
            snackTimer.Stop();
            Snack.Visibility = Visibility.Collapsed;
        }

        void SnackAction_Click(object sender, RoutedEventArgs e)
        {
            var action = snackAction;
            snackAction = null;
            HideSnack();
            action?.Invoke();
        }

        /// <summary>Keeps the snackbar just above the composer when the composer is on screen.</summary>
        void PlaceSnack() =>
            Snack.Margin = new Thickness(20, 0, 20, page == HomePage && Composer.Visibility == Visibility.Visible ? 72 : 20);

        // ================= reminders =================

        void CheckReminders()
        {
            var now = DateTime.Now;
            var lines = new List<(string Head, string Body, string GoalId)>();
            foreach (var g in data.Goals.Where(g => !g.Done && g.Due.HasValue))
            {
                // Any-time daily tasks have no clock to warn about; ones done today are settled.
                if (g.IsDaily && (g.AnyTime || g.DoneToday)) continue;
                if (!g.NotifiedDue && now >= g.Due.Value)
                {
                    g.NotifiedDue = g.NotifiedBefore = true;
                    lines.Add((L["n_due"], g.Title, g.Id));
                }
                else if (!g.NotifiedBefore && g.RemindMinutes > 0 && now >= g.Due.Value.AddMinutes(-g.RemindMinutes))
                {
                    g.NotifiedBefore = true;
                    lines.Add((L["n_soon"], $"{g.Title} · {L.F("left_fmt", L.Dur(g.Due.Value - now))}", g.Id));
                }
            }
            if (lines.Count == 0) return;
            Save();
            if (!S.Notifications) return;
            // Clicking a single reminder opens that goal; a batch opens the list.
            if (lines.Count == 1) App.Notify(lines[0].Head, lines[0].Body, lines[0].GoalId);
            else App.Notify(L.F("n_many", lines.Count), string.Join("\n", lines.Take(5).Select(l => "• " + l.Body)));
        }

        /// <summary>Opens a goal from outside the list (a clicked reminder).</summary>
        public void OpenGoal(string id)
        {
            var g = data.Goals.FirstOrDefault(x => x.Id == id);
            if (g == null) return;
            if (page == SettingsPage) ShowPage(HomePage);
            if (page == HomePage) OpenEditor(g);
        }

        // ================= editor page =================

        void OpenEditor(Goal g, string title = null)
        {
            original = g;
            if (g != null) draft = Store.Clone(g);
            else
            {
                draft = new Goal { Title = title ?? "", Category = cat ?? Categories.Default };
                if (view == "daily")
                {
                    draft.Recurrence = Recurrence.Daily;
                    draft.AnyTime = true;
                    draft.Due = draft.NextRun(DateTime.Today, true);
                }
                else draft.Due = view == "today" ? DateTime.Today.AddHours(23).AddMinutes(59) : DateTime.Today.AddDays(1).AddHours(18);
            }

            syncing = true;
            shownRepeat = draft.Recurrence;
            shownAnyTime = draft.AnyTime;
            EditorTitle.Text = L[g == null ? "new_goal" : "edit_goal"];
            DeleteBtn.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
            EditorForm.DataContext = draft;
            NewStepBox.Clear();
            var m = draft.Due ?? DateTime.Today;
            calMonth = new DateTime(m.Year, m.Month, 1);
            UpdateDueUi(true);
            syncing = false;

            EditorScroll.ScrollToTop();
            CloseDrawer();
            HideSnack();
            ShowPage(EditorPage);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TitleBox.Focus();
                TitleBox.CaretIndex = TitleBox.Text.Length;
            }), DispatcherPriority.Input);
        }

        void CloseEditor()
        {
            draft = original = null;
            ShowPage(HomePage);
        }

        void CloseEditor_Click(object sender, RoutedEventArgs e) => CloseEditor();

        void Save_Click(object sender, RoutedEventArgs e) => SaveDraft();

        void SaveDraft()
        {
            if (draft == null) return;
            var d = draft;
            d.Title = (d.Title ?? "").Trim();
            if (d.Title.Length == 0)
            {
                ShowSnack(L["title_required"]);
                TitleBox.Focus();
                return;
            }
            AddPendingStep();
            foreach (var s in d.SubTasks.Where(s => string.IsNullOrWhiteSpace(s.Title)).ToList())
                d.SubTasks.Remove(s);

            // Tidy the goal before storing it. The editor's pickers are still bound to it, so keep their
            // change handlers out of the way: otherwise clearing AnyTime would re-plan the deadline.
            syncing = true;
            if (!Categories.Exists(d.Category))
                d.Category = Categories.Exists(original?.Category) ? original.Category : Categories.Default;
            if (d.IsDaily)
            {
                if (!d.Due.HasValue) d.Due = d.NextRun(DateTime.Today, true);
                d.Done = false; // a daily task is never finished for good
                d.CompletedAt = null;
            }
            else d.AnyTime = false;
            // A monthly goal belongs to the day the user picked; only a newly picked date moves it.
            if (d.Recurrence != Recurrence.Monthly || !d.Due.HasValue) d.RepeatDay = 0;
            else if (original == null || original.Recurrence != Recurrence.Monthly || original.Due?.Date != d.Due.Value.Date)
                d.RepeatDay = d.Due.Value.Day;
            syncing = false;

            bool rescheduled = original == null || original.Due != d.Due || original.RemindMinutes != d.RemindMinutes;
            if (rescheduled)
            {
                // Don't fire for a deadline that was already in the past when it was set.
                bool past = d.Due.HasValue && d.Due.Value <= DateTime.Now;
                d.NotifiedDue = past;
                d.NotifiedBefore = past;
            }
            else if (original != null)
            {
                // A reminder may have fired while the editor was open; keep that, or it would fire again.
                d.NotifiedBefore = original.NotifiedBefore;
                d.NotifiedDue = original.NotifiedDue;
            }
            int index = original == null ? -1 : data.Goals.IndexOf(original);
            if (index < 0) data.Goals.Add(d);
            else
            {
                d.Expanded = original.Expanded;
                data.Goals[index] = d;
            }
            CloseEditor();
            Save();
            Refresh();
        }

        void EditorDelete_Click(object sender, RoutedEventArgs e)
        {
            var g = original;
            CloseEditor();
            if (g != null) DeleteGoal(g);
        }

        void NewStep_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            AddPendingStep();
            e.Handled = true;
        }

        void AddPendingStep()
        {
            var t = NewStepBox.Text.Trim();
            if (t.Length == 0 || draft == null) return;
            draft.SubTasks.Add(new SubTask { Title = t });
            NewStepBox.Clear();
        }

        void RemoveStep_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SubTask s) draft?.SubTasks.Remove(s);
        }

        // ---- schedule & repeat ----

        string Summary(Goal g) =>
            g.IsDaily ? L.DailySummary(g) : g.Due.HasValue ? L.FormatDate(g.Due.Value) : L["no_deadline"];

        /// <summary>A daily task already ticked today starts its schedule from tomorrow.</summary>
        static DateTime DailyFrom(Goal g) => g.DoneToday ? DateTime.Today.AddDays(1) : DateTime.Today;

        /// <summary>Shows only what the chosen repeat needs: a date for one-off/weekly/monthly, weekdays for daily.</summary>
        void UpdateDueUi(bool setTimeText)
        {
            bool daily = draft.IsDaily, has = draft.Due.HasValue;
            bool timed = has && !(daily && draft.AnyTime);
            DueIcon.Text = daily ? "" : "";
            DueSummary.Text = Summary(draft);
            DatePanel.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;
            CalendarPanel.Visibility = !daily && has ? Visibility.Visible : Visibility.Collapsed;
            DailyPanel.Visibility = daily ? Visibility.Visible : Visibility.Collapsed;
            TimePanel.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            if (!daily && has) BuildCalendar();
            if (daily) BuildDays();
            if (setTimeText)
            {
                bool was = syncing;
                syncing = true;
                TimeBox.Text = timed ? draft.Due.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
                syncing = was;
            }
        }

        void Repeat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (syncing || draft == null || RecChips.SelectedValue is not Recurrence r || r == shownRepeat) return;
            shownRepeat = r;
            draft.Recurrence = r;
            if (r == Recurrence.Daily) draft.Due = draft.NextRun(DailyFrom(draft), true);
            else if (r != Recurrence.None && !draft.Due.HasValue) draft.Due = DateTime.Today.AddDays(1).AddHours(18);
            if (draft.Due.HasValue) calMonth = new DateTime(draft.Due.Value.Year, draft.Due.Value.Month, 1);
            UpdateDueUi(true);
        }

        void TimeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (syncing || draft == null || !draft.IsDaily || TimeModeChips.SelectedValue is not bool any || any == shownAnyTime) return;
            shownAnyTime = any;
            draft.AnyTime = any;
            if (!any) draft.Due = (draft.Due ?? DateTime.Today).Date + new TimeSpan(9, 0, 0);
            draft.Due = draft.NextRun(DailyFrom(draft), true);
            UpdateDueUi(true);
        }

        void BuildDays()
        {
            DaysRow.Children.Clear();
            int first = L.IsAr ? (int)DayOfWeek.Saturday : (int)DayOfWeek.Sunday;
            var style = (Style)FindResource("DayToggle");
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)((first + i) % 7);
                var toggle = new ActionCheckBox
                {
                    Style = style,
                    Tag = day,
                    IsChecked = draft.RunsOn(day),
                    Content = L.Culture.DateTimeFormat.GetShortestDayName(day),
                };
                toggle.Click += Day_Click;
                DaysRow.Children.Add(toggle);
            }
        }

        void Day_Click(object sender, RoutedEventArgs e)
        {
            if (draft == null || sender is not CheckBox toggle) return;
            int bit = 1 << (int)(DayOfWeek)toggle.Tag;
            int days = toggle.IsChecked == true ? draft.Days | bit : draft.Days & ~bit;
            if (days == 0)
            {
                toggle.IsChecked = true; // a daily task runs on at least one day
                return;
            }
            draft.Days = days;
            draft.Due = draft.NextRun(DailyFrom(draft), true);
            DueSummary.Text = Summary(draft);
        }

        void BuildCalendar()
        {
            var culture = L.Culture;
            CalTitle.Text = calMonth.ToString("MMMM yyyy", culture);
            // Icon glyphs are never mirrored by RTL layout: "previous" must point toward the reading start.
            CalPrev.Content = L.IsAr ? "" : "";
            CalNext.Content = L.IsAr ? "" : "";

            int first = L.IsAr ? (int)DayOfWeek.Saturday : (int)DayOfWeek.Sunday;
            var faint = (Brush)FindResource("Faint");
            CalHead.Children.Clear();
            for (int i = 0; i < 7; i++)
            {
                var dow = (DayOfWeek)((first + i) % 7);
                CalHead.Children.Add(new TextBlock
                {
                    Text = culture.DateTimeFormat.GetShortestDayName(dow),
                    Width = 36,
                    Height = 28,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    Foreground = faint,
                });
            }

            CalDays.Children.Clear();
            int offset = ((int)calMonth.DayOfWeek - first + 7) % 7;
            var start = calMonth.AddDays(-offset);
            var style = (Style)FindResource("Btn.Day");
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                string tag = draft.Due?.Date == d ? "sel"
                    : d == DateTime.Today ? "today"
                    : d.Month != calMonth.Month || d < DateTime.Today ? "other" : null;
                var b = new Button { Content = d.Day.ToString(CultureInfo.InvariantCulture), Style = style, Tag = tag };
                b.Click += (s, e) => PickDate(d);
                CalDays.Children.Add(b);
            }
        }

        void PickDate(DateTime d)
        {
            var time = draft.Due?.TimeOfDay ?? TimeInput.Parse(TimeBox.Text) ?? new TimeSpan(18, 0, 0);
            draft.Due = d.Date + time;
            calMonth = new DateTime(d.Year, d.Month, 1);
            UpdateDueUi(true);
        }

        void CalPrev_Click(object sender, RoutedEventArgs e) { calMonth = calMonth.AddMonths(-1); BuildCalendar(); }
        void CalNext_Click(object sender, RoutedEventArgs e) { calMonth = calMonth.AddMonths(1); BuildCalendar(); }

        void QuickDate_Click(object sender, RoutedEventArgs e)
        {
            var tag = (string)((Button)sender).Tag;
            if (tag == "none")
            {
                shownRepeat = Recurrence.None;
                draft.Due = null;
                draft.Recurrence = Recurrence.None;
                UpdateDueUi(true);
                return;
            }
            PickDate(DateTime.Today.AddDays(int.Parse(tag, CultureInfo.InvariantCulture)));
        }

        void QuickTime_Click(object sender, RoutedEventArgs e)
        {
            if (draft?.Due == null) return;
            TimeBox.Text = (string)((Button)sender).Tag; // TextChanged applies it
        }

        void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (syncing || draft?.Due == null) return;
            var t = TimeInput.Parse(TimeBox.Text);
            // Say so when the text isn't a time, instead of silently ignoring it.
            TimeError.Visibility = t == null && TimeBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (t is TimeSpan time)
            {
                draft.Due = draft.Due.Value.Date + time;
                if (draft.IsDaily) draft.Due = draft.NextRun(DailyFrom(draft), true);
                DueSummary.Text = Summary(draft);
            }
        }

        /// <summary>Leaving the box with something that isn't a time puts back the time that is in effect.</summary>
        void TimeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (draft?.Due == null || TimeInput.Parse(TimeBox.Text) != null) return;
            syncing = true;
            TimeBox.Text = draft.Due.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            syncing = false;
            TimeError.Visibility = Visibility.Collapsed;
        }

        // ================= statistics =================

        void BuildStats()
        {
            var h = data.History;
            var today = DateTime.Today;
            var accent = (SolidColorBrush)FindResource("Accent");

            int total = h.Count;
            int week = h.Count(r => r.At >= today.AddDays(-6));
            // Only goals that could be late count towards "on time".
            var timed = h.Where(r => r.HadDeadline != false).ToList();
            string onTime = timed.Count == 0 ? "–" : Math.Round(100.0 * timed.Count(r => r.OnTime) / timed.Count) + "%";
            var days = h.Select(r => r.At.Date).ToHashSet();
            int streak = 0;
            var d = days.Contains(today) ? today : today.AddDays(-1);
            while (days.Contains(d)) { streak++; d = d.AddDays(-1); }

            Tiles.ItemsSource = new[]
            {
                new Tile("", total.ToString(), L["s_total"], Palette.B(0xFF10B981)),
                new Tile("", week.ToString(), L["s_week"], accent),
                new Tile("", onTime, L["s_ontime"], Palette.B(0xFFF59E0B)),
                new Tile("", streak.ToString(), L["s_streak"], Palette.B(0xFFEC4899)),
            };

            var counts = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
                .Select(day => (day, n: h.Count(r => r.At.Date == day))).ToList();
            int max = Math.Max(1, counts.Max(x => x.n));
            var ac = accent.Color;
            var faded = Palette.B(0x59000000 | (uint)(ac.R << 16 | ac.G << 8 | ac.B));
            Bars.ItemsSource = counts.Select(x => new BarItem(
                x.day.ToString("ddd", L.Culture), x.n,
                x.n == 0 ? 4 : 12 + 100.0 * x.n / max,
                x.day == today ? accent : faded)).ToList();

            // Completions from a category that has since been deleted count as the fallback's, like its goals.
            CatStats.ItemsSource = Categories.All.Select(c =>
            {
                int n = h.Count(r => r.Category == c.Id || c.Id == Categories.Fallback && !Categories.Exists(r.Category));
                return new CatStat(Categories.NameOf(c), Categories.BrushOf(c.Id), total == 0 ? 0 : 100.0 * n / total, n);
            }).ToList();
        }

        // ================= settings page =================

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
            HideSnack();
            SyncSettingsUi();
            FillCategoryEditor();
            ShowPage(SettingsPage);
        }

        void SettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

        void CloseSettings()
        {
            // A category name still being typed (Esc or Back pressed in the box) counts too.
            if (Keyboard.FocusedElement is TextBox { DataContext: CatRow } box) CommitCategoryName(box);
            Save();
            ShowPage(HomePage);
        }

        void SyncSettingsUi()
        {
            suppress = true;
            LangChips.SelectedValue = S.Lang;
            ThemeChips.SelectedValue = S.Theme;
            FxChips.SelectedValue = S.Effect;
            AccentChips.SelectedValue = Palette.TryParse(S.Accent, out _) ? S.Accent.ToUpperInvariant() : "system";
            AccentName.Text = (AccentChips.SelectedItem as Opt)?.Name;
            OpacitySlider.Value = S.Transparency;
            OpacitySlider.IsEnabled = Theme.GlassSupported && S.Effect != "solid";
            GlassNote.Visibility = Theme.GlassSupported ? Visibility.Collapsed : Visibility.Visible;
            NotifySwitch.IsChecked = S.Notifications;
            TraySwitch.IsChecked = S.CloseToTray;
            StartupSwitch.IsChecked = AutoStart.IsEnabled();
            UpdateBackupInfo();
            suppress = false;
        }

        void SettingChip_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || ((ListBox)sender).SelectedValue is not string v) return;
            if (sender == LangChips)
            {
                if (v == S.Lang) return;
                S.Lang = v;
                ApplyLanguage();
            }
            else
            {
                if (sender == ThemeChips) S.Theme = v;
                else S.Effect = v;
                Theme.Apply(this, S);
                RefreshCards();
                FillAccentChips();
                SyncSettingsUi();
            }
            Save();
        }

        /// <summary>The accent choices, each shown as the app will use it in the current theme.</summary>
        void FillAccentChips()
        {
            Opt Swatch(string key, string name, Color c) => new() { Key = key, Name = name, Brush = new SolidColorBrush(Theme.Legible(c)) };
            suppress = true;
            AccentChips.ItemsSource = new[] { Swatch("system", L["accent_system"], Theme.SystemAccent) }
                .Concat(Palette.Swatches.Select(s => Swatch(Palette.Hex(s.Argb), L["col_" + s.Key],
                    Color.FromRgb((byte)(s.Argb >> 16), (byte)(s.Argb >> 8), (byte)s.Argb))))
                .ToList();
            suppress = false;
        }

        void Accent_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || AccentChips.SelectedValue is not string v) return;
            S.Accent = v;
            Theme.Apply(this, S);
            RefreshCards();
            SyncSettingsUi();
            Save();
        }

        void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppress || !IsInitialized) return;
            S.Transparency = e.NewValue;
            Theme.Apply(this, S);
        }

        void Switch_Click(object sender, RoutedEventArgs e)
        {
            bool on = ((CheckBox)sender).IsChecked == true;
            if (sender == NotifySwitch) S.Notifications = on;
            else if (sender == TraySwitch) S.CloseToTray = on;
            else if (sender == StartupSwitch)
            {
                S.StartupAsked = true;
                if (noticeKey == "startup") HideNotice();
                try { AutoStart.Set(on); } catch { StartupSwitch.IsChecked = AutoStart.IsEnabled(); }
            }
            Save();
        }

        // ================= saving & data safety =================

        /// <summary>Saves; a failure is shown (never swallowed) and retried on every tick until it goes through.</summary>
        void Save()
        {
            var error = Store.Save(data);
            if (error == null)
            {
                if (saveError != null)
                {
                    saveError = null;
                    HideNotice("save");
                }
                return;
            }
            bool firstFailure = saveError == null;
            saveError = error;
            ShowNotice("save", "danger", L["save_failed_title"], L.F("save_failed_text", Describe(error)), L["retry"], Save);
            if (firstFailure && !IsVisible) App.Notify(L["save_failed_title"], error.Message);
        }

        /// <summary>The common reasons a save fails, in the user's language; anything else keeps the system text.</summary>
        string Describe(Exception error) => error switch
        {
            UnauthorizedAccessException => L["err_access"],
            IOException io when (io.HResult & 0xFFFF) is 112 or 39 => L["err_disk_full"], // ERROR_DISK_FULL / HANDLE_DISK_FULL
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => L["err_locked"],      // sharing / lock violation
            _ => error.Message,
        };

        /// <summary>Tells the user when start-up had to recover their data, and stores the recovered copy.</summary>
        void ReportLoadIssue()
        {
            var issue = Store.Issue;
            if (issue == null) return;
            if (issue.Kind == LoadProblem.RestoredFromBackup)
                ShowNotice("load", "warn", L["load_problem_title"],
                    L.F("load_restored_text", L.FormatDate(issue.BackupTime ?? DateTime.Now)), L["open_data_folder"], OpenDataFolder);
            else
                ShowNotice("load", "danger", L["load_problem_title"], L["load_failed_text"], L["open_data_folder"], OpenDataFolder);
            // The damaged file is already set aside; make the recovered data the main file again.
            Save();
        }

        /// <summary>Shows the notice bar. <paramref name="tone"/> is "info", "warn" or "danger".</summary>
        void ShowNotice(string key, string tone, string title, string text, string actionText = null, Action action = null)
        {
            noticeKey = key;
            noticeAction = action;
            NoticeTitle.Text = title;
            NoticeText.Text = text;
            NoticeAction.Content = actionText;
            NoticeAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            var (fill, stroke, ink, icon) = tone switch
            {
                "danger" => ("DangerSoft", "DangerStroke", "Danger", ""),
                "warn" => ("WarnSoft", "WarnStroke", "Warn", ""),
                _ => ("AccentSoft", "InfoStroke", "Accent", ""),
            };
            Notice.SetResourceReference(Border.BackgroundProperty, fill);
            Notice.SetResourceReference(Border.BorderBrushProperty, stroke);
            NoticeIcon.SetResourceReference(TextBlock.ForegroundProperty, ink);
            NoticeIcon.Text = icon;
            Notice.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Reminders only fire while the app runs, so once there is something to be reminded of, offer
        /// (one time) to start it with Windows, hidden in the tray.
        /// </summary>
        void OfferStartWithWindows()
        {
            if (S.StartupAsked || noticeKey != null || !data.Goals.Any(g => !g.Done && g.Due.HasValue)) return;
            if (AutoStart.IsEnabled())
            {
                S.StartupAsked = true;
                return;
            }
            ShowNotice("startup", "info", L["startup_title"], L["startup_text"], L["startup_enable"], () =>
            {
                S.StartupAsked = true;
                try
                {
                    AutoStart.Set(true);
                    ShowSnack(L["startup_done"]);
                }
                catch
                {
                    ShowSnack(L["startup_failed"]);
                }
                HideNotice("startup");
                Save();
            });
        }

        void HideNotice(string key = null)
        {
            if (key != null && key != noticeKey) return;
            noticeKey = null;
            noticeAction = null;
            Notice.Visibility = Visibility.Collapsed;
        }

        void NoticeAction_Click(object sender, RoutedEventArgs e) => noticeAction?.Invoke();
        void NoticeClose_Click(object sender, RoutedEventArgs e)
        {
            if (noticeKey == "startup")
            {
                // "Not now" is an answer too: don't keep asking.
                S.StartupAsked = true;
                Save();
            }
            HideNotice();
        }
        void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenDataFolder();

        static void OpenDataFolder()
        {
            try { Process.Start(new ProcessStartInfo { FileName = Store.Dir, UseShellExecute = true }); }
            catch { }
        }

        void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            Save();
            if (Store.BackupNow()) ShowSnack(L["backup_done"]);
            else ShowSnack(L["backup_failed"]);
            UpdateBackupInfo();
        }

        void UpdateBackupInfo() =>
            BackupInfo.Text = Store.LastBackup is DateTime t ? L.F("last_backup", L.FormatDate(t)) : L["no_backup_yet"];

        // ---- export & import ----

        static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = L["file_filter"], DefaultExt = ".json", InitialDirectory = Documents,
                FileName = $"Hadaf-{DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json",
            };
            if (dialog.ShowDialog(this) == true) ExportTo(dialog.FileName);
        }

        void ExportTo(string path)
        {
            try
            {
                Store.Export(data, path);
                ShowSnack(L.F("exported", Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                ShowSnack(L.F("export_failed", Describe(ex)));
            }
        }

        void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = L["file_filter"], InitialDirectory = Documents };
            if (dialog.ShowDialog(this) == true) ImportFrom(dialog.FileName);
        }

        /// <summary>
        /// Takes the goals, history and categories from an exported file. What it replaces is backed up first and
        /// can be brought straight back with Undo.
        /// </summary>
        void ImportFrom(string path)
        {
            var incoming = Store.ReadForImport(path);
            if (incoming == null)
            {
                ShowSnack(L["import_invalid"]);
                return;
            }
            Save();
            Store.BackupNow();
            var before = (Goals: data.Goals.ToList(), History: data.History.ToList(), Categories: S.Categories);
            ReplaceData(incoming.Goals, incoming.History, incoming.Settings.Categories);
            ShowSnack(L.F("imported", L.Goals(incoming.Goals.Count)), L["undo"],
                () => ReplaceData(before.Goals, before.History, before.Categories));
        }

        void ReplaceData(List<Goal> goals, List<CompletionRecord> history, List<CategoryDef> categories)
        {
            data.Goals.Clear();
            data.Goals.AddRange(goals);
            data.History.Clear();
            data.History.AddRange(history);
            S.Categories = categories;
            PrepareData();
            RollDailies();
            CategoriesChanged();
            UpdateBackupInfo();
        }

        // ---- categories ----

        /// <summary>
        /// Brings loaded or imported data up to date: the categories in use, goals pointing at a category that is
        /// gone, and completion records from before "HadDeadline" was kept (worked out from their goal).
        /// </summary>
        void PrepareData()
        {
            S.Categories = Categories.Use(S.Categories);
            foreach (var g in data.Goals.Where(g => !Categories.Exists(g.Category))) g.Category = Categories.Fallback;
            foreach (var r in data.History.Where(r => r.HadDeadline == null))
            {
                var g = data.Goals.FirstOrDefault(x => x.Id == r.GoalId);
                r.HadDeadline = g == null || HasRealDeadline(g); // a deleted goal keeps counting, as it did
            }
        }

        /// <summary>The menu's category entries and the editor's category chips.</summary>
        void FillCategoryLists()
        {
            cats.Clear();
            cats.AddRange(Categories.All.Select(c =>
                new NavItem { Key = c.Id, IsCat = true, Brush = Categories.BrushOf(c.Id), Name = Categories.NameOf(c) }));
            suppress = true;
            CatList.ItemsSource = null;
            CatList.ItemsSource = cats;
            CatList.SelectedItem = cats.FirstOrDefault(c => c.Key == cat);
            suppress = false;
            UpdateCounts();

            // Detached while rebuilt, for the same reason as the other pickers (see ApplyLanguage).
            var editing = EditorForm.DataContext;
            syncing = true;
            EditorForm.DataContext = null;
            CatChips.ItemsSource = Categories.All
                .Select(c => new Opt { Key = c.Id, Name = Categories.NameOf(c), Brush = Categories.BrushOf(c.Id) }).ToList();
            EditorForm.DataContext = editing;
            syncing = false;
        }

        void FillCategoryEditor()
        {
            var colors = Palette.Swatches
                .Select(s => new Opt { Key = Palette.Hex(s.Argb), Name = L["col_" + s.Key], Brush = Palette.B(s.Argb) }).ToList();
            CatEditor.ItemsSource = Categories.All.Select(c => new CatRow { Def = c, Colors = colors }).ToList();
            AddCatBtn.Visibility = Categories.All.Count < Categories.Max ? Visibility.Visible : Visibility.Collapsed;
            CatDesc.Text = L.F("cat_desc", Categories.NameOf(Categories.Fallback));
        }

        /// <summary>
        /// After a category was added, renamed, recoloured or deleted. A rename or new colour only refreshes its own
        /// editor row, so a click already on its way to that row isn't lost to a rebuilt list.
        /// </summary>
        void CategoriesChanged(CatRow changed = null)
        {
            if (cat != null && !Categories.Exists(cat))
            {
                cat = null;
                view = "all";
                suppress = true;
                NavList.SelectedIndex = 0;
                suppress = false;
            }
            FillCategoryLists();
            if (changed != null) changed.RefreshAll();
            else FillCategoryEditor();
            RefreshCards();
            Refresh();
            Save();
        }

        void CatColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CatRow row) row.Picking = !row.Picking;
        }

        void CatSwatch_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox { DataContext: CatRow row, SelectedValue: string hex }
                || string.Equals(hex, row.Def.Color, StringComparison.OrdinalIgnoreCase)) return;
            row.Def.Color = hex;
            row.Picking = false;
            CategoriesChanged(row);
        }

        void CatName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitCategoryName((TextBox)sender);
            e.Handled = true;
        }

        void CatName_LostFocus(object sender, RoutedEventArgs e) => CommitCategoryName((TextBox)sender);

        /// <summary>Emptying a built-in category's name (or typing its own) gives it back its name in the current language.</summary>
        void CommitCategoryName(TextBox box)
        {
            if (box.DataContext is not CatRow row) return;
            var def = row.Def;
            var text = box.Text.Trim();
            bool builtIn = Categories.IsBuiltIn(def.Id);
            string name = text.Length == 0 || builtIn && text == Categories.DefaultName(def.Id) ? null : text;
            if (name == null && !builtIn) name = def.Name; // an added category always keeps a name
            if (name != def.Name)
            {
                def.Name = name;
                CategoriesChanged(row);
            }
            box.SetCurrentValue(TextBox.TextProperty, row.Name); // the name in effect, e.g. the built-in one again
        }

        void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            if (Categories.All.Count >= Categories.Max) return;
            var used = Categories.All.Select(c => c.Color?.ToUpperInvariant()).ToHashSet();
            var color = Palette.Swatches.Select(s => Palette.Hex(s.Argb)).FirstOrDefault(h => !used.Contains(h))
                        ?? Palette.Hex(Palette.Swatches[0].Argb);
            var def = new CategoryDef { Id = "c" + Guid.NewGuid().ToString("N")[..8], Name = L["new_category"], Color = color };
            int fallback = S.Categories.FindIndex(c => c.Id == Categories.Fallback);
            S.Categories.Insert(fallback >= 0 ? fallback : S.Categories.Count, def); // "Other" stays last
            CategoriesChanged();
            // Straight into naming it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = CatEditor.ItemContainerGenerator.ContainerFromIndex(S.Categories.IndexOf(def));
                if (FindChild<TextBox>(row) is TextBox box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Its goals move to the fallback category; Undo puts both back.</summary>
        void CatDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CatRow { CanDelete: true } row) return;
            var def = row.Def;
            int index = S.Categories.IndexOf(def);
            var moved = data.Goals.Where(g => g.Category == def.Id).ToList();
            S.Categories.Remove(def);
            foreach (var g in moved) g.Category = Categories.Fallback;
            CategoriesChanged();
            ShowSnack(L.F("cat_deleted", Categories.NameOf(def)), L["undo"], () =>
            {
                if (Categories.Exists(def.Id)) return;
                S.Categories.Insert(Math.Min(index, S.Categories.Count), def);
                foreach (var g in moved.Where(g => g.Category == Categories.Fallback)) g.Category = def.Id;
                CategoriesChanged();
            });
        }

        static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FindChild<T>(child) is T deeper) return deeper;
            }
            return null;
        }
    }
}
