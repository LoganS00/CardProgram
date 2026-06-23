using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CardProgram.Services;

namespace CardProgram
{
    public partial class TcgPlayerPickerWindow : Window
    {
        private readonly TcgPlayerScraperService _scraper;
        public TcgPlayerScraperResult? SelectedResult { get; private set; }

        public TcgPlayerPickerWindow(TcgPlayerScraperService scraper, string initialSearch)
        {
            InitializeComponent();
            _scraper = scraper;
            SearchBox.Text = initialSearch;
            Loaded += async (_, _) => await RunSearchAsync(initialSearch);
        }

        private async System.Threading.Tasks.Task RunSearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            StatusLabel.Text = "Searching TCGPlayer…";
            ResultsPanel.Children.Clear();
            try
            {
                var results = await _scraper.SearchAsync(query);
                StatusLabel.Text = results.Count == 0 ? "No results found." : $"{results.Count} result(s)";
                BuildResults(results);
            }
            catch
            {
                StatusLabel.Text = "Search failed — check your internet connection.";
            }
        }

        private void BuildResults(List<TcgPlayerScraperResult> products)
        {
            ResultsPanel.Children.Clear();
            foreach (var p in products)
            {
                var btn = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x3e)),
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = p
                };

                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x3e)));
                border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x6a)));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
                border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                template.VisualTree = border;
                btn.Template = template;

                btn.Click += (_, _) => { SelectedResult = p; Close(); };

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = p.Name,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                });

                var sub = new StackPanel { Orientation = Orientation.Horizontal };
                if (!string.IsNullOrWhiteSpace(p.SetName))
                    sub.Children.Add(new TextBlock
                    {
                        Text = p.SetName,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xcc)),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                if (p.MarketPrice.HasValue)
                    sub.Children.Add(new TextBlock
                    {
                        Text = $"Market: ${p.MarketPrice:F2}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    });

                sp.Children.Add(sub);
                btn.Content = sp;
                ResultsPanel.Children.Add(btn);
            }
        }

        private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync(SearchBox.Text);
        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await RunSearchAsync(SearchBox.Text);
        }
        private void Skip_Click(object sender, RoutedEventArgs e) => Close();
    }
}
