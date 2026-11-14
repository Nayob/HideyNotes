using System.Windows;
using System.Windows.Input;

namespace HideyNotes.Windows
{
    public partial class HandleWindow : Window
    {
        public HandleWindow()
        {
            InitializeComponent();
        }

        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            App.Current.Controller?.Toggle();
            e.Handled = true;
        }

        /// <summary>
        /// Mirrors the tab for the chosen screen edge: the rounded side always faces away from it,
        /// so the tab looks glued to the monitor border on either side.
        /// </summary>
        public void SetOnLeft(bool onLeft)
        {
            Tab.HorizontalAlignment = onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Tab.CornerRadius = onLeft ? new CornerRadius(0, 9, 9, 0) : new CornerRadius(9, 0, 0, 9);
            Tab.BorderThickness = onLeft ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
        }

        public void SetOpen(bool open)
        {
            // The tab has no glyph anymore; its state is implied by the panel.
        }
    }
}
