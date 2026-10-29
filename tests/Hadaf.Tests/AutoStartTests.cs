using System;
using Microsoft.Win32;

namespace Hadaf.Tests
{
    /// <summary>
    /// Start with Windows. The runner sets HADAF_DATA_DIR, so these use the app's test key, which Windows never reads:
    /// the real start-up list is not touched.
    /// </summary>
    static class AutoStartTests
    {
        const string TestKey = @"Software\Hadaf\TestRun";

        static object Value(string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(TestKey);
            return k?.GetValue(name);
        }

        static void Clean() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\Hadaf", false);

        [Test]
        static void EntryFromBeforeTheRenameIsCarriedOver()
        {
            Clean();
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("Hadaf", "\"C:\\old place\\Hadaf.exe\" --tray");
                AutoStart.Repair();
                Assert.Equal(null, Value("Hadaf"), "the old entry is gone");
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
                using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("Hadaf", "old");
                AutoStart.Set(true);
                Assert.True(Value("Quest") is string && Value("Hadaf") == null, "on, under the new name only");
                AutoStart.Set(false);
                Assert.False(AutoStart.IsEnabled(), "off");
                Assert.True(Value("Quest") == null && Value("Hadaf") == null, "no entry left");
            }
            finally { Clean(); }
        }
    }
}
