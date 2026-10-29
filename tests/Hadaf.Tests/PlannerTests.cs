using System;
using System.Collections.Generic;
using System.Linq;

namespace Hadaf.Tests
{
    static class PlannerTests
    {
        static DateTime Now => DateTime.Now;
        static DateTime Today => DateTime.Today;

        static Goal Daily(string title, DateTime due, bool anyTime = true, DateTime? doneAt = null) => new()
        {
            Title = title, Recurrence = Recurrence.Daily, AnyTime = anyTime, Due = due, LastDoneAt = doneAt,
        };

        static string Titles(IEnumerable<Goal> goals) => string.Join(", ", goals.Select(g => g.Title));

        // ================= views =================

        [Test]
        static void TodayShowsWhatIsLateDueOrDoneToday()
        {
            var goals = new List<Goal>
            {
                new() { Title = "late", Due = Today.AddDays(-1).AddHours(10) },
                new() { Title = "due", Due = Today.AddHours(23).AddMinutes(59) },
                new() { Title = "done", Due = Today.AddHours(8), Done = true, CompletedAt = Now },
                new() { Title = "done yesterday", Due = Today.AddDays(-1), Done = true, CompletedAt = Now.AddDays(-1) },
                new() { Title = "tomorrow", Due = Today.AddDays(1).AddHours(9) },
                Daily("habit", Today.AddDays(1).AddHours(23).AddMinutes(59), doneAt: Now),
            };
            var shown = Planner.SortedFor("today", Planner.Filter(goals, "today", null, "", Now)).ToList();
            Assert.Equal("late, due, done, habit", Titles(shown), "open ones first, by deadline");
        }

        [Test]
        static void SearchLooksAtTitlesAndNotes()
        {
            var goals = new List<Goal> { new() { Title = "Read a book" }, new() { Title = "Gym", Notes = "leg day, read the plan" }, new() { Title = "Pay rent" } };
            Assert.Equal("Read a book, Gym", Titles(Planner.Filter(goals, "all", null, "  READ ", Now)), "case and spaces ignored");
            Assert.Equal("Pay rent", Titles(Planner.Filter(goals, "all", null, "rent", Now)), "single match");
        }

        [Test]
        static void CategoryViewShowsOnlyThatCategory()
        {
            var goals = new List<Goal> { new() { Title = "a", Category = "work" }, new() { Title = "b", Category = "health" } };
            Assert.Equal("a", Titles(Planner.Filter(goals, "all", "work", null, Now)));
        }

        [Test]
        static void GoalsLandInTheRightGroup()
        {
            var noon = Today.AddHours(12);
            string Group(Goal g) => Planner.GroupOf(g, noon);
            Assert.Equal("overdue", Group(new Goal { Due = noon.AddHours(-1) }));
            Assert.Equal("today", Group(new Goal { Due = noon.AddHours(1) }));
            Assert.Equal("tomorrow", Group(new Goal { Due = Today.AddDays(1).AddHours(9) }));
            Assert.Equal("week", Group(new Goal { Due = Today.AddDays(3) }));
            Assert.Equal("later", Group(new Goal { Due = Today.AddDays(30) }));
            Assert.Equal("none", Group(new Goal()));
            Assert.Equal("done", Group(new Goal { Due = noon, Done = true }));
            Assert.Equal("daily", Group(Daily("d", noon)));
        }

        [Test]
        static void GroupsComeInOrderWithoutEmptyOnes()
        {
            var noon = Today.AddHours(12);
            var goals = new List<Goal>
            {
                new() { Title = "later", Due = Today.AddDays(30) },
                new() { Title = "done", Done = true, CompletedAt = noon },
                new() { Title = "late B", Due = noon.AddHours(-1), Priority = Priority.Low },
                new() { Title = "late A", Due = noon.AddHours(-1), Priority = Priority.High },
            };
            var groups = Planner.Groups(goals, noon).ToList();
            Assert.Equal("overdue, later, done", string.Join(", ", groups.Select(x => x.Key)), "group order");
            Assert.Equal("late A, late B", Titles(groups[0].Goals), "same deadline: higher priority first");
        }

