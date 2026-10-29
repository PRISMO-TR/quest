using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Markup;

namespace Quest
{
    /// <summary>Arabic / English string table. Bind with {local:L key}.</summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        public static Loc I { get; } = new Loc();

        static readonly CultureInfo Ar = new CultureInfo("ar-EG");
        static readonly CultureInfo En = new CultureInfo("en-US");

        public string Lang { get; private set; } = "ar";
        public bool IsAr => Lang == "ar";
        public CultureInfo Culture => IsAr ? Ar : En;

        public event PropertyChangedEventHandler PropertyChanged;

        public void Set(string lang)
        {
            Lang = lang == "en" ? "en" : "ar";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAr)));
        }

        public string this[string key] => T.TryGetValue(key, out var v) ? v[IsAr ? 0 : 1] : key;

        public string F(string key, params object[] args) => string.Format(this[key], args);

        public string FormatDate(DateTime d) => $"{FormatDay(d)} · {d.ToString("t", Culture)}";

        /// <summary>Day part only: "اليوم", "غداً", "الأحد 2 نوفمبر".</summary>
        public string FormatDay(DateTime d)
        {
            var today = DateTime.Today;
            return d.Date == today ? this["today"]
                : d.Date == today.AddDays(1) ? this["tomorrow"]
                : d.Date == today.AddDays(-1) ? this["yesterday"]
                : d.ToString(d.Year == today.Year ? "ddd d MMM" : "d MMM yyyy", Culture);
        }

        /// <summary>"يوم واحد" / "يومان" / "3 أيام" / "11 يومًا" — or "1 day" / "3 days".</summary>
        public string DaysPhrase(int n) =>
            IsAr ? ArN(n, "يوم واحد", "يومان", "أيام", "يومًا") : n == 1 ? "1 day" : $"{n} days";

        /// <summary>Weekdays of a daily task, in this language's week order. Compact form collapses long lists.</summary>
        public string DaysText(int mask, bool compact)
        {
            if (mask == Goal.AllDays) return this["every_day"];
            int first = IsAr ? (int)DayOfWeek.Saturday : (int)DayOfWeek.Sunday;
            var days = Enumerable.Range(0, 7).Select(i => (DayOfWeek)((first + i) % 7))
                .Where(d => (mask & (1 << (int)d)) != 0).ToList();
            if (compact && days.Count > 3) return F("days_a_week", days.Count);
            return string.Join(IsAr ? "، " : ", ", days.Select(d => Culture.DateTimeFormat.GetAbbreviatedDayName(d)));
        }

        /// <summary>"كل يوم · 9:00 ص" / "الأحد، الثلاثاء · أي وقت".</summary>
        public string DailySummary(Goal g) =>
            $"{DaysText(g.Days, false)} · {(g.AnyTime || !g.Due.HasValue ? this["any_time"] : g.Due.Value.ToString("t", Culture))}";

        public string LongDate(DateTime d) =>
            d.ToString(IsAr ? "dddd، d MMMM yyyy" : "dddd, MMMM d, yyyy", Culture);

        public string Dur(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = t.Negate();
            int d = (int)t.TotalDays, h = t.Hours, m = t.Minutes;
            if (d == 0 && h == 0 && m == 0) m = 1;
            if (IsAr)
            {
                string D(int n) => ArN(n, "يوم", "يومان", "أيام", "يومًا");
                string H(int n) => ArN(n, "ساعة", "ساعتان", "ساعات", "ساعة");
                string M(int n) => ArN(n, "دقيقة", "دقيقتان", "دقائق", "دقيقة");
                if (d > 0) return h > 0 ? $"{D(d)} و{H(h)}" : D(d);
                if (h > 0) return m > 0 ? $"{H(h)} و{M(m)}" : H(h);
                return M(m);
            }
            if (d > 0) return h > 0 ? $"{d}d {h}h" : $"{d}d";
            if (h > 0) return m > 0 ? $"{h}h {m}m" : $"{h}h";
            return $"{m}m";
        }

        /// <summary>Largest unit only, for the countdown pill: "3 أيام", "19 ساعة" / "3d", "19h".</summary>
        public string ShortDur(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = t.Negate();
            int d = (int)t.TotalDays, h = (int)t.TotalHours, m = Math.Max(1, (int)t.TotalMinutes);
            if (IsAr)
            {
                if (d >= 1) return ArN(d, "يوم واحد", "يومان", "أيام", "يومًا");
                if (h >= 1) return ArN(h, "ساعة واحدة", "ساعتان", "ساعات", "ساعة");
                return ArN(m, "دقيقة واحدة", "دقيقتان", "دقائق", "دقيقة");
            }
            return d >= 1 ? $"{d}d" : h >= 1 ? $"{h}h" : $"{m}m";
        }

        public string Goals(int n) => IsAr
            ? n switch { 0 => "لا أهداف", 1 => "هدف واحد", 2 => "هدفان", <= 10 => $"{n} أهداف", _ => $"{n} هدفًا" }
            : n == 1 ? "1 goal" : $"{n} goals";

        static string ArN(int n, string one, string two, string few, string many) =>
            n == 1 ? one : n == 2 ? two : n <= 10 ? $"{n} {few}" : $"{n} {many}";

        static readonly Dictionary<string, string[]> T = new()
        {
            ["app"] = new[] { "كويست", "Quest" },
            ["tagline"] = new[] { "حقّق أهدافك في موعدها", "Hit your goals on time" },
            ["lang_switch"] = new[] { "English", "العربية" },

            ["nav_all"] = new[] { "كل الأهداف", "All goals" },
            ["nav_today"] = new[] { "اليوم", "Today" },
            ["nav_upcoming"] = new[] { "القادمة (7 أيام)", "Next 7 days" },
            ["nav_overdue"] = new[] { "المتأخرة", "Overdue" },
            ["nav_done"] = new[] { "المنجزة", "Completed" },
            ["nav_stats"] = new[] { "الإحصائيات", "Statistics" },
            ["categories"] = new[] { "التصنيفات", "CATEGORIES" },
            ["settings"] = new[] { "الإعدادات", "Settings" },

            ["new_goal"] = new[] { "هدف جديد", "New goal" },
            ["edit_goal"] = new[] { "تعديل الهدف", "Edit goal" },
            ["quick_add"] = new[] { "أضف هدفاً سريعاً ثم اضغط Enter…", "Quick add a goal and press Enter…" },
            ["search"] = new[] { "بحث…", "Search…" },
            ["title_ph"] = new[] { "ما الذي تريد تحقيقه؟", "What do you want to achieve?" },
            ["notes_ph"] = new[] { "تفاصيل إضافية (اختياري)", "Extra details (optional)" },
            ["category"] = new[] { "التصنيف", "Category" },
            ["priority"] = new[] { "الأولوية", "Priority" },
            ["deadline"] = new[] { "الموعد النهائي", "Deadline" },
            ["time"] = new[] { "الوقت", "Time" },
            ["recurrence"] = new[] { "التكرار", "Repeat" },
            ["remind"] = new[] { "التذكير", "Reminder" },
            ["steps"] = new[] { "الخطوات", "Steps" },
            ["add_step_ph"] = new[] { "أضف خطوة ثم اضغط Enter", "Add a step and press Enter" },
            ["save"] = new[] { "حفظ", "Save" },
            ["cancel"] = new[] { "إلغاء", "Cancel" },
            ["delete"] = new[] { "حذف", "Delete" },
            ["undo"] = new[] { "تراجع", "Undo" },
            ["deleted"] = new[] { "تم حذف الهدف", "Goal deleted" },
            ["great"] = new[] { "أحسنت! تم إنجاز الهدف 🎉", "Nice! Goal completed 🎉" },
            ["next_occ"] = new[] { "تم! الموعد القادم: {0}", "Done! Next one: {0}" },
            ["title_required"] = new[] { "اكتب عنوان الهدف أولاً", "Please enter a title first" },

            ["p_high"] = new[] { "عالية", "High" },
            ["p_medium"] = new[] { "متوسطة", "Medium" },
            ["p_low"] = new[] { "منخفضة", "Low" },
            ["r_none"] = new[] { "بدون", "None" },
            ["r_daily"] = new[] { "يومي", "Daily" },
            ["r_weekly"] = new[] { "أسبوعي", "Weekly" },
            ["r_monthly"] = new[] { "شهري", "Monthly" },
            ["rm_0"] = new[] { "بدون", "None" },
            ["rm_15"] = new[] { "قبل 15 دقيقة", "15 min before" },
            ["rm_60"] = new[] { "قبل ساعة", "1 hour before" },
            ["rm_1440"] = new[] { "قبل يوم", "1 day before" },

            ["no_deadline"] = new[] { "بدون موعد", "No deadline" },
            ["today"] = new[] { "اليوم", "Today" },
            ["tomorrow"] = new[] { "غداً", "Tomorrow" },
            ["yesterday"] = new[] { "أمس", "Yesterday" },
            ["next_week"] = new[] { "بعد أسبوع", "In a week" },
            ["left_fmt"] = new[] { "متبقٍ {0}", "{0} left" },
            ["late_fmt"] = new[] { "متأخر {0}", "{0} overdue" },
            ["due_now"] = new[] { "حان الموعد الآن", "Due now" },
            ["done_fmt"] = new[] { "أُنجز {0}", "Done: {0}" },

            ["cat_work"] = new[] { "عمل", "Work" },
            ["cat_study"] = new[] { "دراسة", "Study" },
            ["cat_personal"] = new[] { "شخصي", "Personal" },
            ["cat_health"] = new[] { "صحة", "Health" },
            ["cat_finance"] = new[] { "مالية", "Finance" },
            ["cat_other"] = new[] { "أخرى", "Other" },

            ["empty_title"] = new[] { "لا توجد أهداف هنا", "Nothing here yet" },
            ["empty_sub"] = new[] { "ابدأ بإضافة هدف جديد وحدّد موعده", "Add a goal and give it a deadline" },

            ["s_total"] = new[] { "إجمالي المُنجز", "Total completed" },
            ["s_week"] = new[] { "آخر 7 أيام", "Last 7 days" },
            ["s_ontime"] = new[] { "في الموعد", "On time" },
            ["s_streak"] = new[] { "أيام متتالية", "Day streak" },
            ["s_active"] = new[] { "أهداف نشطة", "Active goals" },
            ["s_last7"] = new[] { "الإنجاز خلال آخر 7 أيام", "Completions, last 7 days" },
            ["s_bycat"] = new[] { "حسب التصنيف", "By category" },

            ["set_lang"] = new[] { "اللغة", "Language" },
            ["set_theme"] = new[] { "الوضع", "Theme" },
            ["th_system"] = new[] { "النظام", "System" },
            ["th_dark"] = new[] { "داكن", "Dark" },
            ["th_light"] = new[] { "فاتح", "Light" },
            ["set_effect"] = new[] { "تأثير الزجاج", "Glass effect" },
            ["fx_acrylic"] = new[] { "أكريليك", "Acrylic" },
            ["fx_mica"] = new[] { "ميكا", "Mica" },
            ["fx_solid"] = new[] { "معتم", "Solid" },
            ["set_opacity"] = new[] { "الشفافية", "Transparency" },
            ["set_opacity_hint"] = new[] { "النصوص تبقى واضحة دائماً، فالبطاقات تحتفظ بحد أدنى من التعتيم", "Text always stays readable: cards keep a minimum opacity" },
            ["set_notify"] = new[] { "إشعارات التذكير", "Reminder notifications" },
            ["set_tray"] = new[] { "العمل في الخلفية", "Run in the background" },
            ["set_startup"] = new[] { "التشغيل مع بدء Windows", "Start with Windows" },
            ["glass_unsupported"] = new[] { "الزجاج يتطلب Windows 11", "Glass requires Windows 11" },

            ["tray_open"] = new[] { "فتح كويست", "Open Quest" },
            ["tray_exit"] = new[] { "خروج", "Exit" },
            ["tray_running"] = new[] { "يعمل في الخلفية وسيذكّرك بمواعيدك", "Running in the background, reminders stay on" },
            ["n_soon"] = new[] { "⏰ اقترب الموعد", "⏰ Deadline approaching" },
            ["n_due"] = new[] { "🔔 انتهى الموعد", "🔔 Deadline reached" },
            ["n_many"] = new[] { "لديك {0} تنبيهات", "You have {0} reminders" },

            ["composer_ph"] = new[] { "أضف هدفاً جديداً…", "Add a new goal…" },
            ["g_overdue"] = new[] { "متأخرة", "Overdue" },
            ["g_today"] = new[] { "اليوم", "Today" },
            ["g_tomorrow"] = new[] { "غداً", "Tomorrow" },
            ["g_week"] = new[] { "خلال الأسبوع", "This week" },
            ["g_later"] = new[] { "لاحقاً", "Later" },
            ["g_none"] = new[] { "بدون موعد", "No deadline" },
            ["g_done"] = new[] { "المنجزة", "Completed" },
            ["late_short"] = new[] { "متأخر {0}", "{0} late" },
            ["now_short"] = new[] { "الآن", "Now" },
            ["added"] = new[] { "أُضيف الهدف", "Goal added" },
            ["set_deadline"] = new[] { "حدّد موعداً", "Set deadline" },
            ["edit"] = new[] { "تعديل", "Edit" },
            ["delete_goal"] = new[] { "حذف الهدف", "Delete goal" },
            ["rm_s_0"] = new[] { "بدون", "Off" },
            ["rm_s_15"] = new[] { "15 دقيقة", "15 min" },
            ["rm_s_60"] = new[] { "ساعة", "1 hour" },
            ["rm_s_1440"] = new[] { "يوم", "1 day" },
            ["remind_before"] = new[] { "التذكير قبل الموعد", "Remind me before" },
            ["sec_details"] = new[] { "التفاصيل", "Details" },
            ["sec_appearance"] = new[] { "المظهر", "Appearance" },
            ["sec_language"] = new[] { "اللغة", "Language" },
            ["sec_notify"] = new[] { "التنبيهات والتشغيل", "Notifications & startup" },
            ["set_notify_desc"] = new[] { "تنبيه قبل الموعد وعند انتهائه", "Alerts before and at the deadline" },
            ["set_tray_desc"] = new[] { "إغلاق النافذة يُبقي التذكيرات تعمل", "Closing the window keeps reminders on" },
            ["set_startup_desc"] = new[] { "يبدأ مخفياً بجانب الساعة", "Starts hidden in the tray" },
            ["fx_desc"] = new[] { "الأكريليك يُظهر ما خلف النافذة، والميكا يأخذ لون خلفية سطح المكتب",
                                  "Acrylic shows what is behind the window; Mica picks up your wallpaper colour" },
            ["about"] = new[] { "كويست · الإصدار 1.4", "Quest · version 1.4" },
            ["made_by"] = new[] { "صُنع بواسطة", "Made by" },
            ["overdue_n"] = new[] { "{0} متأخر", "{0} overdue" },
            ["empty_action"] = new[] { "أضف هدفاً", "Add a goal" },

            ["load_problem_title"] = new[] { "تعذّرت قراءة ملف البيانات", "Your data file couldn't be read" },
            ["load_restored_text"] = new[] { "استرجعنا أهدافك من نسخة احتياطية ({0}). الملف التالف محفوظ كما هو في مجلد البيانات.",
                                             "Your goals were restored from a backup ({0}). The damaged file is kept untouched in the data folder." },
            ["load_failed_text"] = new[] { "لم نجد نسخة احتياطية سليمة، فبدأ التطبيق فارغاً. الملف الأصلي محفوظ كما هو في مجلد البيانات.",
                                           "No usable backup was found, so the app started empty. The original file is kept untouched in the data folder." },
            ["save_failed_title"] = new[] { "تعذّر حفظ آخر التغييرات", "Your latest changes couldn't be saved" },
            ["save_failed_text"] = new[] { "{0} سنعيد المحاولة تلقائياً كل 30 ثانية.", "{0} Retrying automatically every 30 seconds." },
            ["exit_unsaved_text"] = new[] { "لم تُحفظ آخر تغييراتك:\n{0}\n\nإذا خرجت الآن فستضيع. هل تريد الخروج على أي حال؟",
                                            "Your latest changes haven't been saved:\n{0}\n\nThey will be lost if you quit now. Quit anyway?" },
            ["retry"] = new[] { "أعد المحاولة", "Retry" },
            ["err_access"] = new[] { "لا يملك التطبيق إذن الكتابة على ملف البيانات، فقد يكون للقراءة فقط أو محمياً.",
                                     "The app isn't allowed to write the data file; it may be read-only or protected." },
            ["err_disk_full"] = new[] { "القرص ممتلئ.", "The disk is full." },
            ["err_locked"] = new[] { "ملف البيانات مستخدم من برنامج آخر الآن.", "Another program is using the data file right now." },
            ["open_data_folder"] = new[] { "فتح مجلد البيانات", "Open data folder" },
            ["sec_data"] = new[] { "البيانات والنسخ الاحتياطي", "Data & backups" },
            ["data_desc"] = new[] { "تُحفظ نسخة احتياطية تلقائياً كل ساعة أثناء الاستخدام، ويُحتفظ بآخر 20 نسخة. إذا تلف ملف البيانات يسترجع التطبيق أحدث نسخة سليمة.",
                                    "A backup is taken automatically every hour you use the app, and the latest 20 are kept. If the data file is ever damaged, the newest good backup is restored." },
            ["backup_now"] = new[] { "نسخة احتياطية الآن", "Back up now" },
            ["backup_done"] = new[] { "تم إنشاء نسخة احتياطية", "Backup created" },
            ["backup_failed"] = new[] { "تعذّر إنشاء النسخة الاحتياطية", "Couldn't create the backup" },
            ["last_backup"] = new[] { "آخر نسخة: {0}", "Last backup: {0}" },
            ["no_backup_yet"] = new[] { "لا توجد نسخ احتياطية بعد", "No backups yet" },

            ["ctx_done"] = new[] { "تم الإنجاز", "Mark as done" },
            ["ctx_undone"] = new[] { "إلغاء الإنجاز", "Mark as not done" },
            ["ctx_snooze"] = new[] { "تأجيل إلى الغد", "Postpone to tomorrow" },
            ["ctx_steps"] = new[] { "إظهار الخطوات أو إخفاؤها", "Show or hide steps" },
            ["snoozed"] = new[] { "أُجّل إلى {0}", "Postponed to {0}" },
            ["time_invalid"] = new[] { "اكتب الوقت مثل 21:30 أو 9:30 م", "Type a time like 21:30 or 9:30 pm" },

            ["set_accent"] = new[] { "لون التمييز", "Accent colour" },
            ["accent_system"] = new[] { "لون Windows", "Windows colour" },
            ["col_blue"] = new[] { "أزرق", "Blue" },
            ["col_violet"] = new[] { "بنفسجي", "Violet" },
            ["col_pink"] = new[] { "وردي", "Pink" },
            ["col_green"] = new[] { "أخضر", "Green" },
            ["col_amber"] = new[] { "كهرماني", "Amber" },
            ["col_slate"] = new[] { "رمادي", "Slate" },
            ["col_red"] = new[] { "أحمر", "Red" },
            ["col_teal"] = new[] { "فيروزي", "Teal" },
            ["col_orange"] = new[] { "برتقالي", "Orange" },
            ["col_indigo"] = new[] { "نيلي", "Indigo" },
            ["sec_categories"] = new[] { "التصنيفات", "Categories" },
            ["cat_desc"] = new[] { "غيّر اسم أي تصنيف أو لونه، أو أضف تصنيفاتك. أهداف التصنيف المحذوف تنتقل إلى «{0}».",
                                   "Rename or recolour any category, or add your own. Goals of a deleted category move to “{0}”." },
            ["add_category"] = new[] { "إضافة تصنيف", "Add category" },
            ["new_category"] = new[] { "تصنيف جديد", "New category" },
            ["cat_deleted"] = new[] { "حُذف تصنيف «{0}»", "Deleted “{0}”" },
            ["a11y_cat_color"] = new[] { "لون تصنيف {0}", "Colour of {0}" },
            ["a11y_cat_delete"] = new[] { "حذف تصنيف {0}", "Delete {0}" },
            ["export"] = new[] { "تصدير…", "Export…" },
            ["import"] = new[] { "استيراد…", "Import…" },
            ["transfer_desc"] = new[] { "التصدير يحفظ كل أهدافك في ملف تختاره، لتنقلها إلى جهاز آخر مثلاً، ولتصدير هدف واحد اختر «تصدير…» من قائمة الزر الأيمن على بطاقته. الاستيراد يضيف ما تختاره من أهداف الملف إلى أهدافك.",
                                        "Export saves all your goals to a file you choose, for example to move them to another PC; to export a single goal, choose Export… from its card's right-click menu. Import adds the goals you pick from a file to yours." },
            ["file_filter"] = new[] { "بيانات كويست (*.json)|*.json", "Quest data (*.json)|*.json" },
            ["exported"] = new[] { "صُدّرت البيانات إلى {0}", "Exported to {0}" },
            ["export_failed"] = new[] { "تعذّر التصدير: {0}", "Couldn't export: {0}" },
            ["import_invalid"] = new[] { "هذا الملف ليس ملف بيانات من كويست، أو أنه تالف", "That isn't a Quest data file, or it's damaged" },
            ["imported"] = new[] { "استُوردت البيانات: {0}", "Imported: {0}" },
            ["import_empty"] = new[] { "لا أهداف في هذا الملف", "There are no goals in that file" },
            ["import_added_one"] = new[] { "أُضيف «{0}» إلى أهدافك", "Added “{0}” to your goals" },
            ["import_added"] = new[] { "أُضيفت إلى أهدافك: {0}", "Added to your goals: {0}" },
            ["import_have_one"] = new[] { "«{0}» موجود في أهدافك أصلاً", "“{0}” is already in your goals" },
            ["import_have_all"] = new[] { "هذه الأهداف موجودة في أهدافك أصلاً", "These goals are already in yours" },
            ["import_title"] = new[] { "استيراد", "Import" },
            ["import_add_n"] = new[] { "إضافة ({0})", "Add ({0})" },
            ["import_pick"] = new[] { "اختر الأهداف التي تريد إضافتها. لن يُحذف أو يتغيّر شيء من أهدافك.",
                                      "Pick the goals to add. Nothing of yours is removed or changed." },
            ["import_all"] = new[] { "تحديد الكل", "Select all" },
            ["import_have"] = new[] { "موجود عندك", "already yours" },
            ["import_replace_desc"] = new[] { "أو استبدل أهدافك وسجلّ إنجازك وتصنيفاتك كلها بما في الملف. تُؤخذ نسخة احتياطية منها أولاً، ويمكنك التراجع.",
                                              "Or replace all your goals, history and categories with the file's. They're backed up first, and you can undo it." },
            ["import_replace"] = new[] { "استبدال كل أهدافي بما في الملف", "Replace all my goals with the file's" },

            ["a11y_menu"] = new[] { "القائمة", "Menu" },
            ["a11y_minimize"] = new[] { "تصغير", "Minimize" },
            ["a11y_maximize"] = new[] { "تكبير", "Maximize" },
            ["a11y_search"] = new[] { "بحث", "Search" },
            ["a11y_add"] = new[] { "هدف جديد بالتفاصيل", "New goal with details" },
            ["a11y_dismiss"] = new[] { "إخفاء التنبيه", "Dismiss" },
            ["a11y_back"] = new[] { "رجوع", "Back" },
            ["a11y_theme"] = new[] { "تبديل الوضع الداكن والفاتح", "Switch dark and light" },
            ["a11y_prev_month"] = new[] { "الشهر السابق", "Previous month" },
            ["a11y_next_month"] = new[] { "الشهر التالي", "Next month" },
            ["a11y_remove_step"] = new[] { "حذف الخطوة", "Remove step" },
            ["step_to_heading"] = new[] { "اجعلها عنواناً", "Make it a heading" },
            ["heading_to_step"] = new[] { "اجعلها خطوة", "Make it a step" },

            ["host_title"] = new[] { "كويست يعمل من داخل {0}", "Quest is running inside {0}" },
            ["host_text"] = new[] { "شغّل {0} كويست، لذلك يحفظ Windows ما تغيّره هنا في مساحة {0} الخاصة، ولن تجده عندما تفتح كويست بنفسك. اخرج منه وافتحه بنفسك من مجلده أو من اختصاره.",
                                    "{0} started Quest, so Windows keeps what you change here in {0}'s private storage, and you won't find it when you open Quest yourself. Exit, then open Quest yourself from its folder or a shortcut." },
            ["startup_title"] = new[] { "لا تفوّتك التذكيرات", "Don't miss your reminders" },
            ["startup_text"] = new[] { "التذكيرات تعمل فقط عندما يكون كويست مفتوحاً. شغّله تلقائياً مع بدء Windows، مخفياً بجانب الساعة.",
                                       "Reminders only work while Quest is running. Start it automatically with Windows, hidden next to the clock." },
            ["startup_enable"] = new[] { "تشغيل مع Windows", "Start with Windows" },
            ["startup_done"] = new[] { "سيعمل كويست تلقائياً مع Windows", "Quest will start with Windows" },
            ["startup_failed"] = new[] { "تعذّر تفعيل التشغيل مع Windows", "Couldn't turn on start with Windows" },

            ["nav_daily"] = new[] { "المهام اليومية", "Daily tasks" },
            ["g_daily"] = new[] { "يومية", "Daily" },
            ["sec_schedule"] = new[] { "الموعد والتكرار", "Schedule & repeat" },
            ["days"] = new[] { "أيام التكرار", "Repeat on" },
            ["every_day"] = new[] { "كل يوم", "Every day" },
            ["days_a_week"] = new[] { "{0} أيام في الأسبوع", "{0} days a week" },
            ["any_time"] = new[] { "أي وقت", "Any time" },
            ["at_time"] = new[] { "وقت محدد", "Set time" },
            ["composer_daily_ph"] = new[] { "أضف مهمة يومية…", "Add a daily task…" },
            ["added_daily"] = new[] { "أُضيفت مهمة يومية", "Daily task added" },
            ["daily_done"] = new[] { "أحسنت! تتجدد غداً", "Done for today, back tomorrow" },
            ["daily_done_streak"] = new[] { "أحسنت! {0} على التوالي", "Nice! {0} in a row" },
            ["streak_fmt"] = new[] { "{0} على التوالي", "{0} in a row" },
            ["daily_progress"] = new[] { "أنجزت {0} من {1} اليوم", "{0} of {1} done today" },
            ["empty_daily_title"] = new[] { "لا توجد مهام يومية", "No daily tasks yet" },
            ["empty_daily_sub"] = new[] { "أضف شيئاً تكرره كل يوم، مثل الرياضة أو القراءة، وسيتجدد تلقائياً كل صباح",
                                          "Add something you repeat every day, like exercise or reading; it renews every morning" },
        };
    }

    /// <summary>XAML: Text="{local:L key}"</summary>
    public class L : MarkupExtension
    {
        public L(string key) { Key = key; }
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider sp) =>
            new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(sp);
    }
}
