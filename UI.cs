using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Quest
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

    /// <summary>
    /// A CheckBox whose app logic runs in Click. Screen readers and voice control toggle it through UI Automation,
    /// which never raises Click; this raises it in that case too, so they can tick goals like everyone else.
    /// </summary>
    public class ActionCheckBox : CheckBox
    {
        bool inClick;

        protected override void OnClick()
        {
            inClick = true;
            try { base.OnClick(); }
            finally { inClick = false; }
        }

        protected override void OnToggle()
        {
            base.OnToggle();
            if (!inClick) RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        }
    }

    /// <summary>
    /// The goal list. WPF's stock list peer keeps one entry per goal across view switches and goes on
    /// showing screen readers the old, discarded contents of the cards it reused; this one mirrors the cards on screen.
    /// </summary>
    public class CardList : ItemsControl
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, AutomationControlType.List);
    }

    /// <summary>A goal card: one list item for screen readers, named after the goal, so focus reads its title.</summary>
    public class Card : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, AutomationControlType.ListItem);
    }

    class Peer : FrameworkElementAutomationPeer
    {
        readonly AutomationControlType type;

        public Peer(FrameworkElement owner, AutomationControlType type) : base(owner) => this.type = type;

        protected override AutomationControlType GetAutomationControlTypeCore() => type;
        protected override string GetClassNameCore() => Owner.GetType().Name;
    }
}
