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

        /// <summary>Colours offered for categories and the accent; each reads well on dark and light.</summary>
        public static readonly (string Key, uint Argb)[] Swatches =
        {
            ("blue", 0xFF3B82F6), ("violet", 0xFF8B5CF6), ("pink", 0xFFEC4899), ("green", 0xFF10B981), ("amber", 0xFFF59E0B),
            ("slate", 0xFF64748B), ("red", 0xFFEF4444), ("teal", 0xFF14B8A6), ("orange", 0xFFF97316), ("indigo", 0xFF6366F1),
        };

        public static string Hex(uint argb) => $"#{argb & 0xFFFFFF:X6}";

        /// <summary>Reads #RRGGBB.</summary>
        public static bool TryParse(string hex, out uint argb)
        {
            argb = 0;
            if (hex is not { Length: 7 } || hex[0] != '#'
                || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return false;
            argb = 0xFF000000 | rgb;
            return true;
        }
    }

    /// <summary>A category of goals. The six built-in ones can be renamed and recoloured, and the user can add more.</summary>
    public class CategoryDef
    {
        public string Id { get; set; }
        /// <summary>The user's own name. Empty on a built-in category: it keeps its name in the current language.</summary>
        public string Name { get; set; }
        /// <summary>#RRGGBB</summary>
        public string Color { get; set; }
    }

    public static class Categories
    {
        /// <summary>Where the goals of a deleted category go; it can be renamed, not deleted.</summary>
        public const string Fallback = "other";
        public const int Max = 12;
        static readonly string[] BuiltIn = { "work", "study", "personal", "health", "finance", "other" };
        static readonly uint[] BuiltInColors = { 0xFF3B82F6, 0xFF8B5CF6, 0xFFEC4899, 0xFF10B981, 0xFFF59E0B, 0xFF64748B };

        static List<CategoryDef> list = Defaults();

        /// <summary>The categories in menu order.</summary>
        public static IReadOnlyList<CategoryDef> All => list;

        static List<CategoryDef> Defaults() =>
            BuiltIn.Select((id, i) => new CategoryDef { Id = id, Color = Palette.Hex(BuiltInColors[i]) }).ToList();

        /// <summary>
        /// Puts the settings' list in use, first repairing what an older or hand-edited file may lack
        /// (no list at all, repeated ids, a bad colour, no fallback category).
        /// </summary>
        public static List<CategoryDef> Use(List<CategoryDef> defs)
        {
            defs = (defs ?? new()).Where(c => !string.IsNullOrWhiteSpace(c?.Id)).GroupBy(c => c.Id).Select(g => g.First()).ToList();
            if (defs.Count == 0) defs = Defaults();
            if (!defs.Any(c => c.Id == Fallback)) defs.Add(new CategoryDef { Id = Fallback, Color = Palette.Hex(0xFF64748B) });
            foreach (var c in defs.Where(c => !Palette.TryParse(c.Color, out _))) c.Color = Palette.Hex(0xFF64748B);
            list = defs;
            return defs;
        }

        public static bool IsBuiltIn(string id) => BuiltIn.Contains(id);
        public static bool Exists(string id) => list.Any(c => c.Id == id);
        static CategoryDef Find(string id) => list.FirstOrDefault(c => c.Id == id) ?? list.FirstOrDefault(c => c.Id == Fallback);

        /// <summary>For new goals: "personal" while it exists.</summary>
        public static string Default => Exists("personal") ? "personal" : Fallback;

        // A goal whose category is gone (deleted, or missing from an imported file) shows as the fallback.
        public static SolidColorBrush BrushOf(string id) =>
            Palette.B(Palette.TryParse(Find(id)?.Color, out var c) ? c : 0xFF64748B);
        public static string NameOf(string id) => NameOf(Find(id));
        public static string NameOf(CategoryDef c) =>
            !string.IsNullOrWhiteSpace(c?.Name) ? c.Name : DefaultName(c?.Id);
        public static string DefaultName(string id) => Loc.I["cat_" + (IsBuiltIn(id) ? id : Fallback)];
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
        [JsonIgnore] public string CategoryName => Categories.NameOf(Category);
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

        /// <summary>Day of the month a monthly goal belongs to (0 = the day of its current deadline).</summary>
        public int RepeatDay { get; set; }

        /// <summary>
        /// The occurrence after <paramref name="d"/>. Monthly goals keep their day of the month: a goal on the
        /// 31st falls on the last day of shorter months and returns to the 31st afterwards, instead of drifting.
        /// </summary>
        public DateTime NextAfter(DateTime d)
        {
            switch (Recurrence)
            {
                case Recurrence.Daily: return d.AddDays(1);
                case Recurrence.Weekly: return d.AddDays(7);
                case Recurrence.Monthly:
                    var month = d.AddMonths(1);
                    int day = Math.Min(RepeatDay > 0 ? RepeatDay : d.Day, DateTime.DaysInMonth(month.Year, month.Month));
                    return new DateTime(month.Year, month.Month, day) + d.TimeOfDay;
                default: return d;
            }
        }

        // ---- card actions (right-click menu) ----
        /// <summary>
        /// "Postpone to tomorrow" fits a one-off goal that is due today or already late. Later goals would be pulled
        /// earlier, and moving a repeating one would shift all its future occurrences.
        /// </summary>
        [JsonIgnore] public bool CanSnooze => !IsChecked && !IsRecurring && Due.HasValue && Due.Value.Date <= DateTime.Today;
        [JsonIgnore] public string DoneActionText => Loc.I[IsChecked ? "ctx_undone" : "ctx_done"];
    }

    public class CompletionRecord
    {
        public string GoalId { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public DateTime At { get; set; }
        public bool OnTime { get; set; }
        /// <summary>
        /// The goal had a real deadline when it was ticked off. Goals without one and any-time daily tasks can't
        /// be late, so they stay out of the on-time share. Null on records made before this was kept.
        /// </summary>
        public bool? HadDeadline { get; set; }
    }

    public class Settings
    {
        public string Lang { get; set; } = "ar";
        public string Theme { get; set; } = "system";
        public string Effect { get; set; } = "acrylic";
        /// <summary>"system" (the Windows accent colour) or #RRGGBB.</summary>
        public string Accent { get; set; } = "system";
        /// <summary>The user's categories in menu order (absent from files made before they could be edited).</summary>
        public List<CategoryDef> Categories { get; set; }
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

    /// <summary>Reads a typed time: 21:30, 21.30, 2130, 21, 9:30 م / ص, 9:30 pm, ٩:٣٠ م.</summary>
    public static class TimeInput
    {
        public static TimeSpan? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = new System.Text.StringBuilder();
            foreach (char c in text.Trim().ToLowerInvariant())
                s.Append(c >= '٠' && c <= '٩' ? (char)('0' + (c - '٠')) : c); // Arabic-Indic digits
            var t = s.ToString();
            bool pm = t.Contains("pm") || t.Contains('م');
            bool am = t.Contains("am") || t.Contains('ص');
            var digits = new string(t.Where(c => char.IsDigit(c) || c == ':' || c == '.').ToArray()).Replace('.', ':');
            if (digits.Length == 0) return null;

            int h, m = 0;
            var parts = digits.Split(':');
            if (parts.Length == 2 && parts[0].Length is >= 1 and <= 2 && parts[1].Length == 2)
            {
                h = int.Parse(parts[0]);
                m = int.Parse(parts[1]);
            }
            else if (parts.Length == 1 && digits.Length is 1 or 2) h = int.Parse(digits);
            else if (parts.Length == 1 && digits.Length is 3 or 4)
            {
                h = int.Parse(digits[..^2]);
                m = int.Parse(digits[^2..]);
            }
            else return null;

            if (pm || am)
            {
                if (h < 1 || h > 12) return null;
                if (pm && h < 12) h += 12;
                if (am && h == 12) h = 0;
            }
            if (h > 23 || m > 59) return null;
            return new TimeSpan(h, m, 0);
        }
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
            ?? Folder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        /// <summary>
        /// The data folder under <paramref name="appData"/>. The app used to be called Hadaf: the first time, its folder is
        /// renamed to the new name, backups included. If that can't happen (a file in it is open elsewhere, say), the
        /// old folder stays in use, so no data is ever left behind; the next start tries again.
        /// </summary>
        public static string Folder(string appData)
        {
            var dir = Path.Combine(appData, "Quest");
            var old = Path.Combine(appData, "Hadaf");
            if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
            try
            {
                Directory.Move(old, dir);
                return dir;
            }
            catch
            {
                return old;
            }
        }
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
            Normalize(data);
            return true;
        }

        static void Normalize(AppData data)
        {
            data.Goals ??= new();
            data.History ??= new();
            data.Settings ??= new();
            data.Goals.RemoveAll(g => g == null);
            data.History.RemoveAll(h => h == null);
        }

        /// <summary>Writes a readable copy of the data to a file the user picked (Settings → Export).</summary>
        public static void Export(AppData data, string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions(Options) { WriteIndented = true }));

        /// <summary>Reads a file picked for import; null unless it is one of the app's data files.</summary>
        public static AppData ReadForImport(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(text))
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Goals", out _))
                        return null;
                var data = JsonSerializer.Deserialize<AppData>(text, Options);
                if (data == null) return null;
                Normalize(data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        public static T Clone<T>(T obj) =>
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj, Options), Options);
    }
}
