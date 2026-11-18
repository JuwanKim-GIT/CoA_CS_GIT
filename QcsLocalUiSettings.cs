using System;
using System.IO;
using System.Text.Json;

namespace QCS
{
    // Per-Windows-user settings. No database or shared project files are modified.
    internal sealed class QcsLocalUiSettings
    {
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; } = true;
        public string LastTab { get; set; } = "";
        public string MaterialType { get; set; } = "FG";
        public bool? FilesVisible { get; set; }
        public bool? PreviewVisible { get; set; }
        public double? FilesWidth { get; set; }
        public double? EditorStarWidth { get; set; }
        public double? PreviewStarWidth { get; set; }
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QCS", "ui-settings.json");
        public static QcsLocalUiSettings Load()
        {
            try { return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<QcsLocalUiSettings>(File.ReadAllText(SettingsPath)) ?? new QcsLocalUiSettings()
                : new QcsLocalUiSettings(); }
            catch { return new QcsLocalUiSettings(); }
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, SettingsPath, true);
            }
            catch { /* Settings must not interrupt business operations. */ }
        }
    }
}
