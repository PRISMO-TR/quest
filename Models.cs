using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Hadaf
{
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
        /// <summary>Re-reads every binding (computed text, colours, countdown).</summary>
        public void RefreshAll() => Raise(string.Empty);
    }

    public enum Priority { Low, Medium, High }
    public enum Recurrence { None, Daily, Weekly, Monthly }

    public static class Palette
    {
        static readonly Dictionary<uint, SolidColorBrush> Cache = new();
        public static SolidColorBrush B(uint argb)
        {
            if (!Cache.TryGetValue(argb, out var b))
            {
                b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                b.Freeze();
                Cache[argb] = b;
            }
            return b;
        }
    }

    public static class Categories
    {
        public static readonly string[] Ids = { "work", "study", "personal", "health", "finance", "other" };
        static readonly Dictionary<string, uint> Colors = new()
        {
            ["work"] = 0xFF3B82F6,
            ["study"] = 0xFF8B5CF6,
            ["personal"] = 0xFFEC4899,
            ["health"] = 0xFF10B981,
            ["finance"] = 0xFFF59E0B,
            ["other"] = 0xFF64748B,
        };
        public static SolidColorBrush BrushOf(string id) =>
            Palette.B(id != null && Colors.TryGetValue(id, out var c) ? c : 0xFF64748B);
    }

    public class SubTask : Notify
    {
        string _title = "";
        bool _done;
        public string Title { get => _title; set => Set(ref _title, value); }
        public bool Done { get => _done; set => Set(ref _done, value); }
    }

    public class Goal : Notify
    {
        /// <summary>Every weekday (bit n = DayOfWeek n).</summary>
        public const int AllDays = 0x7F;
        public static readonly TimeSpan EndOfDay = new(23, 59, 0);

        string _title = "", _notes = "", _category = "personal";
        Priority _priority = Priority.Medium;
        DateTime? _due, _lastDone;
        Recurrence _recurrence;
        int _remind = 60, _days = AllDays, _streak;
        bool _done, _expanded, _anyTime;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get => _title; set => Set(ref _title, value); }
        public string Notes { get => _notes; set => Set(ref _notes, value); }
        public string Category { get => _category; set => Set(ref _category, value); }
        public Priority Priority { get => _priority; set => Set(ref _priority, value); }
        public DateTime? Due { get => _due; set => Set(ref _due, value); }
        public Recurrence Recurrence { get => _recurrence; set => Set(ref _recurrence, value); }
        public int RemindMinutes { get => _remind; set => Set(ref _remind, value); }
        public bool Done { get => _done; set => Set(ref _done, value); }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public ObservableCollection<SubTask> SubTasks { get; set; } = new();
        public bool NotifiedBefore { get; set; }
        public bool NotifiedDue { get; set; }

        // ---- daily tasks ----
        /// <summary>Weekdays a daily task runs on (bit n = DayOfWeek n).</summary>
        public int Days { get => _days; set => Set(ref _days, value); }
        /// <summary>A daily task with no set time: due by the end of the day.</summary>
        public bool AnyTime { get => _anyTime; set => Set(ref _anyTime, value); }
        /// <summary>When a daily task was last ticked off; it shows as done until midnight.</summary>
        public DateTime? LastDoneAt { get => _lastDone; set => Set(ref _lastDone, value); }

        [JsonIgnore] public bool IsDaily => Recurrence == Recurrence.Daily;
        [JsonIgnore] public bool DoneToday => IsDaily && LastDoneAt?.Date == DateTime.Today;
        /// <summary>What the check circle shows: finished for good, or finished for today.</summary>
        [JsonIgnore] public bool IsChecked => Done || DoneToday;
        [JsonIgnore] public int Streak { get => _streak; set => Set(ref _streak, value); }
        [JsonIgnore] public bool HasStreak => IsDaily && Streak >= 2;
        [JsonIgnore] public string StreakText => Loc.I.F("streak_fmt", Loc.I.DaysPhrase(Streak));

        public bool RunsOn(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

        /// <summary>
        /// Next run of a daily task on or after <paramref name="fromDay"/>, at its time (end of day
        /// for any-time tasks). <paramref name="skipIfPassed"/> skips a run whose time is already over.
        /// </summary>
        public DateTime NextRun(DateTime fromDay, bool skipIfPassed)
        {
            var time = AnyTime ? EndOfDay : Due?.TimeOfDay ?? new TimeSpan(9, 0, 0);
            var day = fromDay.Date;
            for (int i = 0; i < 14; i++, day = day.AddDays(1))
                if (RunsOn(day.DayOfWeek) && (!skipIfPassed || day + time > DateTime.Now))
                    return day + time;
            return fromDay.Date + time;
        }

        // ---- view-only ----
        [JsonIgnore] public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
        [JsonIgnore] public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
        [JsonIgnore] public bool HasDue => Due.HasValue;
        [JsonIgnore] public bool IsOverdue => !IsChecked && Due.HasValue && Due.Value < DateTime.Now;
        [JsonIgnore] public bool IsRecurring => Recurrence != Recurrence.None;
        [JsonIgnore] public bool IsHigh => Priority == Priority.High;
        [JsonIgnore] public Brush PriorityBrush => Palette.B(Priority switch
        {
            Priority.High => 0xFFEF4444,
            Priority.Medium => 0xFFF59E0B,
            _ => 0xFF94A3B8,
        });
        [JsonIgnore] public string PriorityName => Loc.I["p_" + Priority.ToString().ToLowerInvariant()];
        [JsonIgnore] public Brush CategoryBrush => Categories.BrushOf(Category);
        [JsonIgnore] public string CategoryName => Loc.I["cat_" + Category];
        [JsonIgnore] public string DueText => Due.HasValue ? Loc.I.FormatDate(Due.Value) : "";
        [JsonIgnore] public string RecurrenceName => Loc.I["r_" + Recurrence.ToString().ToLowerInvariant()];
        /// <summary>"Daily", or the chosen weekdays when a daily task skips some days.</summary>
        [JsonIgnore] public string RepeatText => IsDaily && Days != AllDays ? Loc.I.DaysText(Days, true) : RecurrenceName;
        [JsonIgnore] public bool HasSubTasks => SubTasks.Count > 0;
        [JsonIgnore] public int DoneSteps => SubTasks.Count(s => s.Done);
        [JsonIgnore] public double Progress => HasSubTasks ? 100.0 * DoneSteps / SubTasks.Count : 0;
        [JsonIgnore] public string ProgressText => $"{DoneSteps}/{SubTasks.Count}";

        // Card meta: when it is due (or when it was done).
        [JsonIgnore] public bool HasWhen => Done ? CompletedAt.HasValue : DoneToday || Due.HasValue;
        [JsonIgnore] public string WhenIcon => IsChecked ? "" : "";
        [JsonIgnore]
        public string WhenText
        {
            get
            {
                var L = Loc.I;
                if (Done) return CompletedAt.HasValue ? L.F("done_fmt", L.FormatDate(CompletedAt.Value)) : "";
                if (DoneToday) return L.F("done_fmt", L.FormatDate(LastDoneAt.Value));
                if (!Due.HasValue) return "";
                return IsDaily && AnyTime ? L.FormatDay(Due.Value) : DueText;
            }
        }

        // Countdown pill at the end of the title line (none for any-time daily tasks).
        [JsonIgnore] public bool HasPill => !IsChecked && Due.HasValue && !(IsDaily && AnyTime);

        [JsonIgnore]
        public string PillText
        {
            get
            {
                if (!HasPill) return "";
                var L = Loc.I;
                var diff = Due.Value - DateTime.Now;
                if (Math.Abs(diff.TotalMinutes) < 1) return L["now_short"];
                return diff > TimeSpan.Zero ? L.ShortDur(diff) : L.F("late_short", L.ShortDur(diff));
            }
        }

        /// <summary>0 done, 1 overdue, 2 due within 24h, 3 normal</summary>
        int Urgency => IsChecked ? 0 : !Due.HasValue ? 3 : Due.Value < DateTime.Now ? 1 : (Due.Value - DateTime.Now).TotalHours < 24 ? 2 : 3;

        uint UrgencyRgb => (Urgency, Theme.IsDark) switch
        {
            (0, true) => 0x34D399, (0, false) => 0x047857,
            (1, true) => 0xF87171, (1, false) => 0xDC2626,
            (2, true) => 0xFBBF24, (2, false) => 0xB45309,
            (_, true) => 0xA5B4FC, (_, false) => 0x4338CA,
        };
        [JsonIgnore] public Brush UrgencyBrush => Palette.B(0xFF000000 | UrgencyRgb);
        [JsonIgnore] public Brush UrgencySoft => Palette.B(0x2E000000 | UrgencyRgb);

        public static DateTime Advance(DateTime d, Recurrence r) => r switch
        {
            Recurrence.Daily => d.AddDays(1),
            Recurrence.Weekly => d.AddDays(7),
            Recurrence.Monthly => d.AddMonths(1),
            _ => d,
        };
    }

    public class CompletionRecord
    {
        public string GoalId { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public DateTime At { get; set; }
        public bool OnTime { get; set; }
    }

    public class Settings
    {
        public string Lang { get; set; } = "ar";
        public string Theme { get; set; } = "system";
        public string Effect { get; set; } = "acrylic";
        public double Transparency { get; set; } = 0.6;
        public bool Notifications { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool TrayHintShown { get; set; }
        /// <summary>The user has answered the start-with-Windows offer (either way).</summary>
        public bool StartupAsked { get; set; }
        // Last window bounds; 0 width means "never saved".
        public double WinLeft { get; set; }
        public double WinTop { get; set; }
        public double WinWidth { get; set; }
        public double WinHeight { get; set; }
    }

    public class AppData
    {
        public List<Goal> Goals { get; set; } = new();
        public List<CompletionRecord> History { get; set; } = new();
        public Settings Settings { get; set; } = new();
    }

    public enum LoadProblem { RestoredFromBackup, Unreadable }

    /// <summary>The data file could not be used as it was at start-up; the app tells the user what happened.</summary>
    public sealed record LoadIssue(LoadProblem Kind, DateTime? BackupTime);

    /// <summary>
    /// The data file, plus rolling backups next to it. Nothing here ever overwrites the only copy of the
    /// user's data: an unreadable file is set aside under a unique name, and saves go through a temp file.
    /// </summary>
    public static class Store
    {
        public static readonly string Dir = Environment.GetEnvironmentVariable("HADAF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hadaf");
        public static string BackupDir => Path.Combine(Dir, "backups");
        static string FilePath => Path.Combine(Dir, "data.json");
        const int KeepBackups = 20;
        static readonly TimeSpan BackupEvery = TimeSpan.FromHours(1);

        public static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Set by <see cref="Load"/> when the data file could not be used as it was.</summary>
        public static LoadIssue Issue { get; private set; }

        // True once data.json is known to hold good data (read or written by us); only then is it backed up.
        static bool mainIsGood;

        public static AppData Load()
        {
            Issue = null;
            bool hadFile = File.Exists(FilePath);
            if (hadFile)
            {
                if (TryRead(FilePath, out var data))
                {
                    mainIsGood = true;
                    Snapshot(force: false);
                    return data;
                }
                // Keep the unreadable file under a name nothing will ever write to again.
                try { File.Copy(FilePath, Path.Combine(Dir, $"data.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); } catch { }
            }
            // Main file missing or unreadable: fall back to the newest backup that still reads.
            foreach (var backup in Backups())
            {
                if (TryRead(backup.FullName, out var restored))
                {
                    Issue = new LoadIssue(LoadProblem.RestoredFromBackup, BackupTime(backup));
                    return restored;
                }
            }
            if (hadFile) Issue = new LoadIssue(LoadProblem.Unreadable, null);
            return new AppData();
        }

        /// <summary>Writes the data file. Returns null on success, or the error so the app can warn the user.</summary>
        public static Exception Save(AppData data)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, data, Options);
                    fs.Flush(true); // on disk before the swap: a power cut leaves the old file, never half a new one
                }
                if (mainIsGood) Snapshot(force: false);
                File.Move(tmp, FilePath, true);
                mainIsGood = true;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>Takes a backup right now (Settings → Back up now).</summary>
        public static bool BackupNow() => mainIsGood && Snapshot(force: true);

        public static DateTime? LastBackup => Backups().Select(BackupTime).FirstOrDefault();

        /// <summary>Copies data.json into backups/ (at most hourly unless forced) and keeps the newest 20.</summary>
        static bool Snapshot(bool force)
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var last = LastBackup;
                if (!force && last.HasValue && DateTime.Now - last.Value < BackupEvery) return false;
                Directory.CreateDirectory(BackupDir);
                File.Copy(FilePath, Path.Combine(BackupDir, $"data-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                foreach (var old in Backups().Skip(KeepBackups)) old.Delete();
                return true;
            }
            catch
            {
                return false; // a missed backup must never get in the way of using the app
            }
        }

        // Newest first; the timestamp is in the name, because File.Copy keeps the source's write time.
        static IEnumerable<FileInfo> Backups() => Directory.Exists(BackupDir)
            ? new DirectoryInfo(BackupDir).GetFiles("data-*.json").OrderByDescending(f => f.Name)
            : Enumerable.Empty<FileInfo>();

        static DateTime? BackupTime(FileInfo f) =>
            f.Name.Length >= 20 && DateTime.TryParseExact(f.Name.Substring(5, 15), "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : f.CreationTime;

        /// <summary>
        /// Reads a data file, retrying briefly: sync tools and antivirus can hold a file for a moment,
        /// and that must not be mistaken for a damaged file.
        /// </summary>
        static bool TryRead(string path, out AppData data)
        {
            data = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(path), Options);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(250);
                }
                catch
                {
                    break;
                }
            }
            if (data == null) return false;
            data.Goals ??= new();
            data.History ??= new();
            data.Settings ??= new();
            data.Goals.RemoveAll(g => g == null);
            data.History.RemoveAll(h => h == null);
            return true;
        }

        public static T Clone<T>(T obj) =>
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj, Options), Options);
    }
}
