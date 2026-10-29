using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Quest.Tests
{
    /// <summary>A test: a static method without parameters that throws when something is wrong.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    sealed class TestAttribute : Attribute { }

    sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new TestFailure(what);
        }

        public static void False(bool condition, string what) => True(!condition, what);

        public static void Equal<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new TestFailure($"{(what == null ? "" : what + ": ")}expected <{expected}>, got <{actual}>");
        }

        public static void Throws<T>(Action action, string what) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new TestFailure($"{what}: expected {typeof(T).Name}");
        }
    }

    /// <summary>
    /// Runs every [Test] (or the ones whose name contains one of the arguments) and exits with the number that
    /// failed. No test framework to download: dotnet run --project tests\Quest.Tests
    /// </summary>
    static class Runner
    {
        [STAThread] // the emoji tests build WPF text
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // Store reads its folder once, so point it at a scratch folder before anything touches it.
            var dir = Path.Combine(Path.GetTempPath(), "quest-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("QUEST_DATA_DIR", dir);

            var tests = typeof(Runner).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Select(m => (Name: $"{m.DeclaringType.Name}.{m.Name}", Method: m))
                .Where(t => args.Length == 0 || args.Any(a => t.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            if (tests.Count == 0)
            {
                Console.WriteLine($"No test matches: {string.Join(" ", args)}");
                return 1;
            }

            int failed = 0;
            var clock = Stopwatch.StartNew();
            foreach (var (name, method) in tests)
            {
                try
                {
                    method.Invoke(null, null);
                    Console.WriteLine($"  pass  {name}");
                }
                catch (TargetInvocationException e)
                {
                    failed++;
                    Console.WriteLine($"  FAIL  {name}: {e.InnerException?.Message}");
                    if (e.InnerException is not TestFailure) Console.WriteLine(e.InnerException);
                }
                finally
                {
                    // App-wide state: every test starts with the default categories and Arabic.
                    Categories.Use(null);
                    Loc.I.Set("ar");
                }
            }
            Console.WriteLine($"{tests.Count - failed} passed, {failed} failed ({clock.ElapsedMilliseconds} ms)");
            try { Directory.Delete(dir, true); } catch { }
            return failed;
        }
    }
}