        [Test]
        static void MenuCountsWhatIsStillOpen()
        {
            var openDaily = Daily("open daily", Today.AddHours(23).AddMinutes(59));
            var goals = new List<Goal>
            {
                new() { Title = "late", Due = Today.AddDays(-1).AddHours(10) },
                new() { Title = "done", Due = Today, Done = true, CompletedAt = Now },
                openDaily,
                Daily("done daily", Today.AddDays(1).AddHours(23).AddMinutes(59), doneAt: Now),
                new() { Title = "in two days", Due = Now.AddDays(2) },
                new() { Title = "in a month", Due = Now.AddDays(30) },
            };
            Assert.Equal(5, Planner.MenuCount(goals, "all", Now), "all");
            Assert.Equal(2, Planner.MenuCount(goals, "today", Now), "today");
            Assert.Equal(1, Planner.MenuCount(goals, "daily", Now), "daily");
            Assert.Equal(1, Planner.MenuCount(goals, "upcoming", Now), "next 7 days");
            // An any-time daily task is due at 23:59, so in the day's last minute it is late too.
            Assert.Equal(openDaily.IsOverdue ? 2 : 1, Planner.MenuCount(goals, "overdue", Now), "overdue");
            Assert.Equal(1, Planner.MenuCount(goals, "done", Now), "done");
        }

        // ================= ticking off =================

        [Test]
        static void FinishingAndReopeningAOneOffGoal()
        {
            var data = new AppData();
            var g = new Goal { Title = "report", Due = Now.AddDays(1) };
            data.Goals.Add(g);
            Planner.Finish(data, g, Now);
            Assert.True(g.Done && g.CompletedAt.HasValue, "finished");
            Assert.Equal(1, data.History.Count, "recorded");
            Assert.True(data.History[0].OnTime && data.History[0].HadDeadline == true, "on time, with a deadline");
            Planner.Reopen(data, g);
            Assert.True(!g.Done && g.CompletedAt == null, "open again");
            Assert.Equal(0, data.History.Count, "record taken back");
        }

        [Test]
        static void LateOrDeadlineFreeCompletionsAreRecordedAsSuch()
        {
            Assert.False(Planner.Record(new Goal { Due = Now.AddDays(-1) }, Now).OnTime, "late");
            Assert.Equal(false, Planner.Record(new Goal(), Now).HadDeadline, "no deadline");
            Assert.Equal(false, Planner.Record(Daily("d", Today.AddHours(23).AddMinutes(59)), Now).HadDeadline, "any-time daily task");
            Assert.Equal(true, Planner.Record(Daily("d", Today.AddHours(7), anyTime: false), Now).HadDeadline, "daily task with a time");
        }

        [Test]
        static void RepeatingGoalMovesToItsFirstOccurrenceAfterNow()
        {
            var data = new AppData();
            var first = Today.AddDays(-15).AddHours(9);
            var g = new Goal { Title = "weekly", Recurrence = Recurrence.Weekly, Due = first };
            Planner.CompleteOccurrence(data, g, Now);
            var next = g.Due.Value;
            Assert.True(next > Now && next <= Now.AddDays(7), $"next one within a week, got {next}");
            Assert.Equal(0, (next - first).Days % 7, "on the same weekday");
            Assert.Equal(first.TimeOfDay, next.TimeOfDay, "at the same time");
        }

        [Test]
        static void UndoPutsTheOccurrenceBackExactly()
        {
            var data = new AppData();
            var due = Now.AddHours(2);
            var g = new Goal { Recurrence = Recurrence.Monthly, Due = due, NotifiedBefore = true };
            g.SubTasks.Add(new SubTask { Title = "a", Done = true });
            g.SubTasks.Add(new SubTask { Title = "b" });
            var undo = Planner.CompleteOccurrence(data, g, Now);
            Assert.True(g.Due > due && !g.SubTasks[0].Done && !g.NotifiedBefore, "moved on, steps and reminders reset");
            Assert.True(undo(), "undo");
            Assert.Equal(due, g.Due, "deadline back");
            Assert.True(g.SubTasks[0].Done && !g.SubTasks[1].Done && g.NotifiedBefore, "steps and reminder state back");
            Assert.Equal(0, data.History.Count, "record taken back");
        }

