using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CardProgram.Models;

namespace CardProgram
{
    public partial class CardViewerWindow : Window
    {
        public bool Deleted { get; private set; }
        public bool CardChanged { get; private set; }
        private readonly Card _card;
        private readonly List<Models.Folder> _folders;
        private readonly Func<string, Task<(double? market, double? low)?>>? _fetchPrice;
        private readonly Action? _saveCallback;
        private bool _loaded;
        private int _rangeDays = 0; // 0 = show all history
        private System.Windows.Threading.DispatcherTimer? _priceTimer;

        public CardViewerWindow(Card card, BitmapImage image, List<Models.Folder> folders,
            Func<string, Task<(double? market, double? low)?>>? fetchPrice = null, Action? saveCallback = null)
        {
            InitializeComponent();
            _card         = card;
            _folders      = folders;
            _fetchPrice   = fetchPrice;
            _saveCallback = saveCallback;

            TitleBlock.Text = card.Name;
            SetBlock.Text = card.TcgPlayerSetName;
            CardImage.Source = image;

            MarketBlock.Text = card.MarketPrice.HasValue ? $"${card.MarketPrice:F2}" : "—";
            LowBlock.Text    = card.LowPrice.HasValue    ? $"${card.LowPrice:F2}"    : "—";
            UpdatedBlock.Text = card.PriceUpdatedAt.HasValue
                ? card.PriceUpdatedAt.Value.ToString("MMM d, yyyy\nh:mm tt")
                : "Never";

            if (!string.IsNullOrWhiteSpace(card.Notes))
                NotesBlock.Text = card.Notes;
            DateBlock.Text = $"Added {card.CapturedAt:MMM d, yyyy}";

            var displayHistory = BuildDisplayHistory(card);
            if (displayHistory.Count > 0)
            {
                GraphBorder.Visibility = Visibility.Visible;
                GraphCurrentBlock.Text = $"${displayHistory.Last().Price:F2}";
                GraphHighBlock.Text    = $"${displayHistory.Max(p => p.Price):F2}";
                GraphLowBlock.Text     = $"${displayHistory.Min(p => p.Price):F2}";
            }

            QtyBlock.Text = card.Quantity.ToString();

            // Set card type radio button
            switch (card.CardType)
            {
                case "Foil":     TypeFoil.IsChecked    = true; break;
                case "Damaged":  TypeDamaged.IsChecked = true; break;
                default:         TypeNonFoil.IsChecked = true; break;
            }

            if (!_card.IsLinked || _fetchPrice == null)
                RefreshPriceBtn.Visibility = Visibility.Collapsed;

            Loaded += (_, _) =>
            {
                _loaded = true;
                UpdateRangeButtons();
                DrawGraph();
                StartPriceAutoRefresh();
            };
            Closed += (_, _) => _priceTimer?.Stop();
        }

        private void StartPriceAutoRefresh()
        {
            if (_fetchPrice == null || !_card.IsLinked) return;

            _priceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(30)
            };
            _priceTimer.Tick += async (_, _) => await ApplyPriceUpdateAsync();
            _priceTimer.Start();
        }

        private async Task ApplyPriceUpdateAsync()
        {
            if (_fetchPrice == null) return;
            try
            {
                var price = await _fetchPrice(_card.TcgPlayerUrl);
                if (price?.market.HasValue == true)
                {
                    _card.RecordPrice(price.Value.market.Value);
                    if (price.Value.low.HasValue) _card.LowPrice = price.Value.low;
                    CardChanged = true;
                    _saveCallback?.Invoke();

                    MarketBlock.Text  = $"${_card.MarketPrice:F2}";
                    LowBlock.Text     = _card.LowPrice.HasValue ? $"${_card.LowPrice:F2}" : "—";
                    UpdatedBlock.Text = _card.PriceUpdatedAt?.ToString("MMM d, yyyy\nh:mm tt") ?? "Never";
                    if (GraphBorder.Visibility != Visibility.Visible)
                        GraphBorder.Visibility = Visibility.Visible;
                    DrawGraph();
                }
            }
            catch { }
        }

