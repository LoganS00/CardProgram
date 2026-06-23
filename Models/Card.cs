using System;
using System.Collections.Generic;

namespace CardProgram.Models
{
    public class PricePoint
    {
        public DateTime Date { get; set; }
        public double Price { get; set; }
    }

    public class Card
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string ImageFileName { get; set; } = string.Empty;
        public DateTime CapturedAt { get; set; } = DateTime.Now;
        public string Notes { get; set; } = string.Empty;

        public string TcgPlayerUrl { get; set; } = string.Empty;
        public string TcgPlayerSetName { get; set; } = string.Empty;
        public double? MarketPrice { get; set; }
        public double? LowPrice { get; set; }
        public DateTime? PriceUpdatedAt { get; set; }

        public string CardType { get; set; } = "Foil";
        public int Quantity { get; set; } = 1;
        public string? FolderId { get; set; }
        public List<PricePoint> PriceHistory { get; set; } = new();

        public bool IsLinked => !string.IsNullOrWhiteSpace(TcgPlayerUrl);

        public void RecordPrice(double price)
        {
            MarketPrice = price;
            PriceUpdatedAt = DateTime.Now;
            PriceHistory.Add(new PricePoint { Date = DateTime.Now, Price = price });
        }
    }
}
