using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using CardProgram.Models;
using Newtonsoft.Json;

namespace CardProgram.Services
{
    public class CardStorageService
    {
        private readonly string _dataDir;
        private readonly string _imagesDir;
        private readonly string _manifestPath;
        private readonly string _foldersPath;
        private readonly string _watchlistPath;

        public CardStorageService()
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CardProgram");
            _imagesDir     = Path.Combine(_dataDir, "cards");
            _manifestPath  = Path.Combine(_dataDir, "collection.json");
            _foldersPath   = Path.Combine(_dataDir, "folders.json");
            _watchlistPath = Path.Combine(_dataDir, "watchlist.json");

            Directory.CreateDirectory(_imagesDir);
        }

        public List<WatchedCard> LoadWatchlist()
        {
            if (!File.Exists(_watchlistPath)) return new List<WatchedCard>();
            return JsonConvert.DeserializeObject<List<WatchedCard>>(File.ReadAllText(_watchlistPath)) ?? new List<WatchedCard>();
        }

        public void SaveWatchlist(List<WatchedCard> list)
        {
            File.WriteAllText(_watchlistPath, JsonConvert.SerializeObject(list, Formatting.Indented));
        }

        public List<Folder> LoadFolders()
        {
            if (!File.Exists(_foldersPath)) return new List<Folder>();
            var json = File.ReadAllText(_foldersPath);
            return JsonConvert.DeserializeObject<List<Folder>>(json) ?? new List<Folder>();
        }

        public void SaveFolders(List<Folder> folders)
        {
            File.WriteAllText(_foldersPath, JsonConvert.SerializeObject(folders, Formatting.Indented));
        }

        public List<Card> LoadCollection()
        {
            if (!File.Exists(_manifestPath))
                return new List<Card>();

            var json = File.ReadAllText(_manifestPath);
            return JsonConvert.DeserializeObject<List<Card>>(json) ?? new List<Card>();
        }

        public void SaveCollection(List<Card> cards)
        {
            var json = JsonConvert.SerializeObject(cards, Formatting.Indented);
            File.WriteAllText(_manifestPath, json);
        }

        public string SaveCardImage(BitmapSource image, string cardId)
        {
            var fileName = $"{cardId}.png";
            var filePath = Path.Combine(_imagesDir, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.OpenWrite(filePath);
            encoder.Save(stream);

            return fileName;
        }

        public string GetImagePath(string fileName)
        {
            return Path.Combine(_imagesDir, fileName);
        }

        public void DeleteCard(Card card)
        {
            var imagePath = GetImagePath(card.ImageFileName);
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
    }
}
