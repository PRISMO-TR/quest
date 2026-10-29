using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Hadaf
{
    // Saving and data safety: failed saves, notices, backups, export and import.
    public partial class MainWindow
    {
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
            Planner.Prepare(data);
            Planner.RollDailies(data.Goals, DateTime.Now);
            CategoriesChanged();
            UpdateBackupInfo();
        }
    }
}
