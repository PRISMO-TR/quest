using System.Collections.Generic;
using System.Linq;

namespace Quest.Tests
{
    static class CategoryTests
    {
        static string Ids(IEnumerable<CategoryDef> defs) => string.Join(",", defs.Select(c => c.Id));

        [Test]
        static void BuiltInCategoriesWhenTheFileHasNone()
        {
            Assert.Equal("work,study,personal,health,finance,other", Ids(Categories.Use(null)), "defaults");
            Assert.Equal("personal", Categories.Default, "new goals");
        }

        [Test]
        static void HandEditedListsAreRepaired()
        {
            var defs = Categories.Use(new List<CategoryDef>
            {
                new() { Id = "gym", Name = "Gym", Color = "#14B8A6" },
                new() { Id = "gym", Name = "Copy", Color = "#000000" },
                new() { Id = " ", Name = "No id" },
                null,
                new() { Id = "art", Name = "Art", Color = "red" },
            });
            Assert.Equal("gym,art,other", Ids(defs), "repeats and blanks dropped, the fallback added");
            Assert.Equal("Gym", defs[0].Name, "the first of a repeated id kept");
            Assert.Equal("#64748B", defs[1].Color, "a colour that isn't #RRGGBB replaced");
            Assert.Equal(Categories.Fallback, Categories.Default, "without 'personal', new goals go to the fallback");
        }

        [Test]
        static void BuiltInNamesFollowTheLanguageUnlessRenamed()
        {
            var defs = Categories.Use(null);
            Loc.I.Set("en");
            Assert.Equal("Work", Categories.NameOf("work"), "English");
            defs[0].Name = "Job";
            Assert.Equal("Job", Categories.NameOf("work"), "renamed");
            Loc.I.Set("ar");
            Assert.Equal("Job", Categories.NameOf("work"), "the user's name stays in any language");
            defs[0].Name = null;
            Assert.Equal("عمل", Categories.NameOf("work"), "the built-in name again");
        }

        [Test]
        static void UnknownCategoryShowsAsTheFallback()
        {
            Categories.Use(null);
            Assert.Equal(Categories.NameOf(Categories.Fallback), Categories.NameOf("deleted"), "name");
            Assert.Equal(Categories.BrushOf(Categories.Fallback).Color, Categories.BrushOf("deleted").Color, "colour");
        }

        [Test]
        static void ColoursRoundTripAsHex()
        {
            Assert.Equal("#3B82F6", Palette.Hex(0xFF3B82F6), "to hex");
            Assert.True(Palette.TryParse("#3B82F6", out var argb) && argb == 0xFF3B82F6, "from hex");
            foreach (var bad in new[] { "3B82F6", "#GG0000", "#12345", null, "" })
                Assert.False(Palette.TryParse(bad, out _), $"'{bad}'");
        }
    }
}
