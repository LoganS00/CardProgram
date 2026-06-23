using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CardProgram.Models;
using CardProgram.Services;
using Microsoft.Win32;

namespace CardProgram
{
    public partial class SnipForCardWindow : Window
    {
        public Card? ResultCard { get; private set; }
        public bool HasTcgImage { get; }

        private BitmapSource? _cardImage;
        private readonly ScreenCaptureService _capture = new();
        private readonly CardStorageService _storage = new();
        private readonly double? _price;
        private readonly string _tcgUrl;

        public SnipForCardWindow(string name, string set, double? price, string tcgImageUrl, string tcgUrl)
        {
            InitializeComponent();
            DataContext = this;

            NameBox.Text = name;
            SetBox.Text = set;
            PriceBox.Text = price.HasValue ? price.Value.ToString("F2") : string.Empty;
            _price = price;
            _tcgUrl = tcgUrl;

            if (!string.IsNullOrWhiteSpace(tcgImageUrl))
            {
                HasTcgImage = true;
                try
                {
                    var img = new BitmapImage(new Uri(tcgImageUrl));
                    TcgPreview.Source = img;
                }
                catch { HasTcgImage = false; }
            }
        }

        private void Snip_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            System.Threading.Thread.Sleep(300);

            var overlay = new RegionSelectOverlay();
            overlay.ShowDialog();

            Show();
            Activate();

            if (!overlay.Confirmed) return;

            var r = overlay.SelectedRegion;
            _cardImage = _capture.CaptureRegion(r.X, r.Y, r.Width, r.Height);
            if (_cardImage != null)
            {
                SnipPreview.Source = _cardImage;
                SnipPreviewBorder.Visibility = Visibility.Visible;
            }
        }

        private void File_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Choose card image"
            };
            if (dlg.ShowDialog() != true) return;

            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(dlg.FileName);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            _cardImage = img;
            SnipPreview.Source = _cardImage;
            SnipPreviewBorder.Visibility = Visibility.Visible;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
            if (_cardImage == null)
            {
                MessageBox.Show("Please snip a screenshot or choose an image file for this card.",
                    "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double.TryParse(PriceBox.Text, out double parsedPrice);

            var card = new Card
            {
                Name = NameBox.Text.Trim(),
                TcgPlayerSetName = SetBox.Text.Trim(),
                TcgPlayerUrl = _tcgUrl,
                MarketPrice = parsedPrice > 0 ? parsedPrice : _price,
                PriceUpdatedAt = DateTime.Now,
                CapturedAt = DateTime.Now,
            };
            card.ImageFileName = _storage.SaveCardImage(_cardImage, card.Id);

            ResultCard = card;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
