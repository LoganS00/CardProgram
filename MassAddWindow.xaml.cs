using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CardProgram.Models;
using CardProgram.Services;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardProgram
{
    public class MassAddEntry
    {
        public int Quantity { get; set; } = 1;
        public string CardName { get; set; } = string.Empty;
        public string SetHint { get; set; } = string.Empty;
        public string OriginalLine { get; set; } = string.Empty;
        public MassAddResult? Found { get; set; }
        public bool IsFound => Found != null;
    }

    public class MassAddResult
    {
        public string Name { get; set; } = string.Empty;
        public string SetName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public double? MarketPrice { get; set; }
    }

    public partial class MassAddWindow : Window
    {
        private readonly CardStorageService _storage = new();
        private static readonly HttpClient _http = new();
        private readonly List<MassAddEntry> _entries = new();
        private bool _browserReady;
        private TaskCompletionSource<string>? _navTcs;

        public bool IsWatchlistMode { get; set; }
        public List<Card> AddedCards { get; } = new();
        public List<WatchedCard> AddedWatchedCards { get; } = new();

        // Extract first card result directly from TCGPlayer search results page
        private const string SearchExtractScript = @"
(function() {
    // Try __NEXT_DATA__ embedded JSON first
    try {
        var nd = document.getElementById('__NEXT_DATA__');
        if (nd) {
            var root = JSON.parse(nd.textContent);
            var found = null;
            function walk(obj, depth) {
                if (!obj || typeof obj !== 'object' || depth > 12) return;
                if (Array.isArray(obj)) { obj.forEach(function(x){ walk(x, depth+1); }); return; }
                if (!found && (obj.productName || obj.name) &&
                    (obj.productId || obj.urlKey) &&
                    (obj.marketPrice !== undefined || obj.lowestPrice !== undefined || obj.imageUrl)) {
                    found = obj;
                }
                if (!found) Object.values(obj).forEach(function(v){ walk(v, depth+1); });
            }
            walk(root, 0);
            if (found) {
                var urlKey = found.urlKey || found.productUrlKey || '';
                var url = urlKey ? (urlKey.startsWith('http') ? urlKey : 'https://www.tcgplayer.com/product/' + urlKey) : '';
                return JSON.stringify({
                    name: found.productName || found.name || '',
                    setName: found.setName || found.groupName || '',
                    price: found.marketPrice || found.lowestPrice || null,
                    imageUrl: (found.imageUrl || '').replace(/\/fit-in\/\d+x\d+/, ''),
                    url: url
                });
            }
        }
    } catch(e) {}

    // DOM fallback — scrape visible card tiles
    var selectors = [
        '.search-result__content a[href*=""/product/""]',
        '[data-testid=""product-card""] a[href*=""/product/""]',
        'a[href*=""/product/""]'
    ];
    var link = null;
    for (var i = 0; i < selectors.length; i++) {
        link = document.querySelector(selectors[i]);
        if (link) break;
    }
    if (!link) return JSON.stringify(null);

    var card = link.closest('[class*=""search-result""], [class*=""product-card""], [class*=""SearchResult""]') || link.parentElement;
    var nameEl = card.querySelector('[class*=""title""], [class*=""name""], h3, h2, .product-card__title');
    var name = nameEl ? nameEl.innerText.trim() : link.innerText.trim();
    var imgEl = card.querySelector('img');
    var imageUrl = imgEl ? imgEl.src.replace(/\/fit-in\/\d+x\d+/, '') : '';
    var priceEl = card.querySelector('[class*=""price""], [class*=""Price""]');
    var priceText = priceEl ? priceEl.innerText : '';
    var priceMatch = priceText.match(/\$([\d,]+\.?\d{2})/);
    var price = priceMatch ? parseFloat(priceMatch[1].replace(',', '')) : null;

    if (!name) return JSON.stringify(null);
    return JSON.stringify({ name: name, setName: '', price: price, imageUrl: imageUrl, url: link.href });
})()";

        public MassAddWindow()
        {
            InitializeComponent();
            InitBrowserAsync();
        }

        private async void InitBrowserAsync()
        {
            try
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CardProgram", "webview2cache");
                var env = await CoreWebView2Environment.CreateAsync(null, cacheDir);
                await HiddenBrowser.EnsureCoreWebView2Async(env);
                HiddenBrowser.CoreWebView2.NavigationCompleted += (s, e) => _navTcs?.TrySetResult("done");
                _browserReady = true;
            }
            catch { }
        }

        private async Task NavigateTo(string url)
        {
            _navTcs = new TaskCompletionSource<string>();
            HiddenBrowser.CoreWebView2.Navigate(url);
            await Task.WhenAny(_navTcs.Task, Task.Delay(12000));
            await Task.Delay(1800); // allow JS hydration
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private static (int qty, string name, string set) ParseLine(string line)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line)) return (0, string.Empty, string.Empty);

            int qty = 1;
            var qtyMatch = Regex.Match(line, @"^(\d+)\s+(.+)$");
            if (qtyMatch.Success)
            {
                qty = int.Parse(qtyMatch.Groups[1].Value);
                line = qtyMatch.Groups[2].Value.Trim();
            }

            string set = string.Empty;
            var setMatch = Regex.Match(line, @"\[([^\]]+)\]\s*$");
            if (setMatch.Success)
            {
                set = setMatch.Groups[1].Value.Trim();
                line = line[..setMatch.Index].Trim();
            }

            return (qty, line, set);
        }

        private static readonly string[] _nonCardKeywords =
        {
            "starter deck", "starter set", "theme deck", "precon", "preconstructed",
            "booster box", "booster display", "booster case", "booster bundle",
            "elite trainer box", "etb", "bundle", "collection box", "gift box",
            "tin", "binder", "album", "sleeves", "deck box", "playmat",
            "promo pack", "build & battle", "build and battle",
            "commander deck", "commander precon",
            "display box", "set booster", "draft booster", "collector booster",
            "jumpstart", "fat pack", "land station",
        };

        private static bool IsNonCard(string name, string productTypeName)
        {
            var lower = name.ToLowerInvariant();
            foreach (var kw in _nonCardKeywords)
                if (lower.Contains(kw)) return true;

            // Check explicit product type from API
            var typeLower = productTypeName.ToLowerInvariant();
            if (typeLower.Contains("sealed") || typeLower.Contains("deck") ||
                typeLower.Contains("pack") || typeLower.Contains("box") ||
                typeLower.Contains("accessory") || typeLower.Contains("supply"))
                return true;

            return false;
        }

        // Fast path: try TCGPlayer's search API directly (no browser needed)
        private static async Task<MassAddResult?> TryApiSearch(string cardName, string setHint)
        {
            try
            {
                var query = string.IsNullOrEmpty(setHint) ? cardName : $"{cardName} {setHint}";
                var payload = new
                {
                    algorithm = "sales_synonym_v2",
                    from = 0, size = 5,
                    filters = new
                    {
                        term = new { sellerStatus = "Live", channelId = 0, productTypeName = new[] { "Cards" } },
                        range = new { }, match = new { }
                    },
                    listingSearch = new
                    {
                        filters = new
                        {
                            term = new { sellerStatus = "Live", channelId = 0 },
                            range = new { quantity = new { gte = 1 } }
                        }
                    },
                    context = new { shippingCountry = "US", cart = new { } },
                    settings = new { useFuzzySearch = true, didYouMean = new { } },
                    sort = new { },
                    query
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Origin", "https://www.tcgplayer.com");
                client.DefaultRequestHeaders.Add("Referer", "https://www.tcgplayer.com/");

                var body = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, "https://mp-search-api.tcgplayer.com/v1/search/request");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = body;

                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var root = JObject.Parse(await resp.Content.ReadAsStringAsync());

                // Walk every object looking for something that resembles a product
                var candidates = new List<(MassAddResult result, int score)>();
                foreach (var token in root.SelectTokens("$..results[*]"))
                {
                    if (token is not JObject obj) continue;
                    var name = obj["productName"]?.ToString() ?? obj["customAttributes"]?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var urlKey = obj["urlKey"]?.ToString() ?? obj["productUrlKey"]?.ToString() ?? string.Empty;
                    var url = urlKey.StartsWith("http") ? urlKey
                            : string.IsNullOrEmpty(urlKey) ? string.Empty
                            : $"https://www.tcgplayer.com/product/{urlKey}";

                    double? mp = null;
                    if (obj["marketPrice"] != null) mp = (double?)obj["marketPrice"];
                    else if (obj["lowestPrice"] != null) mp = (double?)obj["lowestPrice"];
                    else if (obj["customAttributes"]?["lowestPrice"] != null)
                        mp = (double?)obj["customAttributes"]!["lowestPrice"];

                    var setName = obj["setName"]?.ToString() ?? obj["customAttributes"]?["setName"]?.ToString() ?? string.Empty;
                    var imageUrl = obj["imageUrl"]?.ToString() ?? obj["customAttributes"]?["image"]?.ToString() ?? string.Empty;

                    var productTypeName = obj["productTypeName"]?.ToString()
                                      ?? obj["customAttributes"]?["productTypeName"]?.ToString()
                                      ?? string.Empty;
                    if (IsNonCard(name, productTypeName)) continue;

                    var result = new MassAddResult { Name = name, SetName = setName, MarketPrice = mp, ImageUrl = imageUrl, Url = url };

                    // Score: higher = better match
                    int score = 0;
                    if (name.Contains(cardName, StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (name.Equals(cardName, StringComparison.OrdinalIgnoreCase)) score += 20;
                    if (!string.IsNullOrEmpty(setHint))
                    {
                        if (setName.Contains(setHint, StringComparison.OrdinalIgnoreCase)) score += 30;
                        if (name.Contains(setHint, StringComparison.OrdinalIgnoreCase)) score += 15;
                    }
                    candidates.Add((result, score));
                }

                return candidates.Count == 0 ? null
                     : candidates.OrderByDescending(c => c.score).First().result;
            }
            catch { return null; }
        }

        // Slow path: use real browser to navigate search page
        private async Task<MassAddResult?> TryBrowserSearch(string cardName, string setHint)
        {
            var query = string.IsNullOrEmpty(setHint) ? cardName : $"{cardName} {setHint}";
            var encoded = Uri.EscapeDataString(query);
            await NavigateTo($"https://www.tcgplayer.com/search/all/product?q={encoded}&view=grid");

            var raw = await HiddenBrowser.CoreWebView2.ExecuteScriptAsync(SearchExtractScript);

            // If nothing found and we had a set hint, retry without it
            if ((raw == "null" || raw == "\"null\"") && !string.IsNullOrEmpty(setHint))
            {
                var encoded2 = Uri.EscapeDataString(cardName);
                await NavigateTo($"https://www.tcgplayer.com/search/all/product?q={encoded2}&view=grid");
                raw = await HiddenBrowser.CoreWebView2.ExecuteScriptAsync(SearchExtractScript);
            }

            if (raw == "null" || raw == "\"null\"" || string.IsNullOrWhiteSpace(raw)) return null;

            // ExecuteScriptAsync double-encodes strings
            if (raw.StartsWith("\""))
                raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw;

            var obj = JObject.Parse(raw);
            var name2 = obj["name"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name2)) return null;
            if (IsNonCard(name2, string.Empty)) return null;

            double? price = null;
            if (obj["price"] != null && obj["price"]!.Type != JTokenType.Null)
                price = (double?)obj["price"];

            return new MassAddResult
            {
                Name = name2,
                SetName = obj["setName"]?.ToString() ?? string.Empty,
                Url = obj["url"]?.ToString() ?? string.Empty,
                ImageUrl = obj["imageUrl"]?.ToString() ?? string.Empty,
                MarketPrice = price
            };
        }

        private async Task<MassAddResult?> SearchCard(string cardName, string setHint)
        {
            // Fast path first — no browser needed
            var result = await TryApiSearch(cardName, setHint);
            if (result != null) return result;

            // If set hint search missed, try name only via API
            if (!string.IsNullOrEmpty(setHint))
            {
                result = await TryApiSearch(cardName, string.Empty);
                if (result != null) return result;
            }

            // Slow path — real browser
            if (!_browserReady) return null;
            return await TryBrowserSearch(cardName, setHint);
        }

        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            _entries.Clear();
            ResultsList.Items.Clear();
            AddBtn.IsEnabled = false;
            SearchBtn.IsEnabled = false;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            StatusBlock.Text = $"Searching {lines.Count} card(s)...";
            SummaryBlock.Text = string.Empty;

            foreach (var line in lines)
            {
                var (qty, name, set) = ParseLine(line);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var entry = new MassAddEntry { Quantity = qty, CardName = name, SetHint = set, OriginalLine = line.Trim() };
                _entries.Add(entry);

                var row = BuildResultRow(entry, "Searching...", "#aaaaff");
                ResultsList.Items.Add(row);
                StatusBlock.Text = $"Searching: {name}...";

                try
                {
                    entry.Found = await SearchCard(name, set);

                    var newRow = BuildResultRow(entry,
                        entry.Found != null
                            ? $"✓  {entry.Found.Name}  |  {(string.IsNullOrEmpty(entry.Found.SetName) ? "—" : entry.Found.SetName)}  |  ${entry.Found.MarketPrice:F2}"
                            : "✗  Not found",
                        entry.Found != null ? "#44cc88" : "#cc4444");

                    var idx = ResultsList.Items.IndexOf(row);
                    if (idx >= 0) ResultsList.Items[idx] = newRow;
                }
                catch
                {
                    var idx = ResultsList.Items.IndexOf(row);
                    if (idx >= 0) ResultsList.Items[idx] = BuildResultRow(entry, "✗  Search error", "#cc4444");
                }
            }

            int foundCount = _entries.Count(en => en.IsFound);
            StatusBlock.Text = string.Empty;
            SummaryBlock.Text = $"{foundCount} of {_entries.Count} card(s) found";
            AddBtn.IsEnabled = foundCount > 0;
            AddBtn.Content = $"Add {foundCount} Card(s)";
            SearchBtn.IsEnabled = true;
        }

        private static Border BuildResultRow(MassAddEntry entry, string statusText, string statusColor)
        {
            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = $"{entry.Quantity}× {entry.CardName}" + (string.IsNullOrEmpty(entry.SetHint) ? "" : $" [{entry.SetHint}]"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ccccee")),
                FontSize = 12
            });
            left.Children.Add(new TextBlock
            {
                Text = statusText,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor)),
                FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap
            });
            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e1e30")),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 3, 0, 3), Child = left
            };
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            AddBtn.IsEnabled = false;
            SearchBtn.IsEnabled = false;
            var toAdd = _entries.Where(en => en.IsFound).ToList();
            StatusBlock.Text = $"Saving {toAdd.Count} card(s)...";

            foreach (var entry in toAdd)
            {
                var found = entry.Found!;
                byte[]? imageBytes = null;
                if (!string.IsNullOrEmpty(found.ImageUrl))
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, found.ImageUrl);
                        req.Headers.Add("Referer", "https://www.tcgplayer.com/");
                        var resp = await _http.SendAsync(req);
                        if (resp.IsSuccessStatusCode) imageBytes = await resp.Content.ReadAsByteArrayAsync();
                    }
                    catch { }
                }

                try
                {
                    if (IsWatchlistMode)
                    {
                        var wc = new WatchedCard { Name = found.Name, SetName = found.SetName, TcgPlayerUrl = found.Url };
                        if (found.MarketPrice.HasValue) wc.RecordPrice(found.MarketPrice.Value);
                        if (imageBytes != null) { var b = BytesToBitmap(imageBytes); if (b != null) wc.ImageFileName = _storage.SaveCardImage(b, wc.Id); }
                        AddedWatchedCards.Add(wc);
                    }
                    else
                    {
                        var card = new Card { Name = found.Name, TcgPlayerSetName = found.SetName, TcgPlayerUrl = found.Url, CardType = "Foil", Quantity = entry.Quantity };
                        if (found.MarketPrice.HasValue) card.RecordPrice(found.MarketPrice.Value);
                        if (imageBytes != null) { var b = BytesToBitmap(imageBytes); if (b != null) card.ImageFileName = _storage.SaveCardImage(b, card.Id); }
                        AddedCards.Add(card);
                    }
                }
                catch { }
            }

            StatusBlock.Text = string.Empty;
            DialogResult = true;
            Close();
        }

        private static BitmapSource? BytesToBitmap(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
