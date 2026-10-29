using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hadaf
{
    // The editor page: a goal's details, schedule, repeat, steps and time.
    public partial class MainWindow
    {
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

            // The editor's pickers are still bound to the goal, so keep their change handlers out of the way
            // while it is tidied: otherwise clearing AnyTime would re-plan the deadline.
            syncing = true;
            Planner.Tidy(d, original, DateTime.Now);
            syncing = false;
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

        /// <summary>Turns a step into a part heading over the steps below it, or back into a step.</summary>
        void StepKind_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SubTask s) return;
            s.IsHeading = !s.IsHeading;
            s.Done = false; // a heading has nothing to tick
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
    }
}
