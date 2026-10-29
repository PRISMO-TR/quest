using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hadaf
{
    // The goal list: views, cards, the composer, the snackbar and reminders.
    public partial class MainWindow
    {
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

        IEnumerable<Goal> Filter() => Planner.Filter(data.Goals, view, cat, SearchBox.Text, DateTime.Now);

        /// <summary>
        /// "All" and category views are split into buckets (daily, then by time); the dedicated
        /// views (today, daily, overdue …) already are one bucket, so they stay a flat list.
        /// </summary>
        List<object> BuildEntries(List<Goal> goals)
        {
            var list = new List<object>();
            if (view != "all")
            {
                list.AddRange(Planner.SortedFor(view, goals));
                return list;
            }
            bool searching = SearchBox.Text.Trim().Length > 0;
            foreach (var (key, items) in Planner.Groups(goals, DateTime.Now))
            {
                bool isDone = key == "done", isDaily = key == "daily";
                int planned = isDaily ? Planner.PlannedToday(items, DateTime.Now) : 0;
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
                if (header.Expanded) list.AddRange(items);
            }
            return list;
        }

        void Refresh()
        {
            OfferStartWithWindows();
            Planner.UpdateStreaks(data, DateTime.Now);
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
                ? L.F("daily_progress", goals.Count(g => g.DoneToday), Planner.PlannedToday(goals, DateTime.Now))
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
            foreach (var n in nav)
            {
                n.Count = Planner.MenuCount(data.Goals, n.Key, now);
                n.IsAlert = n.Key == "overdue" && n.Count > 0;
            }
            foreach (var c in cats) c.Count = data.Goals.Count(g => !g.Done && g.Category == c.Key);
            int open = Planner.MenuCount(data.Goals, "all", now), overdue = Planner.MenuCount(data.Goals, "overdue", now);
            DrawerSummary.Text = overdue > 0 ? $"{L.Goals(open)}  ·  {L.F("overdue_n", overdue)}" : L.Goals(open);
        }

        void Tick()
        {
            if (saveError != null) Save();
            bool rolled = Planner.RollDailies(data.Goals, DateTime.Now);
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
            if (GoalOf(sender) is Goal g) OpenStepsOrEditor(g);
        }

        /// <summary>A goal with steps opens or closes them in place; one without steps opens the editor.</summary>
        void OpenStepsOrEditor(Goal g)
        {
            if (g.HasSubTasks) g.Expanded = !g.Expanded;
            else OpenEditor(g);
        }

        // A click that just misses a step's tick box stays in the list, instead of folding it away.
        void StepList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        void Edit_Click(object sender, RoutedEventArgs e)
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

        /// <summary>
        /// Enter does what a click does (steps, or the editor), Space ticks, Delete removes; Shift+F10 or the menu key
        /// opens the card's menu.
        /// </summary>
        void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource != sender || GoalOf(sender) is not Goal g) return;
            switch (e.Key)
            {
                case Key.Enter: OpenStepsOrEditor(g); break;
                case Key.Space: ToggleDone(g); break;
                case Key.Delete: DeleteGoal(g); break;
                default: return;
            }
            e.Handled = true;
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
            var undo = Planner.Postpone(g, DateTime.Now);
            Changed(g);
            ShowSnack(L.F("snoozed", L.FormatDate(g.Due.Value)), L["undo"], () =>
            {
                undo();
                Changed(g);
            });
        }

        /// <summary>Ticks a goal off, or takes the tick back; each tick offers Undo.</summary>
        void ToggleDone(Goal g)
        {
            var now = DateTime.Now;
            if (g.IsDaily) ToggleDaily(g, now);
            else if (g.Done) Planner.Reopen(data, g);
            else if (g.IsRecurring)
            {
                var undo = Planner.CompleteOccurrence(data, g, now);
                ShowSnack(L.F("next_occ", L.FormatDate(g.Due.Value)), L["undo"], () => { if (undo()) Changed(g); });
            }
            else
            {
                Planner.Finish(data, g, now);
                ShowSnack(L["great"], L["undo"], () => { if (g.Done) ToggleDone(g); });
            }
            Changed(g);
        }

        void ToggleDaily(Goal g, DateTime now)
        {
            if (g.DoneToday)
            {
                Planner.UntickDaily(data, g, now);
                return;
            }
            Planner.TickDaily(data, g, now);
            Planner.UpdateStreaks(data, now);
            ShowSnack(g.Streak >= 2 ? L.F("daily_done_streak", L.DaysPhrase(g.Streak)) : L["daily_done"], L["undo"], () =>
            {
                if (!g.DoneToday) return;
                ToggleDaily(g, DateTime.Now);
                Changed(g);
            });
        }

        /// <summary>After a goal changed outside the editor: redraw its card, store it, rebuild the list.</summary>
        void Changed(Goal g)
        {
            g.RefreshAll();
            Save();
            Refresh();
        }

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
            var lines = Planner.TakeReminders(data.Goals, now).Select(r => r.Due
                ? (Head: L["n_due"], Body: r.Goal.Title, GoalId: r.Goal.Id)
                : (Head: L["n_soon"], Body: $"{r.Goal.Title} · {L.F("left_fmt", L.Dur(r.Goal.Due.Value - now))}", GoalId: r.Goal.Id))
                .ToList();
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
    }
}
