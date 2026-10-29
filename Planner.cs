using System;
using System.Collections.Generic;
using System.Linq;

namespace Hadaf
{
    /// <summary>
    /// The rules behind the lists, kept apart from the window: which goals a view shows and in which group, how
    /// ticking a goal off moves it on, daily renewals, streaks, reminders and statistics. The time is passed in,
    /// so the tests can run every rule at any moment.
    /// </summary>
    public static class Planner
    {
        // ================= views =================

        /// <summary>Groups of the "all" and category views, top to bottom.</summary>
        public static readonly string[] GroupOrder = { "overdue", "daily", "today", "tomorrow", "week", "later", "none", "done" };

        /// <summary>The goals a view shows ("all", "today", "daily", "upcoming", "overdue", "done"), optionally of one category and matching a search.</summary>
        public static IEnumerable<Goal> Filter(IEnumerable<Goal> goals, string view, string category, string search, DateTime now)
        {
            var today = now.Date;
            if (category != null) goals = goals.Where(g => g.Category == category);
            goals = view switch
            {
                // Today: everything still open that is due today or already late, plus what was finished today.
                "today" => goals.Where(g => g.DoneToday || (g.Done && g.CompletedAt?.Date == today)
                                         || (!g.IsChecked && g.Due.HasValue && g.Due.Value.Date <= today)),
                "daily" => goals.Where(g => g.IsDaily),
                "upcoming" => goals.Where(g => !g.Done && !g.IsDaily && g.Due > now && g.Due <= now.AddDays(7)),
                "overdue" => goals.Where(g => g.IsOverdue),
                "done" => goals.Where(g => g.Done),
                _ => goals,
            };
            var term = search?.Trim() ?? "";
            if (term.Length > 0)
                goals = goals.Where(g => g.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                      || (g.Notes ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase));
            return goals;
        }

        /// <summary>The number the menu shows next to a view: what is still open there (all finished ones for "done").</summary>
        public static int MenuCount(IEnumerable<Goal> goals, string view, DateTime now)
        {
            var today = now.Date;
            var open = goals.Where(g => !g.Done);
            return view switch
            {
                "all" => open.Count(),
                "today" => open.Count(g => !g.IsChecked && g.Due.HasValue && g.Due.Value.Date <= today),
                "daily" => open.Count(g => g.IsDaily && !g.DoneToday && g.Due?.Date == today),
                "upcoming" => open.Count(g => !g.IsDaily && g.Due > now && g.Due <= now.AddDays(7)),
                "overdue" => open.Count(g => g.IsOverdue),
                "done" => goals.Count(g => g.Done),
                _ => 0,
            };
        }

        /// <summary>Which group of the "all" view a goal belongs to (see <see cref="GroupOrder"/>).</summary>
        public static string GroupOf(Goal g, DateTime now)
        {
            if (g.Done) return "done";
            if (g.IsDaily) return "daily";
            if (!g.Due.HasValue) return "none";
            var due = g.Due.Value;
            var today = now.Date;
            if (due < now) return "overdue";
            if (due.Date == today) return "today";
            if (due.Date == today.AddDays(1)) return "tomorrow";
            if (due.Date <= today.AddDays(7)) return "week";
            return "later";
        }

        /// <summary>The groups that have goals, in order, each sorted the way it is shown.</summary>
        public static IEnumerable<(string Key, List<Goal> Goals)> Groups(IEnumerable<Goal> goals, DateTime now)
        {
            var byGroup = goals.ToLookup(g => GroupOf(g, now));
            foreach (var key in GroupOrder.Where(byGroup.Contains))
                yield return (key, SortedFor(key is "done" or "daily" ? key : "all", byGroup[key]).ToList());
        }

