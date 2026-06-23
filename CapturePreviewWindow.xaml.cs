using System.Windows;
using System.Windows.Media.Imaging;

namespace CardProgram
{
    public partial class CapturePreviewWindow : Window
    {
        public string CardName => CardNameBox.Text.Trim();
        public string Notes => NotesBox.Text.Trim();
        public bool Saved { get; private set; }

        public CapturePreviewWindow(BitmapSource image)
        {
            InitializeComponent();
            PreviewImage.Source = image;
            CardNameBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CardNameBox.Text))
            {
                CardNameBox.Focus();
                return;
            }
            Saved = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