        // ── Price graph ───────────────────────────────────────────────────────

        private List<Models.PricePoint> BuildDisplayHistory(Card card)
        {
            var history = card.PriceHistory.OrderBy(p => p.Date).ToList();
            if (history.Count == 0 && card.MarketPrice.HasValue)
            {
                // Seed: show add-date as first point, updated-date as latest
                history.Add(new Models.PricePoint { Date = card.CapturedAt, Price = card.MarketPrice.Value });
                if (card.PriceUpdatedAt.HasValue && card.PriceUpdatedAt.Value > card.CapturedAt.AddMinutes(1))
                    history.Add(new Models.PricePoint { Date = card.PriceUpdatedAt.Value, Price = card.MarketPrice.Value });
            }
            return history;
        }

        private void RangeBtn_Click(object sender, RoutedEventArgs e)
        {
            _rangeDays = int.Parse((string)((System.Windows.Controls.Button)sender).Tag);
            UpdateRangeButtons();
            DrawGraph();
        }

        private void UpdateRangeButtons()
        {
            var active   = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x6a));
            var inactive = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35));
            RangeAll.Background = _rangeDays == 0   ? active : inactive;
            Range1W.Background  = _rangeDays == 7   ? active : inactive;
            Range1M.Background  = _rangeDays == 30  ? active : inactive;
            Range3M.Background  = _rangeDays == 90  ? active : inactive;
            Range6M.Background  = _rangeDays == 180 ? active : inactive;
            Range1Y.Background  = _rangeDays == 365 ? active : inactive;
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

        private void DrawGraph()
        {
            GraphCanvas.Children.Clear();

            var all     = BuildDisplayHistory(_card);
            var history = _rangeDays == 0
                ? all
                : all.Where(p => p.Date >= DateTime.Now.AddDays(-_rangeDays)).ToList();
            if (history.Count == 0) history = all;

            // Update stats to reflect selected range
            if (history.Count > 0)
            {
                GraphCurrentBlock.Text = $"${history.Last().Price:F2}";
                GraphHighBlock.Text    = $"${history.Max(p => p.Price):F2}";
                GraphLowBlock.Text     = $"${history.Min(p => p.Price):F2}";
            }

            // Duplicate single point so range math works (flat line)
            if (history.Count == 1)
                history.Add(new Models.PricePoint { Date = history[0].Date.AddDays(1), Price = history[0].Price });

            double w = GraphCanvas.ActualWidth;
            double h = GraphCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double minPrice = history.Min(p => p.Price) * 0.9;
            double maxPrice = history.Max(p => p.Price) * 1.1;
            double priceRange = maxPrice - minPrice;
            if (priceRange < 0.01) priceRange = 1;

            double minTime = history.First().Date.Ticks;
            double maxTime = history.Last().Date.Ticks;
            double timeRange = maxTime - minTime;
            if (timeRange < 1) timeRange = 1;

            // Grid lines
            for (int i = 0; i <= 4; i++)
            {
                double y = h * i / 4.0;
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x44)),
                    StrokeThickness = 1
                };
                GraphCanvas.Children.Add(line);

                double priceAtLine = maxPrice - (priceRange * i / 4.0);
                var label = new TextBlock
                {
                    Text = $"${priceAtLine:F2}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77)),
                    FontSize = 9
                };
                Canvas.SetLeft(label, 2);
                Canvas.SetTop(label, y + 1);
                GraphCanvas.Children.Add(label);
            }

            // Build the polyline points
            var points = new PointCollection();
            foreach (var pt in history)
            {
                double x = (pt.Date.Ticks - minTime) / timeRange * w;
                double y = (1.0 - (pt.Price - minPrice) / priceRange) * h;
                points.Add(new System.Windows.Point(x, y));
            }

            // Shaded area under the line
            var area = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x55, 0xcc, 0x88)),
                StrokeThickness = 0
            };
            area.Points.Add(new System.Windows.Point(points[0].X, h));
            foreach (var p in points) area.Points.Add(p);
            area.Points.Add(new System.Windows.Point(points[^1].X, h));
            GraphCanvas.Children.Add(area);

            // Line
            var polyline = new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            GraphCanvas.Children.Add(polyline);

            // Dots at each data point
            foreach (var pt in points)
            {
                var dot = new Ellipse
                {
                    Width = 7, Height = 7,
                    Fill = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(dot, pt.X - 3.5);
                Canvas.SetTop(dot, pt.Y - 3.5);
                GraphCanvas.Children.Add(dot);
            }

            // Date labels at first and last point
            void AddDateLabel(System.Windows.Point pt, DateTime date, bool rightAlign)
            {
                var lbl = new TextBlock
                {
                    Text = date.ToString("MMM d"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77)),
                    FontSize = 9
                };
                double left = rightAlign ? pt.X - 36 : pt.X + 2;
                Canvas.SetLeft(lbl, left);
                Canvas.SetTop(lbl, h - 14);
                GraphCanvas.Children.Add(lbl);
            }
            AddDateLabel(points[0], history[0].Date, false);
            AddDateLabel(points[^1], history[^1].Date, true);
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private async void RefreshPrice_Click(object sender, RoutedEventArgs e)
        {
            if (_fetchPrice == null || !_card.IsLinked) return;
            RefreshPriceBtn.IsEnabled = false;
            RefreshPriceBtn.Content   = "↻  Refreshing…";
            try
            {
                var price = await _fetchPrice(_card.TcgPlayerUrl);
                if (price?.market.HasValue == true)
                {
                    _card.RecordPrice(price.Value.market.Value);
                    if (price.Value.low.HasValue) _card.LowPrice = price.Value.low;
                    CardChanged = true;
                    _saveCallback?.Invoke();

                    MarketBlock.Text  = $"${_card.MarketPrice:F2}";
                    LowBlock.Text     = _card.LowPrice.HasValue ? $"${_card.LowPrice:F2}" : "—";
                    UpdatedBlock.Text = _card.PriceUpdatedAt?.ToString("MMM d, yyyy\nh:mm tt") ?? "Never";

                    if (GraphBorder.Visibility != Visibility.Visible)
                        GraphBorder.Visibility = Visibility.Visible;
                    DrawGraph();
                    RefreshPriceBtn.Content = "✓  Updated";
                }
                else
                {
                    RefreshPriceBtn.Content = "✗  No price found";
                }
            }
            catch
            {
                RefreshPriceBtn.Content = "✗  Failed";
            }

            await System.Threading.Tasks.Task.Delay(2000);
            RefreshPriceBtn.Content   = "↻  Refresh Price";
            RefreshPriceBtn.IsEnabled = true;
        }

        private void QtyMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_card.Quantity <= 1) return;
            _card.Quantity--;
            QtyBlock.Text = _card.Quantity.ToString();
            CardChanged = true;
        }

        private void QtyPlus_Click(object sender, RoutedEventArgs e)
        {
            _card.Quantity++;
            QtyBlock.Text = _card.Quantity.ToString();
            CardChanged = true;
        }

        private void CardType_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _card.CardType = TypeFoil.IsChecked == true ? "Foil"
                           : TypeDamaged.IsChecked == true ? "Damaged"
                           : "Non-Foil";
            CardChanged = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog(
                "Delete Card",
                $"Remove \"{_card.Name}\" from your collection? This cannot be undone.",
                "Delete");
            dlg.Owner = this;
            dlg.ShowDialog();
            if (dlg.Confirmed) { Deleted = true; Close(); }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}
