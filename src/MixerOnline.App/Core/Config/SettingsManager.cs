using System;
using System.IO;
using System.Text.Json;

namespace MixerOnline.Core.Config
{
    public static class SettingsManager
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MixerOnline");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        // Sanitize legacy over-boosted gain values that caused clipping
                        if (settings.BrowserVolume > 1.5f) settings.BrowserVolume = 1.0f;
                        if (settings.MicVolume > 2.0f) settings.MicVolume = 1.0f;
                        if (settings.MasterVolume > 1.5f) settings.MasterVolume = 1.0f;
                        settings.LatencyMs = Math.Clamp(settings.LatencyMs, 20, 200);
                        settings.MicDelayMs = Math.Clamp(settings.MicDelayMs, 0, 300);
                        settings.BrowserKeySemitones = Math.Clamp(settings.BrowserKeySemitones, -6, 6);
                        // Start with monitoring off to prevent accidental mic slapback or double playback
                        settings.IsMonitoringEnabled = false;
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsManager] Load error: {ex.Message}");
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsManager] Save error: {ex.Message}");
            }
        }
    }
}
