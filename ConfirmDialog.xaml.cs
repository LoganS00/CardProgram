using System.Windows;

namespace CardProgram
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog(string title, string message, string confirmLabel = "Delete")
        {
            InitializeComponent();
            TitleBlock.Text = title;
            MessageBlock.Text = message;
            YesBtn.Content = confirmLabel;
        }

        private void Yes_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }
        private void No_Click(object sender, RoutedEventArgs e) => Close();
    }
}
