using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hadaf
{
    // The settings page, including the category editor and the accent colour.
    public partial class MainWindow
    {
        // ================= settings page =================

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
            HideSnack();
            SyncSettingsUi();
            FillCategoryEditor();
            ShowPage(SettingsPage);
        }

        void SettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

        void CloseSettings()
        {
            // A category name still being typed (Esc or Back pressed in the box) counts too.
            if (Keyboard.FocusedElement is TextBox { DataContext: CatRow } box) CommitCategoryName(box);
            Save();
            ShowPage(HomePage);
        }

        void SyncSettingsUi()
        {
            suppress = true;
            LangChips.SelectedValue = S.Lang;
            ThemeChips.SelectedValue = S.Theme;
            FxChips.SelectedValue = S.Effect;
            AccentChips.SelectedValue = Palette.TryParse(S.Accent, out _) ? S.Accent.ToUpperInvariant() : "system";
            AccentName.Text = (AccentChips.SelectedItem as Opt)?.Name;
            OpacitySlider.Value = S.Transparency;
            OpacitySlider.IsEnabled = Theme.GlassSupported && S.Effect != "solid";
            GlassNote.Visibility = Theme.GlassSupported ? Visibility.Collapsed : Visibility.Visible;
            NotifySwitch.IsChecked = S.Notifications;
            TraySwitch.IsChecked = S.CloseToTray;
            StartupSwitch.IsChecked = AutoStart.IsEnabled();
            UpdateBackupInfo();
            suppress = false;
        }

        void SettingChip_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || ((ListBox)sender).SelectedValue is not string v) return;
            if (sender == LangChips)
            {
                if (v == S.Lang) return;
                S.Lang = v;
                ApplyLanguage();
            }
            else
            {
                if (sender == ThemeChips) S.Theme = v;
                else S.Effect = v;
                Theme.Apply(this, S);
                RefreshCards();
                FillAccentChips();
                SyncSettingsUi();
            }
            Save();
        }

        /// <summary>The accent choices, each shown as the app will use it in the current theme.</summary>
        void FillAccentChips()
        {
            Opt Swatch(string key, string name, Color c) => new() { Key = key, Name = name, Brush = new SolidColorBrush(Theme.Legible(c)) };
            suppress = true;
            AccentChips.ItemsSource = new[] { Swatch("system", L["accent_system"], Theme.SystemAccent) }
                .Concat(Palette.Swatches.Select(s => Swatch(Palette.Hex(s.Argb), L["col_" + s.Key],
                    Color.FromRgb((byte)(s.Argb >> 16), (byte)(s.Argb >> 8), (byte)s.Argb))))
                .ToList();
            suppress = false;
        }

        void Accent_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (suppress || AccentChips.SelectedValue is not string v) return;
            S.Accent = v;
            Theme.Apply(this, S);
            RefreshCards();
            SyncSettingsUi();
            Save();
        }

        void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppress || !IsInitialized) return;
            S.Transparency = e.NewValue;
            Theme.Apply(this, S);
        }

        void Switch_Click(object sender, RoutedEventArgs e)
        {
            bool on = ((CheckBox)sender).IsChecked == true;
            if (sender == NotifySwitch) S.Notifications = on;
            else if (sender == TraySwitch) S.CloseToTray = on;
            else if (sender == StartupSwitch)
            {
                S.StartupAsked = true;
                if (noticeKey == "startup") HideNotice();
                try { AutoStart.Set(on); } catch { StartupSwitch.IsChecked = AutoStart.IsEnabled(); }
            }
            Save();
        }

        // ---- categories ----

        /// <summary>The menu's category entries and the editor's category chips.</summary>
        void FillCategoryLists()
        {
            cats.Clear();
            cats.AddRange(Categories.All.Select(c =>
                new NavItem { Key = c.Id, IsCat = true, Brush = Categories.BrushOf(c.Id), Name = Categories.NameOf(c) }));
            suppress = true;
            CatList.ItemsSource = null;
            CatList.ItemsSource = cats;
            CatList.SelectedItem = cats.FirstOrDefault(c => c.Key == cat);
            suppress = false;
            UpdateCounts();

            // Detached while rebuilt, for the same reason as the other pickers (see ApplyLanguage).
            var editing = EditorForm.DataContext;
            syncing = true;
            EditorForm.DataContext = null;
            CatChips.ItemsSource = Categories.All
                .Select(c => new Opt { Key = c.Id, Name = Categories.NameOf(c), Brush = Categories.BrushOf(c.Id) }).ToList();
            EditorForm.DataContext = editing;
            syncing = false;
        }

        void FillCategoryEditor()
        {
            var colors = Palette.Swatches
                .Select(s => new Opt { Key = Palette.Hex(s.Argb), Name = L["col_" + s.Key], Brush = Palette.B(s.Argb) }).ToList();
            CatEditor.ItemsSource = Categories.All.Select(c => new CatRow { Def = c, Colors = colors }).ToList();
            AddCatBtn.Visibility = Categories.All.Count < Categories.Max ? Visibility.Visible : Visibility.Collapsed;
            CatDesc.Text = L.F("cat_desc", Categories.NameOf(Categories.Fallback));
        }

        /// <summary>
        /// After a category was added, renamed, recoloured or deleted. A rename or new colour only refreshes its own
        /// editor row, so a click already on its way to that row isn't lost to a rebuilt list.
        /// </summary>
        void CategoriesChanged(CatRow changed = null)
        {
            if (cat != null && !Categories.Exists(cat))
            {
                cat = null;
                view = "all";
                suppress = true;
                NavList.SelectedIndex = 0;
                suppress = false;
            }
            FillCategoryLists();
            if (changed != null) changed.RefreshAll();
            else FillCategoryEditor();
            RefreshCards();
            Refresh();
            Save();
        }

        void CatColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CatRow row) row.Picking = !row.Picking;
        }

        void CatSwatch_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox { DataContext: CatRow row, SelectedValue: string hex }
                || string.Equals(hex, row.Def.Color, StringComparison.OrdinalIgnoreCase)) return;
            row.Def.Color = hex;
            row.Picking = false;
            CategoriesChanged(row);
        }

        void CatName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitCategoryName((TextBox)sender);
            e.Handled = true;
        }

        void CatName_LostFocus(object sender, RoutedEventArgs e) => CommitCategoryName((TextBox)sender);

        /// <summary>Emptying a built-in category's name (or typing its own) gives it back its name in the current language.</summary>
        void CommitCategoryName(TextBox box)
        {
            if (box.DataContext is not CatRow row) return;
            var def = row.Def;
            var text = box.Text.Trim();
            bool builtIn = Categories.IsBuiltIn(def.Id);
            string name = text.Length == 0 || builtIn && text == Categories.DefaultName(def.Id) ? null : text;
            if (name == null && !builtIn) name = def.Name; // an added category always keeps a name
            if (name != def.Name)
            {
                def.Name = name;
                CategoriesChanged(row);
            }
            box.SetCurrentValue(TextBox.TextProperty, row.Name); // the name in effect, e.g. the built-in one again
        }

        void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            if (Categories.All.Count >= Categories.Max) return;
            var used = Categories.All.Select(c => c.Color?.ToUpperInvariant()).ToHashSet();
            var color = Palette.Swatches.Select(s => Palette.Hex(s.Argb)).FirstOrDefault(h => !used.Contains(h))
                        ?? Palette.Hex(Palette.Swatches[0].Argb);
            var def = new CategoryDef { Id = "c" + Guid.NewGuid().ToString("N")[..8], Name = L["new_category"], Color = color };
            int fallback = S.Categories.FindIndex(c => c.Id == Categories.Fallback);
            S.Categories.Insert(fallback >= 0 ? fallback : S.Categories.Count, def); // "Other" stays last
            CategoriesChanged();
            // Straight into naming it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = CatEditor.ItemContainerGenerator.ContainerFromIndex(S.Categories.IndexOf(def));
                if (FindChild<TextBox>(row) is TextBox box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Its goals move to the fallback category; Undo puts both back.</summary>
        void CatDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CatRow { CanDelete: true } row) return;
            var def = row.Def;
            int index = S.Categories.IndexOf(def);
            var moved = data.Goals.Where(g => g.Category == def.Id).ToList();
            S.Categories.Remove(def);
            foreach (var g in moved) g.Category = Categories.Fallback;
            CategoriesChanged();
            ShowSnack(L.F("cat_deleted", Categories.NameOf(def)), L["undo"], () =>
            {
                if (Categories.Exists(def.Id)) return;
                S.Categories.Insert(Math.Min(index, S.Categories.Count), def);
                foreach (var g in moved.Where(g => g.Category == Categories.Fallback)) g.Category = def.Id;
                CategoriesChanged();
            });
        }

        static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FindChild<T>(child) is T deeper) return deeper;
            }
            return null;
        }
    }
}
