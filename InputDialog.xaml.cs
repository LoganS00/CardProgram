using System.Windows;
using System.Windows.Input;

namespace CardProgram
{
    public partial class InputDialog : Window
    {
        public string Value { get; private set; } = string.Empty;

        public InputDialog(string title, string prompt, string initial = "")
        {
            InitializeComponent();
            Title = title;
            PromptBlock.Text = prompt;
            InputBox.Text = initial;
            Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            Value = InputBox.Text;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)  { Value = InputBox.Text; Close(); }
            if (e.Key == Key.Escape) Close();
        }
    }
}