        [Test]
        static void UndoDoesNothingOnceTheGoalHasMovedAgain()
        {
            var data = new AppData();
            var g = new Goal { Recurrence = Recurrence.Weekly, Due = Now.AddHours(1) };
            var undo = Planner.CompleteOccurrence(data, g, Now);
            g.Due = g.Due.Value.AddDays(3); // edited in the meantime
            var edited = g.Due;
            Assert.False(undo(), "nothing to undo");
            Assert.Equal(edited, g.Due, "the edit stays");
            Assert.Equal(1, data.History.Count, "the record stays");
        }

        [Test]
        static void OnlyRepeatingGoalsHaveANextOccurrence() =>
            Assert.Throws<ArgumentException>(() => Planner.CompleteOccurrence(new AppData(), new Goal { Due = Now }, Now),
                "a one-off goal");

        [Test]
        static void TickingAndUntickingADailyTask()
        {
            var data = new AppData();
            var g = Daily("read", Today.AddHours(23).AddMinutes(59));
            g.SubTasks.Add(new SubTask { Title = "chapter", Done = true });
            Planner.TickDaily(data, g, Now);
            Assert.True(g.DoneToday, "done for today");
            Assert.Equal(Today.AddDays(1).AddHours(23).AddMinutes(59), g.Due, "next run tomorrow");
            Assert.False(g.SubTasks[0].Done, "steps cleared for the next run");
            Assert.Equal(1, data.History.Count, "recorded");
            Planner.UntickDaily(data, g, Now);
            Assert.False(g.DoneToday, "open again");
            Assert.Equal(Today.AddHours(23).AddMinutes(59), g.Due, "today's run again");
            Assert.Equal(0, data.History.Count, "record taken back");
        }

        [Test]
        static void PostponeMovesToTomorrowAtTheSameTime()
        {
            var due = Today.AddHours(10);
            var g = new Goal { Due = due, NotifiedBefore = true, NotifiedDue = true };
            var undo = Planner.Postpone(g, Now);
            Assert.Equal(due.AddDays(1), g.Due, "tomorrow, same time");
            Assert.False(g.NotifiedBefore || g.NotifiedDue, "reminders armed again");
            undo();
            Assert.Equal(due, g.Due, "undo");
            Assert.True(g.NotifiedBefore && g.NotifiedDue, "reminder state back");
        }

        // ================= daily tasks =================

        [Test]
        static void UnfinishedDailyTasksMoveOnToTheirNextRun()
        {
            var missed = Daily("stretch", Today.AddDays(-2).AddHours(7), anyTime: false);
            missed.SubTasks.Add(new SubTask { Title = "neck", Done = true });
            var doneToday = Daily("walk", Today.AddDays(1).AddHours(23).AddMinutes(59), doneAt: Now);
            Assert.True(Planner.RollDailies(new[] { missed, doneToday }, Now), "something moved");
            Assert.Equal(Today.AddHours(7), missed.Due, "today at its time");
            Assert.False(missed.SubTasks[0].Done, "steps cleared");
            Assert.Equal(missed.Due <= Now, missed.NotifiedDue, "no reminder for a time already gone");
            Assert.Equal(Today.AddDays(1).AddHours(23).AddMinutes(59), doneToday.Due, "done one untouched");
            Assert.False(Planner.RollDailies(new[] { missed, doneToday }, Now), "nothing left to move");
        }

        [Test]
        static void StreakCountsScheduledDaysOnly()
        {
            // Wednesday 13 January 2027, a task that runs Monday to Friday.
            var now = new DateTime(2027, 1, 13, 12, 0, 0);
            var g = new Goal { Recurrence = Recurrence.Daily, Days = 0b0111110, CreatedAt = new DateTime(2026, 12, 1) };
            var days = new HashSet<DateTime> { new(2027, 1, 7), new(2027, 1, 8), new(2027, 1, 11), new(2027, 1, 12) };
            Assert.Equal(4, Planner.StreakOf(g, days, now), "Thu, Fri, (weekend skipped), Mon, Tue; today still open");
            days.Add(new DateTime(2027, 1, 13));
            Assert.Equal(5, Planner.StreakOf(g, days, now), "today done too");
        }

