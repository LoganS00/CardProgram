using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardProgram.Services
{
    public class TcgPlayerScraperResult
    {
        public string Name { get; set; } = string.Empty;
        public string SetName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public double? MarketPrice { get; set; }
        public double? LowPrice { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
    }

    public class TcgPlayerScraperService
    {
        private static readonly HttpClient _http = new(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
        });

        static TcgPlayerScraperService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _http.DefaultRequestHeaders.Add("Origin", "https://www.tcgplayer.com");
            _http.DefaultRequestHeaders.Add("Referer", "https://www.tcgplayer.com/");
        }

        public async Task<List<TcgPlayerScraperResult>> SearchAsync(string cardName)
        {
            var results = await TrySearchApiAsync(cardName);
            if (results.Count > 0) return results;

            results = await TryHtmlSearchAsync(cardName);
            return results;
        }

        // TCGPlayer's internal marketplace search — POST with JSON body
        private async Task<List<TcgPlayerScraperResult>> TrySearchApiAsync(string cardName)
        {
            try
            {
                var payload = new
                {
                    algorithm = "sales_synonym_v2",
                    from = 0,
                    size = 10,
                    filters = new
                    {
                        term = new
                        {
                            sellerStatus = "Live",
                            channelId = 0,
                            productTypeName = new[] { "Cards" }
                        },
                        range = new { },
                        match = new { }
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
                    query = cardName
                };

                var body = new StringContent(
                    JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://mp-search-api.tcgplayer.com/v1/search/request");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = body;

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return new List<TcgPlayerScraperResult>();

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                return ParseSearchResults(json);
            }
            catch { return new List<TcgPlayerScraperResult>(); }
        }

        // Fallback: scrape the HTML search page
        private async Task<List<TcgPlayerScraperResult>> TryHtmlSearchAsync(string cardName)
        {
            try
            {
                var encoded = HttpUtility.UrlEncode(cardName);
                var url = $"https://www.tcgplayer.com/search/all/product?q={encoded}&view=grid";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

                var html = await (await _http.SendAsync(req)).Content.ReadAsStringAsync();

                // Pull __NEXT_DATA__ JSON embedded in the page
                var m = Regex.Match(html,
                    @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>");
                if (!m.Success) return new List<TcgPlayerScraperResult>();

                var root = JObject.Parse(m.Groups[1].Value);
                return ParseNextData(root);
            }
            catch { return new List<TcgPlayerScraperResult>(); }
        }

        private static List<TcgPlayerScraperResult> ParseSearchResults(JObject json)
        {
            var results = new List<TcgPlayerScraperResult>();
            try
            {
                // Results live under results[0].results[] in the search API response
                var items = json.SelectTokens("$..results[*]");
                foreach (var item in items)
                {
                    var name = item["productName"]?.Value<string>()
                            ?? item["customAttributes"]?["name"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    results.Add(new TcgPlayerScraperResult
                    {
                        Name = name,
                        SetName = item["setName"]?.Value<string>()
                               ?? item["customAttributes"]?["setName"]?.Value<string>()
                               ?? string.Empty,
                        MarketPrice = item["marketPrice"]?.Value<double?>()
                                   ?? item["lowestPrice"]?.Value<double?>()
                                   ?? item["customAttributes"]?["lowestPrice"]?.Value<double?>(),
                        ImageUrl = item["imageUrl"]?.Value<string>()
                                ?? item["customAttributes"]?["image"]?.Value<string>()
                                ?? string.Empty,
                        Url = BuildUrl(item["urlKey"]?.Value<string>()
                                    ?? item["productUrlKey"]?.Value<string>() ?? string.Empty),
                    });

                    if (results.Count >= 10) break;
                }
            }
            catch { }
            return results;
        }

        private static List<TcgPlayerScraperResult> ParseNextData(JObject root)
        {
            var results = new List<TcgPlayerScraperResult>();
            try
            {
                foreach (var token in root.SelectTokens("$..*"))
                {
                    if (token is not JObject obj) continue;
                    var name = obj["productName"]?.Value<string>() ?? obj["name"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (obj["marketPrice"] == null && obj["lowestPrice"] == null && obj["imageUrl"] == null) continue;

                    results.Add(new TcgPlayerScraperResult
                    {
                        Name = name,
                        SetName = obj["setName"]?.Value<string>() ?? obj["groupName"]?.Value<string>() ?? string.Empty,
                        MarketPrice = obj["marketPrice"]?.Value<double?>() ?? obj["lowestPrice"]?.Value<double?>(),
                        ImageUrl = obj["imageUrl"]?.Value<string>() ?? string.Empty,
                        Url = BuildUrl(obj["urlKey"]?.Value<string>() ?? obj["url"]?.Value<string>() ?? string.Empty),
                    });

                    if (results.Count >= 10) break;
                }
            }
            catch { }
            return results;
        }

        private static string BuildUrl(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            if (key.StartsWith("http")) return key;
            return $"https://www.tcgplayer.com/product/{key}";
        }

        public async Task<(double? market, double? low)?> GetPriceFromPageAsync(string productUrl)
        {
            if (string.IsNullOrWhiteSpace(productUrl)) return null;
            try
            {
                var url = productUrl.StartsWith("http") ? productUrl : "https://www.tcgplayer.com" + productUrl;
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                var html = await (await _http.SendAsync(req)).Content.ReadAsStringAsync();

                var m = Regex.Match(html, @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>");
                if (!m.Success) return null;

                var root = JObject.Parse(m.Groups[1].Value);
                var market = root.SelectToken("$..marketPrice")?.Value<double?>();
                var low = root.SelectToken("$..lowPrice")?.Value<double?>();
                return (market, low);
            }
            catch { return null; }
        }
    }
}
