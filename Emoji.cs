using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Quest
{
    /// <summary>
    /// Colour emoji. WPF draws Segoe UI Emoji in one colour only, so this reads the font's colour layers (its COLR
    /// and CPAL tables) and draws each emoji as its stack of coloured glyphs. Single emoji only: sequences (skin
    /// tones, flags, families) and anything the font has no layers for keep the plain look. The font is read the
    /// first time an emoji turns up; text without emoji never touches it.
    /// </summary>
    public static class Emoji
    {
        /// <summary>XAML: local:Emoji.Text="{Binding Title}" on a TextBlock, in place of Text.</summary>
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Emoji), new PropertyMetadata(null, (o, e) => Show((TextBlock)o, (string)e.NewValue)));

        public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
        public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

        const double Em = 64; // size the layers are drawn at; the picture is scaled to the text

        static GlyphTypeface face;
        static Dictionary<ushort, (int First, int Count)> bases;   // emoji glyph → its layers
        static (ushort Glyph, ushort Color)[] layers;
        static Brush[] colors;                                      // palette 0
        static bool? ready;
        static readonly Dictionary<int, DrawingImage> Pictures = new();

        // Symbols below the pictograph block that are emoji unless asked otherwise (Emoji_Presentation=Yes).
        static readonly int[] EmojiStyle =
        {
            0x231A, 0x231B, 0x23E9, 0x23EA, 0x23EB, 0x23EC, 0x23F0, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615,
            0x2648, 0x2649, 0x264A, 0x264B, 0x264C, 0x264D, 0x264E, 0x264F, 0x2650, 0x2651, 0x2652, 0x2653,
            0x267F, 0x2693, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26D4, 0x26EA,
            0x26F2, 0x26F3, 0x26F5, 0x26FA, 0x26FD, 0x2705, 0x270A, 0x270B, 0x2728, 0x274C, 0x274E, 0x2753,
            0x2754, 0x2755, 0x2757, 0x2795, 0x2796, 0x2797, 0x27B0, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B55,
        };

        /// <summary>Shows <paramref name="text"/> in <paramref name="block"/>, with any emoji in it in colour.</summary>
        public static void Show(TextBlock block, string text)
        {
            if (string.IsNullOrEmpty(text) || !MayContain(text) || !Ready)
            {
                block.Text = text;
                return;
            }
            var inlines = new List<Inline>();
            int plain = 0; // start of the text not added yet
            for (int i = 0; i < text.Length;)
            {
                int cp = CodePoint(text, i, out int len);
                if (!Starts(text, i, cp, len))
                {
                    i += len;
                    continue;
                }
                int end = SequenceEnd(text, i, cp, len);
                bool single = end - i == len || end - i == len + 1 && text[i + len] == '️';
                if (single && Picture(cp) is DrawingImage source)
                {
                    if (i > plain) inlines.Add(new Run(text[plain..i]));
                    var image = new Image { Source = source, Height = block.FontSize * face.Height, Stretch = Stretch.Uniform };
                    inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                    plain = end;
                }
                i = end;
            }
            if (inlines.Count == 0)
            {
                block.Text = text;
                return;
            }
            if (plain < text.Length) inlines.Add(new Run(text[plain..]));
            block.Inlines.Clear();
            block.Inlines.AddRange(inlines);
        }

        // Cheap test before the real scan: anything in the emoji ranges at all?
        static bool MayContain(string s)
        {
            foreach (char c in s)
                if (c >= '\uD83C' && c <= '\uD83E' || c >= '←' && c <= '⯿' || c == '️') return true;
            return false;
        }

        static int CodePoint(string s, int i, out int len)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                len = 2;
                return char.ConvertToUtf32(s[i], s[i + 1]);
            }
            len = 1;
            return s[i];
        }

        /// <summary>Does an emoji start here? Pictographs always; older symbols when they are emoji-style.</summary>
        static bool Starts(string s, int i, int cp, int len)
        {
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;
            if (cp < 0x2190 || cp > 0x2BFF) return false;
            return Array.BinarySearch(EmojiStyle, cp) >= 0 || i + len < s.Length && s[i + len] == '️';
        }

        /// <summary>Where the emoji starting at <paramref name="i"/> ends: style selectors, skin tones, keycaps, flags, tags, joins.</summary>
        static int SequenceEnd(string s, int i, int cp, int len)
        {
            int end = i + len;
            if (cp >= 0x1F1E6 && cp <= 0x1F1FF && end < s.Length)
            {
                int other = CodePoint(s, end, out int l); // a flag is a pair of regional indicators
                if (other >= 0x1F1E6 && other <= 0x1F1FF) end += l;
            }
            while (end < s.Length)
            {
                int next = CodePoint(s, end, out int l);
                if (next is 0xFE0F or 0xFE0E or 0x20E3 || next >= 0x1F3FB && next <= 0x1F3FF || next >= 0xE0020 && next <= 0xE007F)
                    end += l;
                else if (next == 0x200D && end + 1 < s.Length) // zero-width joiner: the next emoji is part of this one
                {
                    CodePoint(s, end + 1, out int joined);
                    end += 1 + joined;
                }
                else break;
            }
            return end;
        }

        /// <summary>The emoji's colour layers as one picture of its whole cell, so all emoji come out the same size.</summary>
        static DrawingImage Picture(int cp)
        {
            if (Pictures.TryGetValue(cp, out var known)) return known;
            DrawingImage image = null;
            if (face.CharacterToGlyphMap.TryGetValue(cp, out ushort glyph) && bases.TryGetValue(glyph, out var range)
                && range.First + range.Count <= layers.Length)
            {
                double advance = face.AdvanceWidths[glyph];
                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null,
                    new RectangleGeometry(new Rect(0, 0, advance * Em, face.Height * Em))));
                var origin = new Point(0, face.Baseline * Em);
                bool complete = true;
                for (int i = range.First; i < range.First + range.Count && complete; i++)
                {
                    var (layer, color) = layers[i];
                    complete = color < colors.Length; // 0xFFFF means "the text colour", which a fixed picture can't follow
                    if (complete)
                        group.Children.Add(new GlyphRunDrawing(colors[color], new GlyphRun(face, 0, false, Em, 1f, new[] { layer },
                            origin, new[] { advance * Em }, null, null, null, null, null, null)));
                }
                if (complete)
                {
                    image = new DrawingImage(group);
                    image.Freeze();
                }
            }
            Pictures[cp] = image;
            return image;
        }

        static bool Ready => ready ??= Load();

        /// <summary>Reads the colour tables of Segoe UI Emoji (version 0 of COLR, which Windows 10 and 11 both have).</summary>
        static bool Load()
        {
            try
            {
                if (!new Typeface("Segoe UI Emoji").TryGetGlyphTypeface(out face)) return false;
                using var file = File.OpenRead(face.FontUri.LocalPath);
                var r = new BinaryReader(file);
                ushort U16() { var b = r.ReadBytes(2); return (ushort)(b[0] << 8 | b[1]); }
                uint U32() { var b = r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }

                long font = 0;
                if (U32() == 0x74746366) // "ttcf": a collection of fonts; the face's number is the URI fragment
                {
                    int index = int.TryParse(face.FontUri.Fragment.TrimStart('#'), out int n) ? n : 0;
                    file.Position = 12 + 4 * index;
                    font = U32();
                }
                file.Position = font + 4;
                int tableCount = U16();
                long colr = 0, cpal = 0;
                for (int t = 0; t < tableCount; t++)
                {
                    file.Position = font + 12 + 16 * t;
                    uint tag = U32();
                    U32(); // checksum
                    uint offset = U32();
                    if (tag == 0x434F4C52) colr = offset;      // "COLR"
                    else if (tag == 0x4350414C) cpal = offset; // "CPAL"
                }
                if (colr == 0 || cpal == 0) return false;

                file.Position = colr + 2; // after the version
                int baseCount = U16();
                uint baseOffset = U32(), layerOffset = U32();
                int layerCount = U16();
                bases = new Dictionary<ushort, (int, int)>(baseCount);
                file.Position = colr + baseOffset;
                for (int i = 0; i < baseCount; i++) bases[U16()] = (U16(), U16());
                layers = new (ushort, ushort)[layerCount];
                file.Position = colr + layerOffset;
                for (int i = 0; i < layerCount; i++) layers[i] = (U16(), U16());

                file.Position = cpal + 2;
                int entries = U16();
                U16(); // palettes
                U16(); // colour records
                uint recordsOffset = U32();
                int firstOfPalette0 = U16();
                colors = new Brush[entries];
                file.Position = cpal + recordsOffset + 4 * firstOfPalette0;
                for (int i = 0; i < entries; i++)
                {
                    var bgra = r.ReadBytes(4);
                    var brush = new SolidColorBrush(Color.FromArgb(bgra[3], bgra[2], bgra[1], bgra[0]));
                    brush.Freeze();
                    colors[i] = brush;
                }
                return bases.Count > 0;
            }
            catch
            {
                return false; // no colour then: the plain rendering is still there
            }
        }
    }
}
