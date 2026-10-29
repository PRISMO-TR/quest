using System;
using System.Linq;
using System.Windows.Media;

namespace Hadaf
{
    // The statistics page.
    public partial class MainWindow
    {
        // ================= statistics =================

        void BuildStats()
        {
            var today = DateTime.Today;
            var accent = (SolidColorBrush)FindResource("Accent");
            var s = Planner.Stats(data.History, DateTime.Now);
            int total = s.Total, week = s.LastSevenDays, streak = s.DayStreak;
            string onTime = s.OnTimePercent is int p ? p + "%" : "–";

            Tiles.ItemsSource = new[]
            {
                new Tile("", total.ToString(), L["s_total"], Palette.B(0xFF10B981)),
                new Tile("", week.ToString(), L["s_week"], accent),
                new Tile("", onTime, L["s_ontime"], Palette.B(0xFFF59E0B)),
                new Tile("", streak.ToString(), L["s_streak"], Palette.B(0xFFEC4899)),
            };

            int max = Math.Max(1, s.Week.Max(x => x.Count));
            var ac = accent.Color;
            var faded = Palette.B(0x59000000 | (uint)(ac.R << 16 | ac.G << 8 | ac.B));
            Bars.ItemsSource = s.Week.Select(x => new BarItem(
                x.Day.ToString("ddd", L.Culture), x.Count,
                x.Count == 0 ? 4 : 12 + 100.0 * x.Count / max,
                x.Day == today ? accent : faded)).ToList();

            CatStats.ItemsSource = Categories.All.Select(c =>
            {
                int n = Planner.CompletionsIn(data.History, c.Id);
                return new CatStat(Categories.NameOf(c), Categories.BrushOf(c.Id), total == 0 ? 0 : 100.0 * n / total, n);
            }).ToList();
        }
    }
}
