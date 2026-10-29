using System;
using Microsoft.Win32;

namespace Quest.Tests
{
    /// <summary>
    /// Start with Windows. The runner sets QUEST_DATA_DIR, so these use the app's test key, which Windows never reads:
    /// the real start-up list is not touched.
    /// </summary>
    static class AutoStartTests
    {
        const string TestKey = @"Software\QuestTestRun";

        static object Value(string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(TestKey);
            return k?.GetValue(name);
        }

        static void Clean() => Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);

        [Test]
        static void EntryFromBeforeTheRenameIsCarriedOver()
        {
            Clean();
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue(Legacy.Name, $"\"C:\\old place\\{Legacy.Name}.exe\" --tray");
                AutoStart.Repair();
                Assert.Equal(null, Value(Legacy.Name), "the old entry is gone");
                Assert.Equal($"\"{Environment.ProcessPath}\" --tray", Value("Quest"), "the new one starts this copy, hidden");
                Assert.True(AutoStart.IsEnabled(), "still on");
            }
            finally { Clean(); }
        }

        [Test]
        static void TurningItOnAndOff()
        {
            Clean();
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue(Legacy.Name, "old");
                AutoStart.Set(true);
                Assert.True(Value("Quest") is string && Value(Legacy.Name) == null, "on, under the new name only");
                AutoStart.Set(false);
                Assert.False(AutoStart.IsEnabled(), "off");
                Assert.True(Value("Quest") == null && Value(Legacy.Name) == null, "no entry left");
            }
            finally { Clean(); }
        }
    }
}