        [Test]
        static void StreakStopsAtAMissedDay()
        {
            var now = new DateTime(2027, 1, 13, 12, 0, 0);
            var g = new Goal { Recurrence = Recurrence.Daily, CreatedAt = new DateTime(2026, 12, 1) };
            var days = new HashSet<DateTime> { new(2027, 1, 12), new(2027, 1, 10) };
            Assert.Equal(1, Planner.StreakOf(g, days, now), "the 11th was missed");
        }

        // ================= reminders =================

        [Test]
        static void ReminderComesBeforeAndAtTheDeadlineOnce()
        {
            var g = new Goal { Title = "call", Due = Now.AddMinutes(30), RemindMinutes = 60 };
            var first = Planner.TakeReminders(new[] { g }, Now);
            Assert.True(first.Count == 1 && !first[0].Due, "the warning before");
            Assert.Equal(0, Planner.TakeReminders(new[] { g }, Now).Count, "only once");
            var later = g.Due.Value.AddMinutes(1);
            var second = Planner.TakeReminders(new[] { g }, later);
            Assert.True(second.Count == 1 && second[0].Due, "the deadline itself");
            Assert.Equal(0, Planner.TakeReminders(new[] { g }, later).Count, "only once");
        }

        [Test]
        static void NoWarningBeforeWhenItIsOff()
        {
            var g = new Goal { Due = Now.AddMinutes(5), RemindMinutes = 0 };
            Assert.Equal(0, Planner.TakeReminders(new[] { g }, Now).Count, "nothing before");
            Assert.Equal(1, Planner.TakeReminders(new[] { g }, g.Due.Value).Count, "but at the deadline");
        }

        [Test]
        static void NoRemindersForFinishedGoalsOrAnyTimeDailyTasks()
        {
            var goals = new[]
            {
                new Goal { Due = Now.AddHours(-1), Done = true },
                Daily("any time", Now.AddHours(-1)),
                Daily("done today", Now.AddHours(-1), anyTime: false, doneAt: Now),
            };
            Assert.Equal(0, Planner.TakeReminders(goals, Now).Count, "nothing to remind");
        }

        // ================= editing =================

        [Test]
        static void TidyGivesAMonthlyGoalTheDayThatWasPicked()
        {
            var picked = new Goal { Recurrence = Recurrence.Monthly, Due = new DateTime(2027, 1, 31, 9, 0, 0) };
            Planner.Tidy(picked, null, Now);
            Assert.Equal(31, picked.RepeatDay, "new goal");

            var stored = new Goal { Recurrence = Recurrence.Monthly, Due = new DateTime(2027, 2, 28, 9, 0, 0), RepeatDay = 31 };
            var renamed = Store.Clone(stored);
            renamed.Title = "renamed";
            Planner.Tidy(renamed, stored, Now);
            Assert.Equal(31, renamed.RepeatDay, "same date kept: still the 31st");

            var moved = Store.Clone(stored);
            moved.Due = new DateTime(2027, 3, 15, 9, 0, 0);
            Planner.Tidy(moved, stored, Now);
            Assert.Equal(15, moved.RepeatDay, "a new date picked");

            var weekly = Store.Clone(stored);
            weekly.Recurrence = Recurrence.Weekly;
            Planner.Tidy(weekly, stored, Now);
            Assert.Equal(0, weekly.RepeatDay, "not monthly any more");
        }

        [Test]
        static void TidyKeepsDailyTasksOpenAndClearsAnyTimeOnOthers()
        {
            var daily = new Goal { Recurrence = Recurrence.Daily, AnyTime = true, Done = true, CompletedAt = Now };
            Planner.Tidy(daily, null, Now);
            Assert.True(!daily.Done && daily.CompletedAt == null && daily.Due.HasValue, "never finished for good, and scheduled");
            var oneOff = new Goal { AnyTime = true, Due = Now.AddDays(1) };
            Planner.Tidy(oneOff, null, Now);
            Assert.False(oneOff.AnyTime, "any-time is for daily tasks");
        }

