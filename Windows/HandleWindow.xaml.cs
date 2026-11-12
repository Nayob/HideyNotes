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

        public void SetOpen(bool open)
        {
            // The tab has no glyph anymore; its state is implied by the panel.
        }
    }
}
