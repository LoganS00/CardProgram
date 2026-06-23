using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CardProgram.Models;
using CardProgram.Services;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace CardProgram
{
    public partial class MainWindow : Window
    {
        private readonly CardStorageService _storage = new();
        private readonly TcgPlayerScraperService _scraper = new();
        private readonly ScreenCaptureService _capture = new();
        private static readonly HttpClient _http = new();
        private List<Card> _allCards = new();
        private List<Models.Folder> _folders = new();
        private List<Models.WatchedCard> _watchlist = new();
        private string? _activeFolderId = null; // null = All Cards

        private bool _browserInitialized;
        private string _pendingImageUrl = string.Empty;
        private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
        private System.Windows.Point _dragStart;
        private bool _isDragging;

        private const string TcgStartUrl =
            "https://www.tcgplayer.com/search/all/product?view=grid";

        // Reads card name (from page title), set, price, and card image URL
        private const string ExtractScript = @"
(function() {
    // Page title is the most reliable source — e.g. ""Charlotte Katakuri (SP) - The Time of Battle (OP16) | TCGPlayer""
    var title = document.title || '';
    var name = title.includes('|') ? title.split('|')[0].trim() : '';

    // Fallback to h1 if title doesn't look like a card name
    if (!name || name.length < 3) {
        var h1 = document.querySelector('h1');
        if (h1) name = h1.innerText.trim();
    }

    // Set name
    var setEl = document.querySelector('[class*=""set-name""]')
              || document.querySelector('[class*=""product-details__set""]')
              || document.querySelector('[data-testid=""set-name""]');
    var set = setEl ? setEl.innerText.trim() : '';

    // Market price — target Near Mint Foil first, fall back to Near Mint
    var price = null;
    var allText = document.body ? document.body.innerText : '';

    // Try Near Mint Foil market price
    var nmFoilMatch = allText.match(/Near\s+Mint\s+Foil[\s\S]{0,200}?Market Price\s*\$?([\d,]+\.?\d{2})/i)
                   || allText.match(/Near\s+Mint\s+Foil[\s\S]{0,80}?\$([\d,]+\.?\d{2})/i);
    if (nmFoilMatch) {
        price = parseFloat(nmFoilMatch[1].replace(',', ''));
    } else {
        // Fall back to Near Mint market price
        var nmMatch = allText.match(/Near\s+Mint\b(?!\s+Foil)[\s\S]{0,200}?Market Price\s*\$?([\d,]+\.?\d{2})/i)
                   || allText.match(/Market Price\s*\n?\s*\$?([\d,]+\.?\d{2})/i);
        if (nmMatch) price = parseFloat(nmMatch[1].replace(',', ''));
    }

    if (!price) {
        var priceEl = document.querySelector('[class*=""spotlight""] [class*=""price""]')
                    || document.querySelector('[class*=""market-price""]');
        if (priceEl) {
            var m = priceEl.innerText.match(/\$?([\d,]+\.?\d{2})/);
            if (m) price = parseFloat(m[1].replace(',', ''));
        }
    }

    // Card image — try many selectors to cover all TCG layouts
    var imgEl = document.querySelector('.product-gallery__image img')
              || document.querySelector('[class*=""product-image""] img')
              || document.querySelector('[class*=""gallery""] img')
              || document.querySelector('[class*=""ProductDetails""] img')
              || document.querySelector('[class*=""product-details""] img')
              || document.querySelector('img[alt*=""card""]')
              || document.querySelector('img[src*=""product-images.tcgplayer""]')
              || document.querySelector('img[src*=""tcgplayer.com""]');
    var imageUrl = imgEl ? imgEl.src : '';
    if (imageUrl) {
        // Try srcset first (highest resolution descriptor wins)
        if (imgEl.srcset) {
            var parts = imgEl.srcset.split(',').map(function(s) { return s.trim().split(/\s+/); });
            parts.sort(function(a, b) { return parseFloat(b[1]) - parseFloat(a[1]); });
            if (parts.length > 0 && parts[0][0]) imageUrl = parts[0][0];
        }
        // Remove fit-in size constraint from TCGPlayer CDN URLs to get full resolution
        imageUrl = imageUrl.replace(/\/fit-in\/\d+x\d+/, '');
    }
    // Also try og:image meta tag as fallback
    if (!imageUrl) {
        var og = document.querySelector('meta[property=""og:image""]');
        if (og) imageUrl = og.getAttribute('content') || '';
    }

    return JSON.stringify({ name: name, set: set, price: price, imageUrl: imageUrl });
})();";

        public MainWindow()
        {
            InitializeComponent();
            LoadCollection();
            StartAutoRefresh();
            SourceInitialized += (_, _) =>
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)
                           .AddHook(WndProc);
            Loaded += (_, _) => _ = RefreshAllPricesOnStartupAsync();
        }

        // Prevent borderless maximized window from covering the taskbar
        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var area = SystemParameters.WorkArea;
                mmi.ptMaxPosition.x = (int)area.Left;
                mmi.ptMaxPosition.y = (int)area.Top;
                mmi.ptMaxSize.x     = (int)area.Width;
                mmi.ptMaxSize.y     = (int)area.Height;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void StartAutoRefresh()
        {
            _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1)
            };
            _autoRefreshTimer.Tick += async (_, _) =>
            {
                var linked = _allCards.Where(c => c.IsLinked).ToList();
                if (linked.Count == 0) return;
                SetStatus("Auto-refreshing prices…");
                int updated = 0;
                foreach (var card in linked)
                {
                    try
                    {
                        var price = await _scraper.GetPriceFromPageAsync(card.TcgPlayerUrl);
                        if (price.HasValue)
                        {
                            if (price.Value.market.HasValue) card.RecordPrice(price.Value.market.Value);
                            card.LowPrice = price.Value.low;
                            updated++;
                        }
                    }
                    catch { }
                }
                _storage.SaveCollection(_allCards);
                RenderCards(FilterCards());
                SetStatus($"Auto-refreshed {updated} price(s) at {DateTime.Now:h:mm tt}.");
            };
            _autoRefreshTimer.Start();
        }

        private void LoadCollection()
        {
            _folders   = _storage.LoadFolders();
            _allCards  = _storage.LoadCollection();
            _watchlist = _storage.LoadWatchlist();
        }

        // ── Navigation ────────────────────────────────────────────────────────

        private void ShowHome()
        {
            HomeView.Visibility       = Visibility.Visible;
            CollectionView.Visibility = Visibility.Collapsed;
            WatchlistView.Visibility  = Visibility.Collapsed;
            BrowserView.Visibility    = Visibility.Collapsed;
        }

        private void ShowCollection()
        {
            HomeView.Visibility       = Visibility.Collapsed;
            WatchlistView.Visibility  = Visibility.Collapsed;
            CollectionView.Visibility = Visibility.Visible;
            CollectionView.Visibility = Visibility.Visible;
            BrowserView.Visibility    = Visibility.Collapsed;
            RenderFolderSidebar();
            RenderCards(FilterCards());
        }

        private void ShowWatchlist()
        {
            HomeView.Visibility       = Visibility.Collapsed;
            CollectionView.Visibility = Visibility.Collapsed;
            WatchlistView.Visibility  = Visibility.Visible;
            RenderWatchlist();
        }

        private void GoCollection_Click(object sender, RoutedEventArgs e) => ShowCollection();
        private void GoWatchlist_Click(object sender, RoutedEventArgs e)  => ShowWatchlist();
        private void CollectionHome_Click(object sender, RoutedEventArgs e) => ShowHome();
        private void WatchlistHome_Click(object sender, RoutedEventArgs e)  => ShowHome();

        // ── Watch list ────────────────────────────────────────────────────────

        private void RenderWatchlist()
        {
            WatchlistGrid.Children.Clear();
            WatchlistEmptyLabel.Visibility = _watchlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            double total = _watchlist.Sum(w => w.MarketPrice ?? 0);
            WatchlistTotalLabel.Text = _watchlist.Count == 0 ? "" : $"Total: ${total:F2}";

            foreach (var w in _watchlist.OrderByDescending(w => w.MarketPrice ?? 0))
            {
                var imagePath = _storage.GetImagePath(w.ImageFileName);

                var btn = new Button
                {
                    Style  = (Style)FindResource("CardButtonStyle"),
                    Width  = 160, Height = 260,
                    Margin = new Thickness(8),
                    Tag    = w
                };

                var sp = new StackPanel();

                // Thumbnail
                var imgGrid = new Grid { Width = 148, Height = 180, Margin = new Thickness(4, 4, 4, 0) };
                if (File.Exists(imagePath))
                {
                    var thumb = new Image { Stretch = Stretch.Uniform, Source = LoadImage(imagePath) };
                    RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
                    imgGrid.Children.Add(thumb);
                }
                else
                {
                    imgGrid.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x44)),
                        CornerRadius = new CornerRadius(6),
                        Child = new TextBlock
                        {
                            Text = w.Name, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xcc)),
                            FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8)
                        }
                    });
                }
                sp.Children.Add(imgGrid);

                // Name
                sp.Children.Add(new TextBlock
                {
                    Text = w.Name, Foreground = Brushes.White,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(4, 4, 4, 0), MaxWidth = 148
                });

                // Price + last-refresh delta inline
                if (w.MarketPrice.HasValue)
                {
                    var refreshDelta = w.LastRefreshChange();
                    var priceRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 2, 4, 0) };
                    priceRow.Children.Add(new TextBlock
                    {
                        Text = $"${w.MarketPrice:F2}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                        FontSize = 12, FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    if (refreshDelta.HasValue)
                    {
                        var up = refreshDelta.Value >= 0;
                        priceRow.Children.Add(new TextBlock
                        {
                            Text = $" {(up ? "+" : "")}{refreshDelta.Value:F2}",
                            Foreground = new SolidColorBrush(up
                                ? Color.FromRgb(0x55, 0xee, 0x88)
                                : Color.FromRgb(0xee, 0x55, 0x55)),
                            FontSize = 10, VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(2, 0, 0, 1)
                        });
                    }
                    sp.Children.Add(priceRow);
                }

                // 1-month change
                var change = w.OneMonthChange();
                if (change.HasValue)
                {
                    var up = change.Value >= 0;
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"{(up ? "▲" : "▼")} {(up ? "+" : "")}{change.Value:F2} (1M)",
                        Foreground = new SolidColorBrush(up
                            ? Color.FromRgb(0x55, 0xee, 0x88)
                            : Color.FromRgb(0xee, 0x55, 0x55)),
                        FontSize = 10, TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(4, 1, 4, 4)
                    });
                }

                btn.Content = btn.Content is null ? (object)sp : sp;
                btn.Content = sp;

                var wCopy = w;
                btn.Click += (_, _) => WatchlistCard_Click(wCopy);
                WatchlistGrid.Children.Add(btn);
            }
        }

        private void MoveWatchedToCollection(Models.WatchedCard w)
        {
            var card = new Card
            {
                Name             = w.Name,
                TcgPlayerSetName = w.SetName,
                TcgPlayerUrl     = w.TcgPlayerUrl,
                ImageFileName    = w.ImageFileName,
                MarketPrice      = w.MarketPrice,
                PriceUpdatedAt   = w.PriceUpdatedAt,
                CapturedAt       = DateTime.Now,
            };
            if (card.MarketPrice.HasValue)
                card.PriceHistory.Add(new Models.PricePoint { Date = DateTime.Now, Price = card.MarketPrice.Value });
            _allCards.Add(card);
            _watchlist.Remove(w);
            _storage.SaveCollection(_allCards);
            _storage.SaveWatchlist(_watchlist);
            RenderWatchlist();
            SetStatus($"\"{card.Name}\" moved to your collection.");
        }

        private bool _watchlistBrowserMode;

        private async void WatchlistAdd_Click(object sender, RoutedEventArgs e)
        {
            _watchlistBrowserMode     = true;
            BrowserBackBtn.Content    = "← Watch List";
            WatchlistView.Visibility  = Visibility.Collapsed;
            WatchlistView.Visibility  = Visibility.Collapsed;
            CollectionView.Visibility = Visibility.Collapsed;
            BrowserView.Visibility    = Visibility.Visible;

            if (!_browserInitialized)
            {
                await Browser.EnsureCoreWebView2Async();
                _browserInitialized = true;
                Browser.CoreWebView2.Navigate(TcgStartUrl);
            }
            SetStatus("Navigate to a card on TCGPlayer, then click ✦ Add This Card to add it to your watch list.");
        }

        private void WatchlistCard_Click(Models.WatchedCard w)
        {
            var dlg = new ConfirmDialog("Remove from Watch List",
                $"Remove \"{w.Name}\" from your watch list, or move it to your collection?",
                "→ Move to Collection");
            dlg.Owner = this;
            dlg.ShowDialog();
            if (dlg.Confirmed)
                MoveWatchedToCollection(w);
        }

        private async void WatchlistRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_watchlist.Count == 0) { SetStatus("Watch list is empty."); return; }
            SetStatus($"Refreshing {_watchlist.Count} watch list price(s)…");
            int updated = 0;
            foreach (var w in _watchlist)
            {
                try
                {
                    var price = await _scraper.GetPriceFromPageAsync(w.TcgPlayerUrl);
                    if (price?.market.HasValue == true) { w.RecordPrice(price.Value.market.Value); updated++; }
                }
                catch { }
            }
            _storage.SaveWatchlist(_watchlist);
            RenderWatchlist();
            SetStatus($"Refreshed {updated} watch list price(s).");
        }

        // ── Folder sidebar ────────────────────────────────────────────────────

        private void RenderFolderSidebar()
        {
            FolderList.Children.Clear();
            var allTotal = _allCards.Sum(c => (c.MarketPrice ?? 0) * c.Quantity);
            AddFolderButton("📁  All Cards", null, null, allTotal);
            foreach (var folder in _folders)
            {
                var total = _allCards.Where(c => c.FolderId == folder.Id).Sum(c => (c.MarketPrice ?? 0) * c.Quantity);
                AddFolderButton($"📂  {folder.Name}", folder.Id, folder, total);
            }
        }

        private void AddFolderButton(string label, string? folderId, Models.Folder? folder, double total)
        {
            var isActive = _activeFolderId == folderId;
            var normalBg  = new SolidColorBrush(isActive ? Color.FromRgb(0x2a, 0x2a, 0x5a) : Color.FromArgb(0, 0, 0, 0));
            var hoverBg   = new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x77));

            // Button content: name on top, total on bottom
            var contentPanel = new StackPanel();
            contentPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(isActive ? Colors.White : Color.FromRgb(0xaa, 0xaa, 0xcc)),
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"${total:F2}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                FontSize = 11
            });

            var btn = new Button
            {
                Content = contentPanel,
                Tag = folderId,
                Background = normalBg,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 0),
                AllowDrop = true
            };

            btn.Click += (_, _) =>
            {
                _activeFolderId = (string?)btn.Tag;
                RenderFolderSidebar();
                RenderCards(FilterCards());
            };

            // Drop target highlight
            btn.DragEnter += (_, e) =>
            {
                if (e.Data.GetDataPresent("CardId")) btn.Background = hoverBg;
            };
            btn.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent("CardId") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            btn.DragLeave += (_, _) => btn.Background = normalBg;
            btn.Drop += (_, e) =>
            {
                btn.Background = normalBg;
                if (!e.Data.GetDataPresent("CardId")) return;
                var cardId = (string)e.Data.GetData("CardId");
                var card = _allCards.FirstOrDefault(c => c.Id == cardId);
                if (card == null) return;
                card.FolderId = folderId;
                _storage.SaveCollection(_allCards);
                RenderFolderSidebar();
                RenderCards(FilterCards());
                SetStatus($"\"{card.Name}\" moved to {(folderId == null ? "All Cards" : folder?.Name ?? "folder")}.");
            };

            // Right-click to rename/delete (skip for All Cards)
            if (folder != null)
            {
                var ctx = new ContextMenu();
                var rename = new MenuItem { Header = "Rename…" };
                rename.Click += (_, _) => RenameFolder(folder);
                var delete = new MenuItem { Header = "Delete Folder" };
                delete.Click += (_, _) => DeleteFolder(folder);
                ctx.Items.Add(rename);
                ctx.Items.Add(delete);
                btn.ContextMenu = ctx;
            }

            FolderList.Children.Add(btn);
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog("New Folder", "Folder name:");
            dlg.Owner = this;
            dlg.ShowDialog();
            if (string.IsNullOrWhiteSpace(dlg.Value)) return;
            var folder = new Models.Folder { Name = dlg.Value.Trim() };
            _folders.Add(folder);
            _storage.SaveFolders(_folders);
            RenderFolderSidebar();
        }

        private void RenameFolder(Models.Folder folder)
        {
            var dlg = new InputDialog("Rename Folder", "New name:", folder.Name);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (string.IsNullOrWhiteSpace(dlg.Value)) return;
            folder.Name = dlg.Value.Trim();
            _storage.SaveFolders(_folders);
            RenderFolderSidebar();
        }

        private void DeleteFolder(Models.Folder folder)
        {
            var dlg = new ConfirmDialog("Delete Folder",
                $"Delete \"{folder.Name}\"? Cards inside will be moved to All Cards.", "Delete");
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;
            foreach (var c in _allCards.Where(c => c.FolderId == folder.Id))
                c.FolderId = null;
            _folders.Remove(folder);
            if (_activeFolderId == folder.Id) _activeFolderId = null;
            _storage.SaveFolders(_folders);
            _storage.SaveCollection(_allCards);
            RenderFolderSidebar();
            RenderCards(FilterCards());
        }

        private void RenderCards(IEnumerable<Card> cards)
        {
            CardGrid.Children.Clear();
            var list = cards.OrderByDescending(c => c.MarketPrice ?? 0).ThenByDescending(c => c.CapturedAt).ToList();
            EmptyLabel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountLabel.Text = list.Count == 0 ? "" : $"({list.Count} card{(list.Count == 1 ? "" : "s")})";

            foreach (var card in list)
            {
                var imagePath = _storage.GetImagePath(card.ImageFileName);

                var btn = new Button
                {
                    Style = (Style)FindResource("CardButtonStyle"),
                    Width = 160, Height = 240,
                    Margin = new Thickness(8),
                    Tag = card
                };

                // Drag to folder support
                btn.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    _dragStart = e.GetPosition(null);
                    _isDragging = false;
                };
                btn.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _isDragging) return;
                    var pos  = e.GetPosition(null);
                    var diff = _dragStart - pos;
                    if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    _isDragging = true;
                    var data = new DataObject("CardId", card.Id);
                    DragDrop.DoDragDrop(btn, data, DragDropEffects.Move);
                    _isDragging = false;
                };

                btn.Click += CardButton_Click;

                var sp = new StackPanel();

                // Card image with optional quantity badge
                var imgGrid = new Grid { Width = 148, Height = 180, Margin = new Thickness(4, 4, 4, 0) };
                if (File.Exists(imagePath))
                {
                    var gridImg = new Image { Stretch = Stretch.Uniform, Source = LoadImage(imagePath) };
                    RenderOptions.SetBitmapScalingMode(gridImg, BitmapScalingMode.HighQuality);
                    imgGrid.Children.Add(gridImg);
                }
                else
                {
                    imgGrid.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x44)),
                        CornerRadius = new CornerRadius(6),
                        Child = new TextBlock
                        {
                            Text = card.Name, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xcc)),
                            FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8)
                        }
                    });
                }

                if (card.Quantity > 1)
                {
                    var badge = new Border
                    {
                        Background   = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x8a)),
                        CornerRadius = new CornerRadius(10),
                        Padding      = new Thickness(6, 2, 6, 2),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment   = VerticalAlignment.Top,
                        Margin = new Thickness(0, 4, 4, 0)
                    };
                    badge.Child = new TextBlock
                    {
                        Text       = $"×{card.Quantity}",
                        Foreground = Brushes.White,
                        FontSize   = 11,
                        FontWeight = FontWeights.Bold
                    };
                    imgGrid.Children.Add(badge);
                }

                sp.Children.Add(imgGrid);
                sp.Children.Add(new TextBlock
                {
                    Text = card.Name, Foreground = Brushes.White,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(4, 4, 4, 0), MaxWidth = 148
                });
                if (card.MarketPrice.HasValue)
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"${card.MarketPrice:F2}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xee, 0x88)),
                        FontSize = 12, FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(4, 2, 4, 4)
                    });
                else if (card.IsLinked)
                    sp.Children.Add(new TextBlock
                    {
                        Text = "Price unavailable",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                        FontSize = 10, TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(4, 2, 4, 4)
                    });

                btn.Content = sp;
                CardGrid.Children.Add(btn);
            }
        }

        private static BitmapImage LoadImage(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void CardButton_Click(object sender, RoutedEventArgs e)
        {
            var card = (Card)((Button)sender).Tag;
            var viewer = new CardViewerWindow(card, LoadImage(_storage.GetImagePath(card.ImageFileName)), _folders,
                FetchPriceViaBrowserAsync, () => _storage.SaveCollection(_allCards));
            viewer.Owner = this;
            viewer.Width  = Math.Max(420, ActualWidth  * 0.55);
            viewer.Height = Math.Max(480, ActualHeight * 0.85);
            viewer.ShowDialog();
            if (viewer.Deleted)
            {
                _storage.DeleteCard(card);
                _allCards.Remove(card);
                _storage.SaveCollection(_allCards);
                RenderCards(FilterCards());
                SetStatus($"Deleted \"{card.Name}\".");
            }
            else if (viewer.CardChanged)
            {
                _storage.SaveCollection(_allCards);
                RenderCards(FilterCards());
                SetStatus($"\"{card.Name}\" updated.");
            }
        }

        // ── Browser panel ────────────────────────────────────────────────────

        private async void BrowserAdd_Click(object sender, RoutedEventArgs e)
        {
            CollectionView.Visibility  = Visibility.Collapsed;
            AddCardForm.Visibility     = Visibility.Collapsed;
            BrowserView.Visibility     = Visibility.Visible;

            if (!_browserInitialized)
            {
                await Browser.EnsureCoreWebView2Async();
                _browserInitialized = true;
                Browser.CoreWebView2.Navigate(TcgStartUrl);
            }
            SetStatus("Navigate to a card on TCGPlayer, then click ✦ Add This Card.");
        }

        private void BrowserBack_Click(object sender, RoutedEventArgs e)
        {
            BrowserView.Visibility = Visibility.Collapsed;
            BrowserBackBtn.Content = "← Collection";
            if (_watchlistBrowserMode)
            {
                _watchlistBrowserMode    = false;
                WatchlistView.Visibility = Visibility.Visible;
                RenderWatchlist();
            }
            else
            {
                CollectionView.Visibility = Visibility.Visible;
                RenderCards(FilterCards());
            }
            SetStatus("Ready.");
        }

        private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
            => AddressBar.Text = e.Uri;

        private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            AddressBar.Text = Browser.Source?.ToString() ?? string.Empty;
            AddCardForm.Visibility = Visibility.Collapsed;
            SetStatus(e.IsSuccess ? "Navigate to a card page, then click ✦ Add This Card." : "Page failed to load.");
        }

        private void NavBack_Click(object sender, RoutedEventArgs e) { if (Browser.CanGoBack) Browser.GoBack(); }
        private void NavForward_Click(object sender, RoutedEventArgs e) { if (Browser.CanGoForward) Browser.GoForward(); }
        private void NavReload_Click(object sender, RoutedEventArgs e) => Browser.Reload();

        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var url = AddressBar.Text.Trim();
            if (!url.StartsWith("http")) url = "https://" + url;
            Browser.CoreWebView2?.Navigate(url);
        }

        private async void AddThisCard_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Reading card from page…");
            try
            {
                var json = await Browser.ExecuteScriptAsync(ExtractScript);

                // ExecuteScriptAsync returns a JSON string wrapped in quotes — parse it
                var raw = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? json;
                var data = JObject.Parse(raw);

                var name     = data["name"]?.Value<string>()?.Trim()     ?? string.Empty;
                var set      = data["set"]?.Value<string>()?.Trim()      ?? string.Empty;
                var price    = data["price"]?.Value<double?>();
                var imageUrl = data["imageUrl"]?.Value<string>()?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                {
                    SetStatus("Couldn't read the card name. Make sure you're on a single card's product page.");
                    return;
                }

                FormName.Text  = name;
                FormSet.Text   = set;
                FormPrice.Text = price.HasValue ? price.Value.ToString("F2") : string.Empty;
                _pendingImageUrl = imageUrl;

                AddCardForm.Visibility = Visibility.Visible;
                SetStatus($"Ready to save: {name}{(price.HasValue ? $" — ${price:F2}" : "")}. Click 💾 Save to Collection.");
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
        }

        private async void FormSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FormName.Text)) { FormName.Focus(); return; }

            SetStatus("Saving…");
            double.TryParse(FormPrice.Text, out double parsedPrice);

            var card = new Card
            {
                Name             = FormName.Text.Trim(),
                TcgPlayerSetName = FormSet.Text.Trim(),
                TcgPlayerUrl     = Browser.Source?.ToString() ?? string.Empty,
                MarketPrice      = parsedPrice > 0 ? parsedPrice : null,
                PriceUpdatedAt   = parsedPrice > 0 ? DateTime.Now : null,
                CapturedAt       = DateTime.Now,
            };

            // Download the card image from TCGPlayer
            BitmapSource? cardImage = null;
            if (!string.IsNullOrWhiteSpace(_pendingImageUrl))
            {
                try
                {
                    var imgReq = new HttpRequestMessage(HttpMethod.Get, _pendingImageUrl);
                    imgReq.Headers.Add("Referer", "https://www.tcgplayer.com/");
                    imgReq.Headers.Add("Accept", "image/webp,image/apng,image/*,*/*;q=0.8");
                    var imgResp = await _http.SendAsync(imgReq);
                    if (imgResp.IsSuccessStatusCode)
                    {
                        var bytes = await imgResp.Content.ReadAsByteArrayAsync();
                        using var ms = new MemoryStream(bytes);
                        var decoder = BitmapDecoder.Create(ms,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        cardImage = decoder.Frames[0];
                        cardImage.Freeze();
                    }
                }
                catch { }
            }

            // Fallback: blank placeholder image
            if (cardImage == null)
            {
                var bmp = new WriteableBitmap(200, 280, 96, 96, PixelFormats.Bgr32, null);
                cardImage = bmp;
            }

            // Watchlist mode: save as watched card instead
            if (_watchlistBrowserMode)
            {
                var watched = new Models.WatchedCard
                {
                    Name          = card.Name,
                    SetName       = card.TcgPlayerSetName,
                    TcgPlayerUrl  = card.TcgPlayerUrl,
                    MarketPrice   = card.MarketPrice,
                    PriceUpdatedAt= card.PriceUpdatedAt,
                };
                watched.ImageFileName = _storage.SaveCardImage(cardImage!, watched.Id);
                if (card.MarketPrice.HasValue)
                    watched.PriceHistory.Add(new Models.PricePoint { Date = DateTime.Now, Price = card.MarketPrice.Value });
                _watchlist.Add(watched);
                _storage.SaveWatchlist(_watchlist);
                AddCardForm.Visibility    = Visibility.Collapsed;
                BrowserView.Visibility    = Visibility.Collapsed;
                BrowserBackBtn.Content    = "← Collection";
                _watchlistBrowserMode     = false;
                WatchlistView.Visibility  = Visibility.Visible;
                RenderWatchlist();
                SetStatus($"Added \"{watched.Name}\" to watch list.");
                return;
            }

            // If same card already exists, just increment quantity
            var existing = string.IsNullOrWhiteSpace(card.TcgPlayerUrl) ? null
                : _allCards.FirstOrDefault(c => c.TcgPlayerUrl == card.TcgPlayerUrl);

            string statusMsg;
            if (existing != null)
            {
                existing.Quantity++;
                if (card.MarketPrice.HasValue) existing.RecordPrice(card.MarketPrice.Value);
                statusMsg = $"\"{existing.Name}\" quantity is now ×{existing.Quantity}.";
            }
            else
            {
                card.ImageFileName = _storage.SaveCardImage(cardImage, card.Id);
                if (card.MarketPrice.HasValue)
                    card.PriceHistory.Add(new Models.PricePoint { Date = DateTime.Now, Price = card.MarketPrice.Value });
                _allCards.Add(card);
                statusMsg = $"Added \"{card.Name}\"" +
                            (card.MarketPrice.HasValue ? $" — Market: ${card.MarketPrice:F2}" : "") + ".";
            }

            _storage.SaveCollection(_allCards);
            AddCardForm.Visibility = Visibility.Collapsed;
            RenderFolderSidebar();
            SetStatus($"✓ {statusMsg} Navigate to the next card or click ← Collection when done.");
        }

        private void FormCancel_Click(object sender, RoutedEventArgs e)
            => AddCardForm.Visibility = Visibility.Collapsed;

        // ── Startup price refresh ─────────────────────────────────────────────

        private async Task RefreshAllPricesOnStartupAsync()
        {
            var linked = _allCards.Where(c => c.IsLinked).ToList();
            if (linked.Count == 0) return;

            SetStatus($"Refreshing prices for {linked.Count} card(s)…");

            if (!_browserInitialized)
            {
                await Browser.EnsureCoreWebView2Async();
                _browserInitialized = true;
            }

            int updated = 0;
            foreach (var card in linked)
            {
                try
                {
                    var price = await FetchPriceViaBrowserAsync(card.TcgPlayerUrl);
                    if (price?.market.HasValue == true)
                    {
                        card.RecordPrice(price.Value.market.Value);
                        if (price.Value.low.HasValue) card.LowPrice = price.Value.low;
                        updated++;
                    }
                }
                catch { }
            }

            _storage.SaveCollection(_allCards);
            RenderCards(FilterCards());
            RenderFolderSidebar();
            SetStatus($"Prices refreshed — {updated} of {linked.Count} updated at {DateTime.Now:h:mm tt}.");
        }

        // ── Browser price fetch (used by CardViewerWindow) ───────────────────

        private async Task<(double? market, double? low)?> FetchPriceViaBrowserAsync(string url)
        {
            if (!_browserInitialized)
            {
                await Browser.EnsureCoreWebView2Async();
                _browserInitialized = true;
            }

            var tcs = new TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(true);
            Browser.CoreWebView2.NavigationCompleted += OnNav;
            Browser.CoreWebView2.Navigate(url);
            await Task.WhenAny(tcs.Task, Task.Delay(15000));
            Browser.CoreWebView2.NavigationCompleted -= OnNav;
            await Task.Delay(2500); // let JS finish rendering prices

            try
            {
                var json = await Browser.ExecuteScriptAsync(ExtractScript);
                var raw  = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? json;
                var data = JObject.Parse(raw);
                var price = data["price"]?.Value<double?>();
                return price.HasValue ? (price, (double?)null) : null;
            }
            catch { return null; }
        }

        // ── Refresh prices ───────────────────────────────────────────────────

        private async void RefreshPrices_Click(object sender, RoutedEventArgs e)
        {
            var linked = _allCards.Where(c => c.IsLinked).ToList();
            if (linked.Count == 0) { SetStatus("No cards are linked to TCGPlayer."); return; }

            SetStatus($"Refreshing {linked.Count} price(s)…");
            int updated = 0;
            foreach (var card in linked)
            {
                try
                {
                    var price = await _scraper.GetPriceFromPageAsync(card.TcgPlayerUrl);
                    if (price.HasValue)
                    {
                        if (price.Value.market.HasValue)
                            card.RecordPrice(price.Value.market.Value);
                        card.LowPrice = price.Value.low;
                        updated++;
                    }
                }
                catch { }
            }
            _storage.SaveCollection(_allCards);
            RenderCards(FilterCards());
            SetStatus($"Updated {updated} price(s).");
        }

        // ── Mass Add ─────────────────────────────────────────────────────────

        private bool _massAddCancelled;

        private void CollectionMassAdd_Click(object sender, RoutedEventArgs e) => OpenMassAdd(watchlist: false);
        private void WatchlistMassAdd_Click(object sender, RoutedEventArgs e)  => OpenMassAdd(watchlist: true);
        private void MassAddCancel_Click(object sender, RoutedEventArgs e)
        {
            _massAddCancelled = true;
            MassAddView.Visibility = Visibility.Collapsed;
            if (_watchlistBrowserMode) ShowWatchlist(); else ShowCollection();
        }

        private void OpenMassAdd(bool watchlist)
        {
            var input = new MassAddInputDialog { Owner = this };
            if (input.ShowDialog() != true || string.IsNullOrWhiteSpace(input.CardList)) return;

            var entries = ParseMassAddList(input.CardList);
            if (entries.Count == 0) return;

            _ = RunMassAdd(entries, watchlist);
        }

        private static readonly string[] _nonCardKeywords =
        {
            "starter deck", "starter set", "theme deck", "precon", "preconstructed",
            "booster box", "booster display", "booster case", "booster bundle",
            "elite trainer box", "bundle", "collection box", "gift box",
            "tin", "binder", "sleeves", "deck box", "playmat",
            "promo pack", "build & battle", "build and battle",
            "commander deck", "display box", "set booster", "draft booster",
            "collector booster", "jumpstart", "fat pack",
        };

        private static bool IsNonCard(string name) =>
            _nonCardKeywords.Any(kw => name.Contains(kw, StringComparison.OrdinalIgnoreCase));

        private static List<(int qty, string name, string set)> ParseMassAddList(string text)
        {
            var result = new List<(int, string, string)>();
            foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                int qty = 1;
                var qm = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\s+(.+)$");
                if (qm.Success) { qty = int.Parse(qm.Groups[1].Value); line = qm.Groups[2].Value.Trim(); }

                string set = string.Empty;
                var sm = System.Text.RegularExpressions.Regex.Match(line, @"\[([^\]]+)\]\s*$");
                if (sm.Success) { set = sm.Groups[1].Value.Trim(); line = line[..sm.Index].Trim(); }

                if (!string.IsNullOrWhiteSpace(line))
                    result.Add((qty, line, set));
            }
            return result;
        }

        private void AddMassResultRow(string text, string color)
        {
            MassAddResultStack.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontSize = 12, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap
            });
            MassAddResultScroll.ScrollToBottom();
        }

        private TaskCompletionSource<bool>? _massNavTcs;

        // Navigate to a TCGPlayer search URL and poll JS until a product link appears (up to 10s)
        private async Task<string> SearchAndGetProductUrl(string searchUrl)
        {
            _massNavTcs = new TaskCompletionSource<bool>();
            Browser.CoreWebView2.Navigate(searchUrl);
            await Task.WhenAny(_massNavTcs.Task, Task.Delay(14000));

            // JS polls DOM every 400ms for up to 10 seconds waiting for product links to render
            const string pollScript = @"
(function() {
    return new Promise(function(resolve) {
        var badWords = ['starter deck','booster box','booster bundle','bundle','elite trainer box',
                        'tin','sleeves','deck box','playmat','display box','commander deck',
                        'preconstructed','theme deck'];
        var attempts = 0;
        function check() {
            attempts++;
            // First try __NEXT_DATA__ embedded JSON (fastest)
            var nd = document.getElementById('__NEXT_DATA__');
            if (nd) {
                try {
                    var root = JSON.parse(nd.textContent);
                    var found = null;
                    function walk(o, d) {
                        if (!o || typeof o !== 'object' || d > 15 || found) return;
                        if (Array.isArray(o)) { o.forEach(function(x){ walk(x,d+1); }); return; }
                        if ((o.productName||o.name) && o.productId && (o.urlKey||o.productUrlKey)) {
                            var n = (o.productName||o.name||'').toLowerCase();
                            if (!badWords.some(function(k){ return n.includes(k); })) { found = o; return; }
                        }
                        Object.values(o).forEach(function(v){ walk(v,d+1); });
                    }
                    walk(root, 0);
                    if (found) {
                        var key = found.urlKey || found.productUrlKey || '';
                        var url = key.startsWith('http') ? key : 'https://www.tcgplayer.com/product/' + key;
                        resolve(url); return;
                    }
                } catch(e){}
            }
            // Then try DOM links
            var links = Array.from(document.querySelectorAll('a[href]')).filter(function(a){
                return a.href && a.href.includes('/product/') && !a.href.includes('/search/');
            });
            for (var i = 0; i < links.length; i++) {
                var container = links[i].closest('[class*=""search-result""],[class*=""product-card""],[class*=""SearchResult""],[class*=""ProductCard""]') || links[i].parentElement;
                var text = (container ? container.innerText : (links[i].textContent||'')).toLowerCase();
                if (badWords.some(function(k){ return text.includes(k); })) continue;
                resolve(links[i].href); return;
            }
            if (attempts < 25) { setTimeout(check, 400); } else { resolve(''); }
        }
        check();
    });
})()";
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(pollScript);
            var url = raw?.Trim('"') ?? string.Empty;
            return url == "null" ? string.Empty : url;
        }

        private async Task RunMassAdd(List<(int qty, string name, string set)> entries, bool watchlist)
        {
            try
            {
            // Make sure browser is ready
            if (!_browserInitialized)
            {
                MassAddProgressLabel.Text = "Initializing browser...";
                await Browser.EnsureCoreWebView2Async();
                _browserInitialized = true;
                Browser.CoreWebView2.Navigate(TcgStartUrl);
                await Task.Delay(3000); // let browser settle
            }

            _massAddCancelled = false;
            MassAddResultStack.Children.Clear();
            MassAddProgressBar.Value = 0;
            MassAddProgressLabel.Text = $"Adding 0 of {entries.Count} card(s)...";
            MassAddCancelBtn.IsEnabled = true;

            // Show mass add overlay (reuse browser underneath for navigation)
            HomeView.Visibility       = Visibility.Collapsed;
            CollectionView.Visibility = Visibility.Collapsed;
            WatchlistView.Visibility  = Visibility.Collapsed;
            BrowserView.Visibility    = Visibility.Collapsed;
            MassAddView.Visibility    = Visibility.Visible;

            // Hook nav-completed to our TCS
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => _massNavTcs?.TrySetResult(true);
            Browser.CoreWebView2.NavigationCompleted += OnNav;

            int added = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                if (_massAddCancelled) break;

                var (qty, cardName, setHint) = entries[i];
                MassAddProgressLabel.Text = $"Searching {i + 1} of {entries.Count}: {cardName}...";

                try
                {
                    // Build search query — replace & with "and" so TCGPlayer search handles it
                    var searchName = cardName.Replace("&", "and");
                    var queryStr   = string.IsNullOrEmpty(setHint) ? searchName : $"{searchName} {setHint}";
                    var encoded    = System.Web.HttpUtility.UrlEncode(queryStr);
                    var searchUrl  = $"https://www.tcgplayer.com/search/all/product?q={encoded}&view=grid";

                    var productUrl = await SearchAndGetProductUrl(searchUrl);

                    // Retry with name only if set hint search found nothing
                    if (string.IsNullOrEmpty(productUrl) && !string.IsNullOrEmpty(setHint))
                    {
                        var enc2 = System.Web.HttpUtility.UrlEncode(searchName);
                        productUrl = await SearchAndGetProductUrl(
                            $"https://www.tcgplayer.com/search/all/product?q={enc2}&view=grid");
                    }

                    if (string.IsNullOrEmpty(productUrl))
                    {
                        AddMassResultRow($"✗  Not found: {cardName}", "#cc6666");
                        continue;
                    }

                    // Navigate to product page and extract
                    _massNavTcs = new TaskCompletionSource<bool>();
                    Browser.CoreWebView2.Navigate(productUrl);
                    await Task.WhenAny(_massNavTcs.Task, Task.Delay(14000));
                    await Task.Delay(2000);

                    var rawJson = await Browser.CoreWebView2.ExecuteScriptAsync(ExtractScript);
                    if (rawJson.StartsWith("\""))
                        rawJson = System.Text.Json.JsonSerializer.Deserialize<string>(rawJson) ?? rawJson;

                    var obj  = Newtonsoft.Json.Linq.JObject.Parse(rawJson);
                    var name = obj["name"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(name) || IsNonCard(name))
                    {
                        AddMassResultRow($"✗  Skipped (not a card): {cardName}", "#cc6666");
                        continue;
                    }

                    double? price = obj["price"]?.Type != Newtonsoft.Json.Linq.JTokenType.Null
                        ? (double?)obj["price"] : null;
                    var imageUrl = obj["imageUrl"]?.ToString() ?? string.Empty;
                    var setName  = obj["set"]?.ToString() ?? string.Empty;

                    // Download image
                    byte[]? imageBytes = null;
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        try
                        {
                            var req  = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                            req.Headers.Add("Referer", "https://www.tcgplayer.com/");
                            var resp = await _http.SendAsync(req);
                            if (resp.IsSuccessStatusCode) imageBytes = await resp.Content.ReadAsByteArrayAsync();
                        }
                        catch { }
                    }

                    BitmapSource? bmp = null;
                    if (imageBytes != null)
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.StreamSource = new System.IO.MemoryStream(imageBytes);
                            bi.CacheOption  = BitmapCacheOption.OnLoad;
                            bi.EndInit();
                            bi.Freeze();
                            bmp = bi;
                        }
                        catch { }
                    }

                    if (watchlist)
                    {
                        var wc = new Models.WatchedCard { Name = name, SetName = setName, TcgPlayerUrl = productUrl };
                        if (price.HasValue) wc.RecordPrice(price.Value);
                        if (bmp != null) wc.ImageFileName = _storage.SaveCardImage(bmp, wc.Id);
                        _watchlist.Add(wc);
                        _storage.SaveWatchlist(_watchlist);
                    }
                    else
                    {
                        var existing = _allCards.FirstOrDefault(c =>
                            !string.IsNullOrEmpty(c.TcgPlayerUrl) && c.TcgPlayerUrl == productUrl);
                        if (existing != null)
                        {
                            existing.Quantity += qty;
                        }
                        else
                        {
                            var card = new Models.Card
                            {
                                Name = name, TcgPlayerSetName = setName,
                                TcgPlayerUrl = productUrl, CardType = "Foil", Quantity = qty
                            };
                            if (price.HasValue) card.RecordPrice(price.Value);
                            if (bmp != null) card.ImageFileName = _storage.SaveCardImage(bmp, card.Id);
                            _allCards.Add(card);
                        }
                        _storage.SaveCollection(_allCards);
                    }

                    added++;
                    AddMassResultRow($"✓  {name}{(string.IsNullOrEmpty(setName) ? "" : $"  [{setName}]")}  {(price.HasValue ? $"${price:F2}" : "")}", "#44cc88");
                }
                catch (Exception ex)
                {
                    AddMassResultRow($"✗  Error: {cardName} — {ex.Message}", "#cc6666");
                }

                MassAddProgressBar.Value = (i + 1) * 100.0 / entries.Count;
            }

            Browser.CoreWebView2.NavigationCompleted -= OnNav;

            MassAddProgressLabel.Text = $"Done — added {added} of {entries.Count} card(s).";
            MassAddCancelBtn.Content  = watchlist ? "← Watch List" : "← Collection";
            MassAddCancelBtn.IsEnabled = true;
            _watchlistBrowserMode = watchlist;

            if (watchlist) RenderWatchlist(); else { RenderCards(FilterCards()); RenderFolderSidebar(); }
            }
            catch (Exception ex)
            {
                MassAddProgressLabel.Text = $"Error: {ex.Message}";
                MassAddCancelBtn.Content   = "← Back";
                MassAddCancelBtn.IsEnabled = true;
                SetStatus($"Mass add error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Search ───────────────────────────────────────────────────────────

        private void Search_Changed(object sender, TextChangedEventArgs e) => RenderCards(FilterCards());

        private IEnumerable<Card> FilterCards()
        {
            var cards = _activeFolderId == null
                ? _allCards
                : _allCards.Where(c => c.FolderId == _activeFolderId);

            var q = SearchBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(q))
                cards = cards.Where(c =>
                    c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Notes.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.TcgPlayerSetName.Contains(q, StringComparison.OrdinalIgnoreCase));

            return cards;
        }

        // ── Window controls ──────────────────────────────────────────────────

        private void SetStatus(string msg) => StatusLabel.Text = msg;

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            MaximizeBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
