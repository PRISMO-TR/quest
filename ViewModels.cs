using System.Collections.Generic;
using System.Windows.Media;

namespace Hadaf
{
    public class Opt
    {
        public object Key { get; set; }
        public string Name { get; set; }
        public Brush Brush { get; set; }
    }

    public class NavItem : Notify
    {
        string _name;
        int _count;
        bool _alert;
        public string Key { get; set; }
        public string Icon { get; set; }
        public Brush Brush { get; set; }
        public bool IsCat { get; set; }
        public bool ShowCount { get; set; } = true;
        public string Name { get => _name; set => Set(ref _name, value); }
        public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(HasCount)); } }
        public bool HasCount => ShowCount && Count > 0;
        public bool IsAlert { get => _alert; set => Set(ref _alert, value); }
    }

    /// <summary>A row of the category editor in Settings.</summary>
    public class CatRow : Notify
    {
        bool _picking;
        public CategoryDef Def { get; init; }
        public List<Opt> Colors { get; init; }
        public string Name => Categories.NameOf(Def);
        /// <summary>Shown when the box is emptied: a built-in category goes back to its own name.</summary>
        public string Placeholder => Categories.IsBuiltIn(Def.Id) ? Categories.DefaultName(Def.Id) : "";
        public Brush Brush => Categories.BrushOf(Def.Id);
        public bool CanDelete => Def.Id != Categories.Fallback;
        /// <summary>The colour choices are open.</summary>
        public bool Picking { get => _picking; set => Set(ref _picking, value); }
        public string ColorLabel => Loc.I.F("a11y_cat_color", Name);
        public string DeleteLabel => Loc.I.F("a11y_cat_delete", Name);
    }

    /// <summary>A goal in a file being imported, as the import page lists it.</summary>
    public class ImportRow : Notify
    {
        bool _picked;
        public Goal Goal { get; init; }
        /// <summary>Not one of the user's goals yet; one they already have can't be picked.</summary>
        public bool IsNew { get; init; }
        public bool Picked { get => _picked; set => Set(ref _picked, value); }
        /// <summary>The colour of its category, as it will show once imported.</summary>
        public Brush Brush { get; init; }
        /// <summary>Category · when · steps, and whether the user already has it.</summary>
        public string Meta { get; init; }
    }

    /// <summary>Section title inside the goal list ("Overdue 2", "Daily 1/3" …).</summary>
    public class GroupHeader
    {
        public string Key { get; set; }
        public string Title { get; set; }
        /// <summary>Shown next to the title: a count, or "done/planned today" for daily tasks.</summary>
        public string CountText { get; set; }
        public bool Collapsible { get; set; }
        public bool Expanded { get; set; } = true;
        public bool IsFirst { get; set; }
        public string Chevron { get; set; }
    }

    public record Tile(string Icon, string Value, string Label, Brush Brush);
    public record BarItem(string Label, int Count, double Height, Brush Brush);
    public record CatStat(string Name, Brush Brush, double Pct, int Count);
}
