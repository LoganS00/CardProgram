using System.Windows;

namespace CardProgram
{
    public partial class SettingsWindow : Window
    {
        public string PublicKey => PublicKeyBox.Text.Trim();
        public string PrivateKey => PrivateKeyBox.Password.Trim();
        public bool Saved { get; private set; }

        public SettingsWindow(string currentPublic, string currentPrivate)
        {
            InitializeComponent();
            PublicKeyBox.Text = currentPublic;
            PrivateKeyBox.Password = currentPrivate;
        }

        private void Save_Click(object sender, RoutedEventArgs e) { Saved = true; Close(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
