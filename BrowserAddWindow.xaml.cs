using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CardProgram.Models;
using CardProgram.Services;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace CardProgram
{
    public partial class BrowserAddWindow : Window
    {
        public Card? ResultCard { get; private set; }

        private const string StartUrl =
            "https://www.tcgplayer.com/search/one-piece-card-game/product?view=grid";

        public BrowserAddWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) => await InitBrowserAsync();
        }

        private async Task InitBrowserAsync()
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Navigate(StartUrl);
        }

        // ── Scrape the current page for card info ──────────────────────────────

        private async void AddThisCard_Click(object sender, RoutedEventArgs e)
        {
            StatusLabel.Text = "Reading card from page…";

            try
            {
                // Extract card name and price via JavaScript injected into the live page
                var json = await Browser.ExecuteScriptAsync(ExtractScript);
                var data = JObject.Parse(json);

                var name  = data["name"]?.Value<string>() ?? string.Empty;
                var set   = data["set"]?.Value<string>() ?? string.Empty;
                var price = data["price"]?.Value<double?>();
                var img   = data["image"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                {
                    StatusLabel.Text = "Couldn't read the card name. Make sure you're on a product page.";
                    return;
                }

                // Ask for a card image via snip
                StatusLabel.Text = $"Got: {name} — now snip a screenshot of your card.";

                var snipWindow = new SnipForCardWindow(name, set, price, img, Browser.CoreWebView2.Source);
                snipWindow.Owner = this;
                snipWindow.ShowDialog();

                if (snipWindow.ResultCard != null)
                {
                    ResultCard = snipWindow.ResultCard;
                    Close();
                }
                else
                {
                    StatusLabel.Text = "Snip cancelled. Navigate to a card and try again.";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error: {ex.Message}";
            }
        }

        // JavaScript that reads the card name and price from the TCGPlayer product page
        private const string ExtractScript = @"
(function() {
    // Product detail page
    var nameEl = document.querySelector('h1.product-details__name')
               || document.querySelector('[class*=""product-details__name""]')
               || document.querySelector('h1[class*=""name""]')
               || document.querySelector('.product-card__title')
               || document.querySelector('h1');

    var priceEl = document.querySelector('[class*=""spotlight__price""]')
                || document.querySelector('[class*=""price-point__data""]')
                || document.querySelector('[class*=""product-price""]');

    var imgEl = document.querySelector('.product-gallery__image img')
              || document.querySelector('[class*=""product-image""] img')
              || document.querySelector('.lazy-image img');

    var setEl = document.querySelector('[class*=""product-details__set""]')
              || document.querySelector('[class*=""set-name""]');

    var price = null;
    if (priceEl) {
        var m = priceEl.innerText.match(/\$?([\d,]+\.?\d*)/);
        if (m) price = parseFloat(m[1].replace(',',''));
    }

    return JSON.stringify({
        name:  nameEl  ? nameEl.innerText.trim()  : '',
        set:   setEl   ? setEl.innerText.trim()   : '',
        price: price,
        image: imgEl   ? imgEl.src : ''
    });
})();
";

        // ── Navigation helpers ─────────────────────────────────────────────────

        private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            AddressBar.Text = e.Uri;
            StatusLabel.Text = "Loading…";
        }

        private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            AddressBar.Text = Browser.Source?.ToString() ?? string.Empty;
            StatusLabel.Text = e.IsSuccess ? "Ready  —  navigate to a card page and click Add This Card"
                                           : "Page failed to load.";
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CanGoBack) Browser.GoBack();
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CanGoForward) Browser.GoForward();
        }

        private void Reload_Click(object sender, RoutedEventArgs e) => Browser.Reload();

        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var url = AddressBar.Text.Trim();
            if (!url.StartsWith("http")) url = "https://" + url;
            Browser.CoreWebView2?.Navigate(url);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
