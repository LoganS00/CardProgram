using System;
using System.Collections.Generic;
using System.Linq;

namespace CardProgram.Models
{
    public class WatchedCard
    {
        public string Id           { get; set; } = Guid.NewGuid().ToString();
        public string Name         { get; set; } = string.Empty;
        public string SetName      { get; set; } = string.Empty;
        public string TcgPlayerUrl { get; set; } = string.Empty;
        public string ImageFileName{ get; set; } = string.Empty;
        public double? MarketPrice { get; set; }
        public double? TargetPrice { get; set; }
        public DateTime AddedAt    { get; set; } = DateTime.Now;
        public DateTime? PriceUpdatedAt { get; set; }
        public List<PricePoint> PriceHistory { get; set; } = new();

        public void RecordPrice(double price)
        {
            MarketPrice = price;
            PriceUpdatedAt = DateTime.Now;
            PriceHistory.Add(new PricePoint { Date = DateTime.Now, Price = price });
        }

        // Change vs ~1 month ago
        public double? OneMonthChange()
        {
            if (MarketPrice == null || PriceHistory.Count < 2) return null;
            var cutoff = DateTime.Now.AddMonths(-1);
            var old = PriceHistory.Where(p => p.Date <= cutoff).OrderByDescending(p => p.Date).FirstOrDefault()
                   ?? PriceHistory.OrderBy(p => p.Date).First();
            return MarketPrice - old.Price;
        }

        // Change since the previous refresh (last two price points)
        public double? LastRefreshChange()
        {
            if (PriceHistory.Count < 2) return null;
            var sorted = PriceHistory.OrderBy(p => p.Date).ToList();
            return sorted[^1].Price - sorted[^2].Price;
        }
    }
}