        [Test]
        static void TidyDecidesWhichRemindersMayStillFire()
        {
            var stored = new Goal { Due = Now.AddHours(3), NotifiedBefore = true };
            var same = Store.Clone(stored);
            Planner.Tidy(same, stored, Now);
            Assert.True(same.NotifiedBefore, "unchanged deadline: a reminder already shown stays shown");

            var later = Store.Clone(stored);
            later.Due = Now.AddDays(2);
            Planner.Tidy(later, stored, Now);
            Assert.False(later.NotifiedBefore || later.NotifiedDue, "new deadline: reminders armed");

            var past = Store.Clone(stored);
            past.Due = Now.AddDays(-2);
            Planner.Tidy(past, stored, Now);
            Assert.True(past.NotifiedBefore && past.NotifiedDue, "a deadline set in the past never fires");
        }

        [Test]
        static void TidyReplacesACategoryThatIsGone()
        {
            var g = new Goal { Category = "gone" };
            Planner.Tidy(g, null, Now);
            Assert.Equal(Categories.Default, g.Category);
        }

        // ================= statistics =================

        [Test]
        static void OnTimeShareLeavesOutGoalsThatCouldNotBeLate()
        {
            var history = new List<CompletionRecord>
            {
                new() { At = Now, OnTime = false, HadDeadline = true },
                new() { At = Now, OnTime = true, HadDeadline = true },
                new() { At = Now, OnTime = true, HadDeadline = true },
                new() { At = Now, OnTime = true, HadDeadline = false },
            };
            Assert.Equal<int?>(67, Planner.Stats(history, Now).OnTimePercent, "2 of 3");
            Assert.Equal<int?>(null, Planner.Stats(new List<CompletionRecord> { history[3] }, Now).OnTimePercent, "nothing that could be late");
        }

        [Test]
        static void StatsCountTheLastSevenDaysAndTheStreak()
        {
            var history = new[] { 0, 1, 2, 4, 10 }.Select(d => new CompletionRecord { At = Now.AddDays(-d), OnTime = true }).ToList();
            var s = Planner.Stats(history, Now);
            Assert.Equal(5, s.Total, "total");
            Assert.Equal(4, s.LastSevenDays, "last seven days");
            Assert.Equal(3, s.DayStreak, "today and the two days before");
            Assert.Equal(7, s.Week.Count, "seven bars");
            Assert.Equal(Today, s.Week[6].Day, "ending today");
            Assert.Equal("0,0,1,0,1,1,1", string.Join(",", s.Week.Select(x => x.Count)), "per day, oldest first");
        }

        [Test]
        static void CompletionsOfADeletedCategoryCountAsOther()
        {
            var history = new[] { "work", "gone", Categories.Fallback }.Select(c => new CompletionRecord { Category = c }).ToList();
            Assert.Equal(1, Planner.CompletionsIn(history, "work"), "work");
            Assert.Equal(2, Planner.CompletionsIn(history, Categories.Fallback), "other, plus the deleted one");
        }

        // ================= older data =================

        [Test]
        static void PrepareUpgradesDataFromOlderVersions()
        {
            var data = new AppData();
            var anyTime = Daily("habit", Today.AddHours(23).AddMinutes(59));
            var noDeadline = new Goal { Title = "idea" };
            var timed = new Goal { Title = "bills", Due = Now.AddDays(1), Category = "zzz" };
            data.Goals.AddRange(new[] { anyTime, noDeadline, timed });
            foreach (var id in new[] { anyTime.Id, noDeadline.Id, timed.Id, "deleted" })
                data.History.Add(new CompletionRecord { GoalId = id, OnTime = true });
            Planner.Prepare(data);
            Assert.Equal(6, data.Settings.Categories.Count, "the built-in categories");
            Assert.Equal(Categories.Fallback, timed.Category, "unknown category → other");
            Assert.Equal("False,False,True,True", string.Join(",", data.History.Select(r => r.HadDeadline)),
                "any-time daily, no deadline, timed, deleted goal");
        }

        // ================= exporting and importing some goals =================

        static AppData Prepared()
        {
            var data = new AppData();
            Planner.Prepare(data); // the built-in categories
            return data;
        }

