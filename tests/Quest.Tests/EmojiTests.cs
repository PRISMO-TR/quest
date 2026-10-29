using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Quest.Tests
{
    /// <summary>Which emoji get drawn in colour. Skipped (passing) on a PC without Segoe UI Emoji.</summary>
    static class EmojiTests
    {
        static bool FontPresent => new Typeface("Segoe UI Emoji").TryGetGlyphTypeface(out _);

        static TextBlock Show(string text)
        {
            var block = new TextBlock();
            Emoji.Show(block, text);
            return block;
        }

        static int Pictures(string text) => Show(text).Inlines.OfType<InlineUIContainer>().Count();

        [Test]
        static void SingleEmojiAreDrawnInColour()
        {
            if (!FontPresent) return;
            Assert.Equal(2, Pictures("قراءة 📚 كل يوم 🎉"), "two pictographs");
            Assert.Equal(1, Pictures("اتصل بأمي ❤️"), "a symbol asked to look like an emoji");
            Assert.Equal(1, Pictures("✅ done"), "a symbol that is an emoji by default");
        }

        [Test]
        static void SequencesAndTextStyleSymbolsStayPlain()
        {
            if (!FontPresent) return;
            foreach (var text in new[] { "🇸🇦", "👨‍👩‍👧", "👍🏽", "☺", "☺︎", "1️⃣", "© 2027" })
                Assert.Equal(0, Pictures(text), $"'{text}'");
        }

        [Test]
        static void TheTextAroundEmojiIsKept()
        {
            if (!FontPresent) return;
            var block = Show("أ 🎉 ب");
            Assert.Equal("أ  ب", string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)), "text on both sides");
        }

        [Test]
        static void TextWithoutEmojiIsLeftAsPlainText()
        {
            var block = Show("Hello عربي 123");
            Assert.Equal("Hello عربي 123", block.Text, "text");
            Assert.Equal(0, block.Inlines.OfType<InlineUIContainer>().Count(), "no pictures");
        }
    }
}
