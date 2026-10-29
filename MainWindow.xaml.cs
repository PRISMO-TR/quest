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
            foreach (var id in Categories.Ids)
                cats.Add(new NavItem { Key = id, IsCat = true, Brush = Categories.BrushOf(id) });
            NavList.ItemsSource = nav;
            CatList.ItemsSource = cats;
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
            ReportLoadIssue();
            Dispatcher.BeginInvoke(new Action(CheckReminders), DispatcherPriority.ApplicationIdle);
            // Startup allocates a lot of one-off memory (XAML parsing, JIT); hand it back once idle.
            var trim = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(3) };
            trim.Tick += (s, e) => { trim.Stop(); Native.Trim(); };
            trim.Start();
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
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            Dispatcher.BeginInvoke(new Action(() => { Theme.Apply(this, S); RefreshCards(); }));
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

        static void Slide(FrameworkElement el, double fromOpacity, double toOpacity, double fromX, double toX, Action done)
        {
            var time = TimeSpan.FromMilliseconds(200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(fromOpacity, toOpacity, time) { EasingFunction = ease };
            if (done != null) fade.Completed += (s, e) => done();
            el.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, time) { EasingFunction = ease });
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
            DrawerOverlay.Visibility = Visibility.Visible;
            var time = TimeSpan.FromMilliseconds(220);
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-(Drawer.Width + 16), 0, time)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            DrawerScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        }

        void CloseDrawer()
        {
            if (!drawerOpen) return;
            drawerOpen = false;
            var time = TimeSpan.FromMilliseconds(160);
            var slide = new DoubleAnimation(-(Drawer.Width + 16), time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            slide.Completed += (s, e) => { if (!drawerOpen) DrawerOverlay.Visibility = Visibility.Collapsed; };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
            DrawerScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
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
            foreach (var c in cats) c.Name = L["cat_" + c.Key];

            // Swapping a picker's items briefly clears its selection, and a two-way binding would write
            // that into the goal being edited; detach the editor while the lists are rebuilt.
            var editing = EditorForm.DataContext;
            syncing = true;
            EditorForm.DataContext = null;
            CatChips.ItemsSource = Categories.Ids
                .Select(id => new Opt { Key = id, Name = L["cat_" + id], Brush = Categories.BrushOf(id) }).ToList();
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
                "today" => q.Where(g => g.DoneToday || (!g.IsChecked && g.Due?.Date == today)),
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
            UpdateStreaks();
            UpdateCounts();
            bool stats = view == "stats", daily = view == "daily";
            ViewTitle.Text = cat != null ? L["cat_" + cat] : L["nav_" + view];
            StatsView.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
            GoalList.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
            SearchToggle.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
            SearchRow.Visibility = !stats && searchOpen ? Visibility.Visible : Visibility.Collapsed;
            Composer.Visibility = stats || view == "done" ? Visibility.Collapsed : Visibility.Visible;
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
                    "today" => active.Count(g => !g.DoneToday && g.Due?.Date == today),
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
            var g = new Goal { Title = title, Category = cat ?? "personal" };
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
                data.History.Add(Record(g, now));
                if (g.IsRecurring)
                {
                    var next = g.Due ?? now;
                    do next = Goal.Advance(next, g.Recurrence); while (next <= now);
                    g.Due = next;
                    foreach (var s in g.SubTasks) s.Done = false;
                    g.NotifiedBefore = g.NotifiedDue = false;
                    ShowSnack(L.F("next_occ", L.FormatDate(next)));
                }
                else
                {
                    g.Done = true;
                    g.CompletedAt = now;
                    ShowSnack(L["great"]);
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
            ShowSnack(g.Streak >= 2 ? L.F("daily_done_streak", L.DaysPhrase(g.Streak)) : L["daily_done"]);
        }

        static CompletionRecord Record(Goal g, DateTime now) => new()
        {
            GoalId = g.Id, Title = g.Title, Category = g.Category, At = now,
            OnTime = !g.Due.HasValue || now <= g.Due.Value,
        };

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
            SnackText.Text = text;
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
            var lines = new List<(string Head, string Body)>();
            foreach (var g in data.Goals.Where(g => !g.Done && g.Due.HasValue))
            {
                // Any-time daily tasks have no clock to warn about; ones done today are settled.
                if (g.IsDaily && (g.AnyTime || g.DoneToday)) continue;
                if (!g.NotifiedDue && now >= g.Due.Value)
                {
                    g.NotifiedDue = g.NotifiedBefore = true;
                    lines.Add((L["n_due"], g.Title));
                }
                else if (!g.NotifiedBefore && g.RemindMinutes > 0 && now >= g.Due.Value.AddMinutes(-g.RemindMinutes))
                {
                    g.NotifiedBefore = true;
                    lines.Add((L["n_soon"], $"{g.Title} · {L.F("left_fmt", L.Dur(g.Due.Value - now))}"));
                }
            }
            if (lines.Count == 0) return;
            Save();
            if (!S.Notifications) return;
            if (lines.Count == 1) App.Notify(lines[0].Head, lines[0].Body);
            else App.Notify(L.F("n_many", lines.Count), string.Join("\n", lines.Take(5).Select(l => "• " + l.Body)));
        }

        // ================= editor page =================

        void OpenEditor(Goal g, string title = null)
        {
            original = g;
            if (g != null) draft = Store.Clone(g);
            else
            {
                draft = new Goal { Title = title ?? "", Category = cat ?? "personal" };
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
            if (!Categories.Ids.Contains(d.Category))
                d.Category = Categories.Ids.Contains(original?.Category) ? original.Category : "personal";
            if (d.IsDaily)
            {
                if (!d.Due.HasValue) d.Due = d.NextRun(DateTime.Today, true);
                d.Done = false; // a daily task is never finished for good
                d.CompletedAt = null;
            }
            else d.AnyTime = false;
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
                var toggle = new CheckBox
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
            var time = draft.Due?.TimeOfDay ?? ParseTime(TimeBox.Text) ?? new TimeSpan(18, 0, 0);
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
            if (ParseTime(TimeBox.Text) is TimeSpan t)
            {
                draft.Due = draft.Due.Value.Date + t;
                if (draft.IsDaily) draft.Due = draft.NextRun(DailyFrom(draft), true);
                DueSummary.Text = Summary(draft);
            }
        }

        static TimeSpan? ParseTime(string s)
        {
            s = (s ?? "").Trim().Replace('.', ':');
            if (TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)
                && t < TimeSpan.FromDays(1))
                return t;
            return null;
        }

        // ================= statistics =================

        void BuildStats()
        {
            var h = data.History;
            var today = DateTime.Today;
            var accent = (SolidColorBrush)FindResource("Accent");

            int total = h.Count;
            int week = h.Count(r => r.At >= today.AddDays(-6));
            int onTime = total == 0 ? 0 : (int)Math.Round(100.0 * h.Count(r => r.OnTime) / total);
            var days = h.Select(r => r.At.Date).ToHashSet();
            int streak = 0;
            var d = days.Contains(today) ? today : today.AddDays(-1);
            while (days.Contains(d)) { streak++; d = d.AddDays(-1); }

            Tiles.ItemsSource = new[]
            {
                new Tile("", total.ToString(), L["s_total"], Palette.B(0xFF10B981)),
                new Tile("", week.ToString(), L["s_week"], accent),
                new Tile("", onTime + "%", L["s_ontime"], Palette.B(0xFFF59E0B)),
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

            CatStats.ItemsSource = Categories.Ids.Select(id =>
            {
                int n = h.Count(r => r.Category == id);
                return new CatStat(L["cat_" + id], Categories.BrushOf(id), total == 0 ? 0 : 100.0 * n / total, n);
            }).ToList();
        }

        // ================= settings page =================

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
            HideSnack();
            SyncSettingsUi();
            ShowPage(SettingsPage);
        }

        void SettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

        void CloseSettings()
        {
            Save();
            ShowPage(HomePage);
        }

        void SyncSettingsUi()
        {
            suppress = true;
            LangChips.SelectedValue = S.Lang;
            ThemeChips.SelectedValue = S.Theme;
            FxChips.SelectedValue = S.Effect;
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
                SyncSettingsUi();
            }
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
            ShowNotice("save", true, L["save_failed_title"], L.F("save_failed_text", Describe(error)), L["retry"], Save);
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
                ShowNotice("load", false, L["load_problem_title"],
                    L.F("load_restored_text", L.FormatDate(issue.BackupTime ?? DateTime.Now)), L["open_data_folder"], OpenDataFolder);
            else
                ShowNotice("load", true, L["load_problem_title"], L["load_failed_text"], L["open_data_folder"], OpenDataFolder);
            // The damaged file is already set aside; make the recovered data the main file again.
            Save();
        }

        void ShowNotice(string key, bool danger, string title, string text, string actionText = null, Action action = null)
        {
            noticeKey = key;
            noticeAction = action;
            NoticeTitle.Text = title;
            NoticeText.Text = text;
            NoticeAction.Content = actionText;
            NoticeAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            Notice.SetResourceReference(Border.BackgroundProperty, danger ? "DangerSoft" : "WarnSoft");
            Notice.SetResourceReference(Border.BorderBrushProperty, danger ? "DangerStroke" : "WarnStroke");
            NoticeIcon.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Warn");
            Notice.Visibility = Visibility.Visible;
        }

        void HideNotice(string key = null)
        {
            if (key != null && key != noticeKey) return;
            noticeKey = null;
            noticeAction = null;
            Notice.Visibility = Visibility.Collapsed;
        }

        void NoticeAction_Click(object sender, RoutedEventArgs e) => noticeAction?.Invoke();
        void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice();
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
    }
}