        static string Ids(IEnumerable<CategoryDef> categories) => string.Join(",", categories.Select(c => c.Id));

        [Test]
        static void AFileOfOneGoalHoldsItsHistoryAndCategory()
        {
            var data = Prepared();
            data.Settings.Categories.Insert(0, new CategoryDef { Id = "cpts", Name = "CPTS", Color = "#14B8A6" });
            Planner.Prepare(data);
            var module = new Goal { Title = "Footprinting", Category = "cpts" };
            module.SubTasks.Add(new SubTask { Title = "Introduction", IsHeading = true });
            var other = new Goal { Title = "bills", Category = "finance" };
            data.Goals.AddRange(new[] { module, other });
            data.History.Add(new CompletionRecord { GoalId = module.Id, At = Now });
            data.History.Add(new CompletionRecord { GoalId = other.Id, At = Now });

            var part = Planner.Part(data, new[] { module });
            Assert.Equal("Footprinting", Titles(part.Goals), "just that goal");
            Assert.False(ReferenceEquals(module, part.Goals[0]), "a copy, not the goal itself");
            Assert.True(part.Goals[0].SubTasks[0].IsHeading, "with its steps as they are");
            Assert.Equal(module.Id, part.History.Single().GoalId, "its history only");
            Assert.Equal("cpts", Ids(part.Settings.Categories), "the category it uses");
            Categories.Use(null);
        }

        [Test]
        static void ImportAddsNewGoalsAndLeavesTheRestAlone()
        {
            var data = Prepared();
            var mine = new Goal { Title = "mine", Category = "work", Due = Now.AddDays(1) };
            data.Goals.Add(mine);
            var file = new AppData();
            var same = Store.Clone(mine);
            same.Title = "changed elsewhere";
            var late = new Goal { Title = "late", Category = "cpts", Due = Now.AddDays(-1) };
            var soon = new Goal { Title = "soon", Category = "cpts", Due = Now.AddDays(2), NotifiedBefore = true, NotifiedDue = true };
            file.Goals.AddRange(new[] { same, late, soon });
            file.History.Add(new CompletionRecord { GoalId = soon.Id, At = Now.AddDays(-3) });
            file.Settings.Categories = new() { new CategoryDef { Id = "cpts", Name = "CPTS", Color = "#14B8A6" } };

            var added = Planner.AddGoals(data, file, file.Goals, Now);
            Assert.Equal("late, soon", Titles(added.Goals), "only the goals that weren't here");
            Assert.Equal("mine, late, soon", Titles(data.Goals), "the one already here left as it is");
            Assert.Equal("work,study,personal,health,finance,cpts,other", Ids(data.Settings.Categories), "their category, before the fallback");
            Assert.True(Categories.Exists("cpts"), "and in use");
            Assert.Equal(1, data.History.Count, "with their history");
            Assert.True(late.NotifiedBefore && late.NotifiedDue, "no reminder for a deadline already gone");
            Assert.False(soon.NotifiedBefore || soon.NotifiedDue, "reminders still to come");

            Planner.RemoveAdded(data, added);
            Assert.Equal("mine", Titles(data.Goals), "undo takes the goals out");
            Assert.Equal(0, data.History.Count, "and their history");
            Assert.False(Categories.Exists("cpts"), "and their category");
            Categories.Use(null);
        }

        [Test]
        static void ImportedGoalShowsAsOtherWhenThereIsNoRoomForItsCategory()
        {
            var data = Prepared();
            for (int i = data.Settings.Categories.Count; i < Categories.Max; i++)
                data.Settings.Categories.Insert(0, new CategoryDef { Id = "c" + i, Color = "#3B82F6" });
            Planner.Prepare(data);
            var file = new AppData();
            file.Goals.Add(new Goal { Title = "x", Category = "cpts" });
            file.Settings.Categories = new() { new CategoryDef { Id = "cpts", Color = "#14B8A6" } };

            var added = Planner.AddGoals(data, file, file.Goals, Now);
            Assert.Equal(Categories.Max, data.Settings.Categories.Count, "no room for another category");
            Assert.Equal(Categories.Fallback, added.Goals.Single().Category, "so it shows as the fallback");
            Categories.Use(null);
        }
    }
}
