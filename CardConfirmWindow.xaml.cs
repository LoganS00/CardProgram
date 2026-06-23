using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CardProgram.Services;

namespace CardProgram
{
    public enum ConfirmAction { Saved, Skipped, Cancelled }

    public partial class CardConfirmWindow : Window
    {
        public ConfirmAction Action { get; private set; } = ConfirmAction.Cancelled;
        public string CardName => CardNameBox.Text.Trim();
        public string Notes => NotesBox.Text.Trim();
        public TcgPlayerScraperResult? SelectedResult { get; private set; }

        private readonly TcgPlayerScraperService _scraper;
        private TcgPlayerScraperResult? _currentMatch;

        public CardConfirmWindow(BitmapSource capture, TcgPlayerScraperResult? match,
                                 string detectedName, TcgPlayerScraperService scraper)
        {
            InitializeComponent();
            _scraper = scraper;
            _currentMatch = match;

            CaptureImage.Source = capture;
            CardNameBox.Text = detectedName;

            if (match != null)
            {
                SubLabel.Text = "Here's the closest TCGPlayer match. Confirm or search again if it's wrong.";
                ShowMatch(match);
            }
            else
            {
                SubLabel.Text = "Type the card name below to search TCGPlayer for the price.";
                InlineSearchBox.Text = detectedName;
                ShowNoMatch();
                Loaded += (_, _) => InlineSearchBox.Focus();
            }
        }

        private void ShowMatch(TcgPlayerScraperResult match)
        {
            LoadingLabel.Visibility = Visibility.Collapsed;
            NoMatchPanel.Visibility = Visibility.Collapsed;
            MatchFoundPanel.Visibility = Visibility.Visible;

            MatchName.Text = match.Name;
            MatchSet.Text = match.SetName;
            MatchPrice.Text = match.MarketPrice.HasValue ? $"Market: ${match.MarketPrice:F2}" : "Price not listed";

            // Auto-fill the card name with the full market listing name
            CardNameBox.Text = match.Name;

            if (!string.IsNullOrWhiteSpace(match.ImageUrl))
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(match.ImageUrl);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    TcgImage.Source = img;
                }
                catch { TcgImage.Visibility = Visibility.Collapsed; }
            }
            else
            {
                TcgImage.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowNoMatch()
        {
            LoadingLabel.Visibility = Visibility.Collapsed;
            MatchFoundPanel.Visibility = Visibility.Collapsed;
            NoMatchPanel.Visibility = Visibility.Visible;
        }

        private void ShowLoading()
        {
            NoMatchPanel.Visibility = Visibility.Collapsed;
            MatchFoundPanel.Visibility = Visibility.Collapsed;
            LoadingLabel.Visibility = Visibility.Visible;
        }

        private async void RunInlineSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            ShowLoading();
            try
            {
                var results = await _scraper.SearchAsync(query);
                if (results.Count > 0)
                {
                    _currentMatch = results[0];
                    ShowMatch(_currentMatch); // auto-fills CardNameBox with full market name
                }
                else
                {
                    ShowNoMatch();
                }
            }
            catch
            {
                ShowNoMatch();
            }
        }

        private void InlineSearch_Click(object sender, RoutedEventArgs e)
            => RunInlineSearch(InlineSearchBox.Text.Trim());

        private void InlineSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) RunInlineSearch(InlineSearchBox.Text.Trim());
        }

        private void SearchAgain_Click(object sender, RoutedEventArgs e)
        {
            _currentMatch = null;
            InlineSearchBox.Text = CardNameBox.Text;
            ShowNoMatch();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CardNameBox.Text)) { CardNameBox.Focus(); return; }
            SelectedResult = _currentMatch;
            Action = ConfirmAction.Saved;
            Close();
        }

        private void SkipPricing_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CardNameBox.Text)) { CardNameBox.Focus(); return; }
            SelectedResult = null;
            Action = ConfirmAction.Skipped;
            Close();
        }
    }
}