        /// <summary>
        /// The order of a view or group: newest first among finished ones, open daily tasks before the ones done today,
        /// in Today the open ones first; otherwise by deadline, then priority.
        /// </summary>
        public static IEnumerable<Goal> SortedFor(string view, IEnumerable<Goal> goals) => view switch
        {
            "done" => goals.OrderByDescending(g => g.CompletedAt),
            "daily" => goals.OrderBy(g => g.DoneToday).ThenBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority),
            "today" => goals.OrderBy(g => g.IsChecked).ThenBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority),
            _ => goals.OrderBy(g => g.Due ?? DateTime.MaxValue).ThenByDescending(g => g.Priority).ThenBy(g => g.CreatedAt),
        };

        /// <summary>Daily tasks that run today, done or not.</summary>
        public static int PlannedToday(IEnumerable<Goal> dailies, DateTime now) =>
            dailies.Count(g => g.DoneToday || g.Due?.Date == now.Date);

        // ================= ticking off =================

        /// <summary>The history entry for a goal ticked off at <paramref name="now"/>.</summary>
        public static CompletionRecord Record(Goal g, DateTime now) => new()
        {
            GoalId = g.Id, Title = g.Title, Category = g.Category, At = now,
            OnTime = !g.Due.HasValue || now <= g.Due.Value,
            HadDeadline = HasRealDeadline(g),
        };

        /// <summary>A deadline the goal could miss: it has one, and it isn't an any-time daily task.</summary>
        public static bool HasRealDeadline(Goal g) => g.Due.HasValue && !(g.IsDaily && g.AnyTime);

        /// <summary>Finishes a one-off goal.</summary>
        public static void Finish(AppData data, Goal g, DateTime now)
        {
            data.History.Add(Record(g, now));
            g.Done = true;
            g.CompletedAt = now;
        }

        /// <summary>Opens a finished one-off goal again and takes back its completion.</summary>
        public static void Reopen(AppData data, Goal g)
        {
            g.Done = false;
            g.CompletedAt = null;
            var rec = data.History.LastOrDefault(h => h.GoalId == g.Id);
            if (rec != null) data.History.Remove(rec);
        }

        /// <summary>
        /// Ticks off the current occurrence of a repeating goal: it moves on to its first occurrence after
        /// <paramref name="now"/>, with its steps cleared. Returns the undo, which does nothing (and says false)
        /// once the goal has moved on again.
        /// </summary>
        public static Func<bool> CompleteOccurrence(AppData data, Goal g, DateTime now)
        {
            if (!g.IsRecurring) throw new ArgumentException("Only a repeating goal has a next occurrence.", nameof(g));
            var record = Record(g, now);
            data.History.Add(record);
            var before = (Due: g.Due, Steps: g.SubTasks.Select(s => s.Done).ToArray(), g.NotifiedBefore, g.NotifiedDue);
            var next = g.Due ?? now;
            do next = g.NextAfter(next); while (next <= now);
            g.Due = next;
            foreach (var s in g.SubTasks) s.Done = false;
            g.NotifiedBefore = g.NotifiedDue = false;
            return () =>
            {
                if (g.Due != next) return false;
                g.Due = before.Due;
                for (int i = 0; i < Math.Min(before.Steps.Length, g.SubTasks.Count); i++) g.SubTasks[i].Done = before.Steps[i];
                g.NotifiedBefore = before.NotifiedBefore;
                g.NotifiedDue = before.NotifiedDue;
                data.History.Remove(record);
                return true;
            };
        }

        /// <summary>Marks today's run of a daily task done; it comes back unticked at its next run.</summary>
        public static void TickDaily(AppData data, Goal g, DateTime now)
        {
            data.History.Add(Record(g, now));
            g.LastDoneAt = now;
            g.Due = g.NextRun(now.Date.AddDays(1), false);
            foreach (var s in g.SubTasks) s.Done = false;
            g.NotifiedBefore = g.NotifiedDue = false;
        }

        /// <summary>Takes back today's tick of a daily task: today's run is open again.</summary>
        public static void UntickDaily(AppData data, Goal g, DateTime now)
        {
            var today = now.Date;
            g.LastDoneAt = null;
            var rec = data.History.LastOrDefault(h => h.GoalId == g.Id && h.At.Date == today);
            if (rec != null) data.History.Remove(rec);
            g.Due = g.NextRun(today, false);
            g.NotifiedBefore = g.NotifiedDue = g.Due <= now;
        }

        /// <summary>Moves the deadline to tomorrow at the same time (see <see cref="Goal.CanSnooze"/>). Returns the undo.</summary>
        public static Action Postpone(Goal g, DateTime now)
        {
            var before = (g.Due, g.NotifiedBefore, g.NotifiedDue);
            g.Due = now.Date.AddDays(1) + g.Due.Value.TimeOfDay;
            g.NotifiedBefore = g.NotifiedDue = false;
            return () => (g.Due, g.NotifiedBefore, g.NotifiedDue) = before;
        }

        // ================= daily tasks =================

        /// <summary>
        /// Daily tasks never pile up as overdue: once their day is over they move on to the next run with their
        /// steps cleared, like a checklist that starts fresh every morning. Says whether any moved.
        /// </summary>
        public static bool RollDailies(IEnumerable<Goal> goals, DateTime now)
        {
            bool changed = false;
            var today = now.Date;
            foreach (var g in goals.Where(g => g.IsDaily && !g.Done && !g.DoneToday))
            {
                if (g.Due.HasValue && g.Due.Value.Date >= today) continue;
                g.Due = g.NextRun(today, false);
                foreach (var s in g.SubTasks) s.Done = false;
                g.NotifiedBefore = g.NotifiedDue = g.Due <= now;
                g.RefreshAll();
                changed = true;
            }
            return changed;
        }

        /// <summary>Sets every daily task's streak from the history.</summary>
        public static void UpdateStreaks(AppData data, DateTime now)
        {
            var doneDays = data.History.Where(h => h.GoalId != null).GroupBy(h => h.GoalId)
                .ToDictionary(x => x.Key, x => x.Select(h => h.At.Date).ToHashSet());
            foreach (var g in data.Goals.Where(g => g.IsDaily))
            {
                int streak = doneDays.TryGetValue(g.Id, out var days) ? StreakOf(g, days, now) : 0;
                if (streak == g.Streak) continue;
                g.Streak = streak;
                g.RefreshAll();
            }
        }

        /// <summary>Consecutive scheduled days a daily task was done, ending today (or yesterday while today is still open).</summary>
        public static int StreakOf(Goal g, HashSet<DateTime> days, DateTime now)
        {
            var day = days.Contains(now.Date) ? now.Date : now.Date.AddDays(-1);
            int n = 0;
            for (int i = 0; i < 20000 && day >= g.CreatedAt.Date; i++, day = day.AddDays(-1))
            {
                if (!g.RunsOn(day.DayOfWeek)) continue;
                if (!days.Contains(day)) break;
                n++;
            }
            return n;
        }

        // ================= reminders =================

        /// <summary>A reminder to show: the deadline arrived (<paramref name="Due"/>), or the time to be warned before it did.</summary>
        public sealed record Reminder(Goal Goal, bool Due);

        /// <summary>
        /// The reminders due at <paramref name="now"/>. Each fires once: the goal keeps a note that it was reminded.
        /// Any-time daily tasks have no clock to warn about, and the ones done today are settled.
        /// </summary>
        public static List<Reminder> TakeReminders(IEnumerable<Goal> goals, DateTime now)
        {
            var reminders = new List<Reminder>();
            foreach (var g in goals.Where(g => !g.Done && g.Due.HasValue))
            {
                if (g.IsDaily && (g.AnyTime || g.DoneToday)) continue;
                if (!g.NotifiedDue && now >= g.Due.Value)
                {
                    g.NotifiedDue = g.NotifiedBefore = true;
                    reminders.Add(new Reminder(g, true));
                }
                else if (!g.NotifiedBefore && g.RemindMinutes > 0 && now >= g.Due.Value.AddMinutes(-g.RemindMinutes))
                {
                    g.NotifiedBefore = true;
                    reminders.Add(new Reminder(g, false));
                }
            }
            return reminders;
        }

        // ================= editing =================

        /// <summary>
        /// Tidies an edited goal before it is stored: a category that exists, a daily task's first run, a monthly goal's
        /// day of the month, and reminders that must not fire again (or at all, for a deadline set in the past).
        /// </summary>
        public static void Tidy(Goal d, Goal original, DateTime now)
        {
            if (!Categories.Exists(d.Category))
                d.Category = Categories.Exists(original?.Category) ? original.Category : Categories.Default;
            if (d.IsDaily)
            {
                if (!d.Due.HasValue) d.Due = d.NextRun(now.Date, true);
                d.Done = false; // a daily task is never finished for good
                d.CompletedAt = null;
            }
            else d.AnyTime = false;
            // A monthly goal belongs to the day the user picked; only a newly picked date moves it.
            if (d.Recurrence != Recurrence.Monthly || !d.Due.HasValue) d.RepeatDay = 0;
            else if (original == null || original.Recurrence != Recurrence.Monthly || original.Due?.Date != d.Due.Value.Date)
                d.RepeatDay = d.Due.Value.Day;

            bool rescheduled = original == null || original.Due != d.Due || original.RemindMinutes != d.RemindMinutes;
            if (rescheduled)
            {
                // Don't fire for a deadline that was already in the past when it was set.
                bool past = d.Due.HasValue && d.Due.Value <= now;
                d.NotifiedDue = past;
                d.NotifiedBefore = past;
            }
            else if (original != null)
            {
                // A reminder may have fired while the editor was open; keep that, or it would fire again.
                d.NotifiedBefore = original.NotifiedBefore;
                d.NotifiedDue = original.NotifiedDue;
            }
        }

        // ================= statistics =================

        /// <summary>
        /// The statistics page's numbers. <paramref name="OnTimePercent"/> counts only goals that could be late, and is
        /// null until there is one; <paramref name="Week"/> is completions per day, oldest first, ending today.
        /// </summary>
        public sealed record Statistics(int Total, int LastSevenDays, int? OnTimePercent, int DayStreak,
                                        IReadOnlyList<(DateTime Day, int Count)> Week);

        public static Statistics Stats(IReadOnlyCollection<CompletionRecord> history, DateTime now)
        {
            var today = now.Date;
            var timed = history.Where(r => r.HadDeadline != false).ToList();
            int? onTime = timed.Count == 0 ? null : (int)Math.Round(100.0 * timed.Count(r => r.OnTime) / timed.Count);
            var days = history.Select(r => r.At.Date).ToHashSet();
            int streak = 0;
            for (var d = days.Contains(today) ? today : today.AddDays(-1); days.Contains(d); d = d.AddDays(-1)) streak++;
            var week = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
                .Select(day => (day, history.Count(r => r.At.Date == day))).ToList();
            return new Statistics(history.Count, history.Count(r => r.At >= today.AddDays(-6)), onTime, streak, week);
        }

        /// <summary>Completions in a category; those of a deleted category count as the fallback's, like its goals.</summary>
        public static int CompletionsIn(IEnumerable<CompletionRecord> history, string category) =>
            history.Count(r => r.Category == category || category == Categories.Fallback && !Categories.Exists(r.Category));

        // ================= data from older versions and other PCs =================

        /// <summary>
        /// Brings loaded or imported data up to date: the categories in use, goals pointing at a category that is
        /// gone, and completion records from before "HadDeadline" was kept (worked out from their goal).
        /// </summary>
        public static void Prepare(AppData data)
        {
            data.Settings.Categories = Categories.Use(data.Settings.Categories);
            foreach (var g in data.Goals.Where(g => !Categories.Exists(g.Category))) g.Category = Categories.Fallback;
            foreach (var r in data.History.Where(r => r.HadDeadline == null))
            {
                var g = data.Goals.FirstOrDefault(x => x.Id == r.GoalId);
                r.HadDeadline = g == null || HasRealDeadline(g); // a deleted goal keeps counting, as it did
            }
        }
    }
}
