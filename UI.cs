using System.Windows;

namespace Hadaf
{
    /// <summary>Attached properties shared by control templates.</summary>
    public static class UI
    {
        /// <summary>Corner radius for templated controls (Button, TextBox) that have no CornerRadius of their own.</summary>
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(CornerRadius), typeof(UI), new FrameworkPropertyMetadata(new CornerRadius(8)));

        public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject o, CornerRadius value) => o.SetValue(RadiusProperty, value);
    }
}
