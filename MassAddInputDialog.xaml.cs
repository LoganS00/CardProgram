using System.Windows;
using System.Windows.Input;

namespace CardProgram
{
    public partial class MassAddInputDialog : Window
    {
        public string CardList { get; private set; } = string.Empty;

        public MassAddInputDialog() => InitializeComponent();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            CardList = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(CardList)) return;
            DialogResult = true;
            Close();
        }
    }
}
