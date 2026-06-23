using System;
using System.IO;
using Newtonsoft.Json;

namespace CardProgram.Services
{
    public class AppSettings
    {
        public string TcgPlayerPublicKey { get; set; } = string.Empty;
        public string TcgPlayerPrivateKey { get; set; } = string.Empty;
    }

    public class AppSettingsService
    {
        private readonly string _path;

        public AppSettingsService()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CardProgram");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_path)) return new AppSettings();
            try { return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings(); }
            catch { return new AppSettings(); }
        }

        public void Save(AppSettings settings)
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
    }
}
