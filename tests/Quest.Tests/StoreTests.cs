using System;
using System.IO;
using System.Linq;

namespace Quest.Tests
{
    /// <summary>The data file, its backups and export/import, in the scratch folder the runner set up.</summary>
    static class StoreTests
    {
        static string MainFile => Path.Combine(Store.Dir, "data.json");

        static void Empty()
        {
            foreach (var f in Directory.GetFiles(Store.Dir, "*", SearchOption.AllDirectories)) File.Delete(f);
        }

        static AppData Sample(string title)
        {
            var data = new AppData();
            data.Goals.Add(new Goal { Title = title, Category = "work", Due = new DateTime(2027, 1, 2, 9, 0, 0) });
            return data;
        }

        [Test]
        static void SavedDataLoadsBack()
        {
            Empty();
            Assert.Equal(null, Store.Save(Sample("قراءة 🎉")), "save error");
            var back = Store.Load();
            Assert.Equal(null, Store.Issue, "load problem");
            Assert.Equal("قراءة 🎉", back.Goals.Single().Title, "title");
            Assert.Equal(new DateTime(2027, 1, 2, 9, 0, 0), back.Goals[0].Due, "deadline");
        }

        [Test]
        static void PartHeadingsLoadBackAndStepsAreWrittenAsBefore()
        {
            Empty();
            var data = Sample("Footprinting");
            data.Goals[0].SubTasks.Add(new SubTask { Title = "Introduction", IsHeading = true });
            data.Goals[0].SubTasks.Add(new SubTask { Title = "Enumeration Principles", Done = true });
            Assert.Equal(null, Store.Save(data), "save error");
            Assert.Equal(2, File.ReadAllText(MainFile).Split("IsHeading").Length, "only the heading carries the flag");
            var steps = Store.Load().Goals.Single().SubTasks;
            Assert.True(steps[0].IsHeading && !steps[1].IsHeading && steps[1].Done, "heading and step back as they were");
        }

        [Test]
        static void DamagedFileIsKeptAndTheNewestBackupRestored()
        {
            Empty();
            Store.Save(Sample("good"));
            Assert.True(Store.BackupNow(), "backup taken");
            File.WriteAllText(MainFile, "{ this is not json");
            var back = Store.Load();
            Assert.Equal(LoadProblem.RestoredFromBackup, Store.Issue?.Kind, "restored");
            Assert.Equal("good", back.Goals.Single().Title, "from the backup");
            Assert.True(Directory.GetFiles(Store.Dir, "data.unreadable-*.json").Length == 1, "damaged file set aside");
        }

        [Test]
        static void WithoutBackupsAnUnreadableFileIsNeverOverwritten()
        {
            Empty();
            File.WriteAllText(MainFile, "garbage");
            var back = Store.Load();
            Assert.Equal(LoadProblem.Unreadable, Store.Issue?.Kind, "reported");
            Assert.Equal(0, back.Goals.Count, "starts empty");
            Assert.Equal("garbage", File.ReadAllText(MainFile), "the file itself untouched");
            Assert.True(Directory.GetFiles(Store.Dir, "data.unreadable-*.json").Length == 1, "and a copy set aside");
        }

        [Test]
        static void ExportIsReadableAndImportsBack()
        {
            Empty();
            var path = Path.Combine(Store.Dir, "export.json");
            var data = Sample("نقل");
            data.Settings.Categories = Categories.Use(null);
            Store.Export(data, path);
            var text = File.ReadAllText(path);
            Assert.True(text.Contains("\n  ") && text.Contains("نقل"), "indented, with the text as written");
            var back = Store.ReadForImport(path);
            Assert.Equal("نقل", back?.Goals.Single().Title, "imported");
            Assert.Equal(6, back.Settings.Categories.Count, "with the categories");
        }

        // ---- the data folder of earlier versions, under the app's old name ----

        static string NewAppData() => Directory.CreateDirectory(Path.Combine(Store.Dir, "appdata-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        [Test]
        static void DataFolderFromBeforeTheRenameIsCarriedOver()
        {
            var appData = NewAppData();
            var old = Path.Combine(appData, Legacy.Name);
            Directory.CreateDirectory(Path.Combine(old, "backups"));
            File.WriteAllText(Path.Combine(old, "data.json"), "{}");
            File.WriteAllText(Path.Combine(old, "backups", "data-20270102-090000.json"), "{}");
            var dir = Store.Folder(appData);
            Assert.Equal(Path.Combine(appData, "Quest"), dir, "the new name");
            Assert.True(File.Exists(Path.Combine(dir, "data.json")) && File.Exists(Path.Combine(dir, "backups", "data-20270102-090000.json")),
                "data and backups came along");
            Assert.False(Directory.Exists(old), "nothing left behind");
            Assert.Equal(dir, Store.Folder(appData), "next time there is nothing to do");
        }

        [Test]
        static void DataFolderStaysWhereItIsWhileAFileInItIsOpen()
        {
            var appData = NewAppData();
            var old = Path.Combine(appData, Legacy.Name);
            Directory.CreateDirectory(old);
            var file = Path.Combine(old, "data.json");
            File.WriteAllText(file, "{}");
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(old, Store.Folder(appData), "keeps using the old folder");
            Assert.Equal(Path.Combine(appData, "Quest"), Store.Folder(appData), "and moves it once it is free");
        }

        [Test]
        static void NewInstallUsesTheNewName()
        {
            var appData = NewAppData();
            Assert.Equal(Path.Combine(appData, "Quest"), Store.Folder(appData), "no old folder");
        }

        [Test]
        static void ImportTakesOnlyItsOwnFiles()
        {
            Empty();
            var path = Path.Combine(Store.Dir, "other.json");
            foreach (var content in new[] { "{ \"Hello\": 1 }", "[]", "not json at all", "" })
            {
                File.WriteAllText(path, content);
                Assert.Equal(null, Store.ReadForImport(path), $"'{content}'");
            }
            Assert.Equal(null, Store.ReadForImport(Path.Combine(Store.Dir, "missing.json")), "a missing file");
        }
    }
}
