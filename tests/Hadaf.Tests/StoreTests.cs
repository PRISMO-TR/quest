using System;
using System.IO;
using System.Linq;

namespace Hadaf.Tests
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

        [Test]
        static void ImportTakesOnlyHadafFiles()
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
