using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardProgram.Services
{
    public class TcgPlayerProduct
    {
        public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SetName { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
    }

    public class TcgPlayerPrice
    {
        public double? MarketPrice { get; set; }
        public double? LowPrice { get; set; }
        public double? MidPrice { get; set; }
        public DateTime FetchedAt { get; set; } = DateTime.Now;
    }

    public class TcgPlayerService
    {
        private const string BaseUrl = "https://api.tcgplayer.com";
        // One Piece TCG category ID on TCGPlayer
        private const int OnePieceCategoryId = 67;

        private readonly HttpClient _http = new();
        private string? _token;
        private DateTime _tokenExpiry;

        public string PublicKey { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;

        public bool HasCredentials => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);

        private async Task EnsureTokenAsync()
        {
            if (_token != null && DateTime.UtcNow < _tokenExpiry) return;

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type", "client_credentials"),
                new KeyValuePair<string,string>("client_id", PublicKey),
                new KeyValuePair<string,string>("client_secret", PrivateKey),
            });

            var resp = await _http.PostAsync($"{BaseUrl}/token", content);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            _token = json["access_token"]!.Value<string>();
            int expiresIn = json["expires_in"]!.Value<int>();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 30);
        }

        private HttpRequestMessage Auth(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return req;
        }

        public async Task<List<TcgPlayerProduct>> SearchCardsAsync(string name)
        {
            await EnsureTokenAsync();
            var encoded = Uri.EscapeDataString(name);
            var url = $"{BaseUrl}/catalog/products?productName={encoded}&categoryId={OnePieceCategoryId}&productTypes=Cards&limit=10";
            var resp = await _http.SendAsync(Auth(HttpMethod.Get, url));
            resp.EnsureSuccessStatusCode();

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var results = new List<TcgPlayerProduct>();
            foreach (var item in json["results"] ?? new JArray())
            {
                results.Add(new TcgPlayerProduct
                {
                    ProductId = item["productId"]!.Value<int>(),
                    Name = item["name"]?.Value<string>() ?? string.Empty,
                    SetName = item["groupName"]?.Value<string>() ?? string.Empty,
                    ImageUrl = item["imageUrl"]?.Value<string>() ?? string.Empty,
                });
            }
            return results;
        }

        public async Task<TcgPlayerPrice?> GetPriceAsync(int productId)
        {
            await EnsureTokenAsync();
            var url = $"{BaseUrl}/pricing/product/{productId}";
            var resp = await _http.SendAsync(Auth(HttpMethod.Get, url));
            resp.EnsureSuccessStatusCode();

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            // Pick the "Normal" printing price (subTypeName == null or "Normal")
            foreach (var item in json["results"] ?? new JArray())
            {
                var subType = item["subTypeName"]?.Value<string>();
                if (subType == null || subType.Equals("Normal", StringComparison.OrdinalIgnoreCase))
                {
                    return new TcgPlayerPrice
                    {
                        MarketPrice = item["marketPrice"]?.Value<double?>(),
                        LowPrice = item["lowPrice"]?.Value<double?>(),
                        MidPrice = item["midPrice"]?.Value<double?>(),
                    };
                }
            }
            // Fall back to first result
            var first = json["results"]?.First;
            if (first == null) return null;
            return new TcgPlayerPrice
            {
                MarketPrice = first["marketPrice"]?.Value<double?>(),
                LowPrice = first["lowPrice"]?.Value<double?>(),
                MidPrice = first["midPrice"]?.Value<double?>(),
            };
        }
    }
}
