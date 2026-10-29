using System;

namespace Hadaf.Tests
{
    static class GoalTests
    {
        // 1 January 2027 is a Friday.
        static readonly DateTime Saturday = new(2027, 1, 2);
        const int Weekdays = 0b0111110; // Monday to Friday (bit n = DayOfWeek n)

        [Test]
        static void MonthlyGoalKeepsItsDayThroughShortMonths()
        {
            var g = new Goal { Recurrence = Recurrence.Monthly, RepeatDay = 31 };
            var d = new DateTime(2027, 1, 31, 9, 0, 0);
            d = g.NextAfter(d);
            Assert.Equal(new DateTime(2027, 2, 28, 9, 0, 0), d, "February");
            d = g.NextAfter(d);
            Assert.Equal(new DateTime(2027, 3, 31, 9, 0, 0), d, "March, back on the 31st");
            d = g.NextAfter(d);
            Assert.Equal(new DateTime(2027, 4, 30, 9, 0, 0), d, "April");
        }

        [Test]
        static void MonthlyGoalLandsOnLeapDay()
        {
            var g = new Goal { Recurrence = Recurrence.Monthly, RepeatDay = 30 };
            Assert.Equal(new DateTime(2028, 2, 29), g.NextAfter(new DateTime(2028, 1, 30)), "leap-year February");
            Assert.Equal(new DateTime(2028, 3, 30), g.NextAfter(new DateTime(2028, 2, 29)), "then the 30th again");
        }

        [Test]
        static void MonthlyGoalWithoutItsDayFollowsTheDeadline()
        {
            var g = new Goal { Recurrence = Recurrence.Monthly };
            Assert.Equal(new DateTime(2027, 2, 15, 18, 0, 0), g.NextAfter(new DateTime(2027, 1, 15, 18, 0, 0)), "same day next month");
        }

        [Test]
        static void WeeklyAndDailyStepWholeWeeksAndDays()
        {
            var start = new DateTime(2027, 1, 4, 7, 30, 0);
            Assert.Equal(start.AddDays(7), new Goal { Recurrence = Recurrence.Weekly }.NextAfter(start), "weekly");
            Assert.Equal(start.AddDays(1), new Goal { Recurrence = Recurrence.Daily }.NextAfter(start), "daily");
        }

        [Test]
        static void DailyTaskSkipsTheDaysItDoesNotRunOn()
        {
            var g = new Goal { Recurrence = Recurrence.Daily, Days = Weekdays, AnyTime = true };
            Assert.Equal(new DateTime(2027, 1, 4, 23, 59, 0), g.NextRun(Saturday, false), "from Saturday to Monday, end of day");
        }

        [Test]
        static void DailyTaskKeepsItsTime()
        {
            var g = new Goal { Recurrence = Recurrence.Daily, Due = new DateTime(2026, 12, 1, 7, 30, 0) };
            Assert.Equal(new DateTime(2027, 1, 2, 7, 30, 0), g.NextRun(Saturday, false), "same time on the next run");
        }

        [Test]
        static void NextRunSkipsTodayOnceItsTimeHasPassed()
        {
            var today = DateTime.Today;
            var g = new Goal { Recurrence = Recurrence.Daily, Due = today }; // midnight: always passed
            Assert.Equal(today.AddDays(1), g.NextRun(today, true), "tomorrow");
            Assert.Equal(today, g.NextRun(today, false), "unless asked to keep today");
        }

        [Test]
        static void PostponeIsOfferedOnlyForOneOffGoalsDueTodayOrLate()
        {
            var today = DateTime.Today;
            Assert.True(new Goal { Due = today.AddDays(-1).AddHours(9) }.CanSnooze, "late");
            Assert.True(new Goal { Due = today.AddHours(23).AddMinutes(59) }.CanSnooze, "due today");
            Assert.False(new Goal { Due = today.AddDays(1).AddHours(9) }.CanSnooze, "due tomorrow would move earlier");
            Assert.False(new Goal { Due = today.AddHours(9), Recurrence = Recurrence.Weekly }.CanSnooze, "repeating");
            Assert.False(new Goal { Due = today.AddHours(9), Recurrence = Recurrence.Daily }.CanSnooze, "daily task");
            Assert.False(new Goal { Due = today.AddHours(9), Done = true }.CanSnooze, "finished");
            Assert.False(new Goal().CanSnooze, "no deadline");
        }

        [Test]
        static void PartHeadingsAreNotCountedAsSteps()
        {
            var g = new Goal();
            g.SubTasks.Add(new SubTask { Title = "Introduction", IsHeading = true, Done = true }); // a hand-edited file
            g.SubTasks.Add(new SubTask { Title = "Enumeration Principles", Done = true });
            g.SubTasks.Add(new SubTask { Title = "Enumeration Methodology" });
            g.SubTasks.Add(new SubTask { Title = "Host Based Enumeration", IsHeading = true });
            g.SubTasks.Add(new SubTask { Title = "FTP" });
            g.SubTasks.Add(new SubTask { Title = "SMB" });
            Assert.Equal(4, g.StepCount, "steps");
            Assert.Equal(1, g.DoneSteps, "done");
            Assert.Equal("1/4", g.ProgressText, "progress text");
            Assert.Equal(25.0, g.Progress, "progress");

            var headingsOnly = new Goal();
            headingsOnly.SubTasks.Add(new SubTask { Title = "Part", IsHeading = true });
            Assert.False(headingsOnly.HasSubTasks, "headings alone leave nothing to tick");
            Assert.Equal(0.0, headingsOnly.Progress, "and no progress");
        }
    }

    static class TimeInputTests
    {
        static void Reads(string text, int hours, int minutes) =>
            Assert.Equal<TimeSpan?>(new TimeSpan(hours, minutes, 0), TimeInput.Parse(text), $"'{text}'");

        [Test]
        static void ReadsTwentyFourHourTimes()
        {
            Reads("21:30", 21, 30);
            Reads("21.30", 21, 30);
            Reads("2130", 21, 30);
            Reads("930", 9, 30);
            Reads("21", 21, 0);
            Reads(" 7 ", 7, 0);
        }

        [Test]
        static void ReadsTwelveHourTimesInArabicAndEnglish()
        {
            Reads("9:30 م", 21, 30);
            Reads("9:30 ص", 9, 30);
            Reads("12:15 ص", 0, 15);
            Reads("12 pm", 12, 0);
            Reads("12 am", 0, 0);
            Reads("9:30 PM", 21, 30);
            Reads("٩:٣٠ م", 21, 30);
        }

        [Test]
        static void RejectsWhatIsNotATime()
        {
            foreach (var text in new[] { null, "", "abc", "25:00", "9:75", "13 pm", "0 am", "12345", "1:2" })
                Assert.Equal<TimeSpan?>(null, TimeInput.Parse(text), $"'{text}'");
        }
    }
}
