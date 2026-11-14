using System;
using System.IO;
using System.Text.Json;

namespace MixerOnline.Core.Config
{
    public static class SettingsManager
    {
        private static readonly string AppDataDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        private static readonly string OfficialSettingsDirectory =
            Path.Combine(AppDataDirectory, "MixerOnline");

        private static readonly string OfficialSettingsFilePath =
            Path.Combine(OfficialSettingsDirectory, "settings.json");

        private static readonly string BetaSettingsDirectory =
            Path.Combine(AppDataDirectory, "MixerOnline-beta");

        private static readonly string LegacyBetaSettingsFilePath =
            Path.Combine(BetaSettingsDirectory, "settings.json");

        private static readonly string BetaExtrasFilePath =
            Path.Combine(BetaSettingsDirectory, "beta-settings.json");

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        private static readonly object IoLock = new();

        private sealed class CoreSettingsDto
        {
            public string? SelectedMicId { get; set; }
            public string? SelectedOutputId { get; set; }
            public string? SelectedMonitorId { get; set; }

            public float MicVolume { get; set; } = 1.0f;
            public bool IsMicMuted { get; set; }
            public int MicDelayMs { get; set; } = 11;

            public float BrowserVolume { get; set; } = 1.0f;
            public bool IsBrowserMuted { get; set; }
            public int BrowserKeySemitones { get; set; }

            public float MasterVolume { get; set; } = 1.0f;
            public bool IsMasterMuted { get; set; }

            public bool IsMonitoringEnabled { get; set; }
            public float MonitorVolume { get; set; } = 1.0f;

            public int LatencyMs { get; set; } = 30;
            public int BridgePort { get; set; } = 49876;

            public bool MinimizeToTray { get; set; } = true;
            public bool AutoReconnect { get; set; } = true;
            public bool HearTabInBrowser { get; set; } = true;
        }

        private sealed class BetaExtras
        {
            public int MonitorSourceMode { get; set; }
            public int VoiceEffectMode { get; set; }
            public float VoiceEffectIntensity { get; set; } = 0.75f;
            public bool AutoTuneEnabled { get; set; }
            public int AutoTuneKey { get; set; }
            public int AutoTuneScale { get; set; }
            public float AutoTuneSpeed { get; set; } = 0.20f;
            public float AutoTuneMix { get; set; } = 1.0f;
        }

        public static AppSettings Load()
        {
            AppSettings? settings = null;

            lock (IoLock)
            {
                try
                {
                    if (File.Exists(OfficialSettingsFilePath))
                    {
                        string json = File.ReadAllText(OfficialSettingsFilePath);
                        settings = JsonSerializer.Deserialize<AppSettings>(json);
                    }
                    else if (File.Exists(LegacyBetaSettingsFilePath))
                    {
                        string json = File.ReadAllText(LegacyBetaSettingsFilePath);
                        settings = JsonSerializer.Deserialize<AppSettings>(json);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SettingsManager] Core load error: {ex.Message}");
                }

                settings ??= new AppSettings();

                try
                {
                    if (File.Exists(BetaExtrasFilePath))
                    {
                        string extrasJson = File.ReadAllText(BetaExtrasFilePath);
                        var extras = JsonSerializer.Deserialize<BetaExtras>(extrasJson);
                        if (extras != null)
                        {
                            settings.MonitorSourceMode = extras.MonitorSourceMode;
                            settings.VoiceEffectMode = extras.VoiceEffectMode;
                            settings.VoiceEffectIntensity = extras.VoiceEffectIntensity;
                            settings.AutoTuneEnabled = extras.AutoTuneEnabled;
                            settings.AutoTuneKey = extras.AutoTuneKey;
                            settings.AutoTuneScale = extras.AutoTuneScale;
                            settings.AutoTuneSpeed = extras.AutoTuneSpeed;
                            settings.AutoTuneMix = extras.AutoTuneMix;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SettingsManager] Beta extras load error: {ex.Message}");
                }
            }

            Sanitize(settings);
            return settings;
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null) return;

            var coreSettings = new CoreSettingsDto
            {
                SelectedMicId = settings.SelectedMicId,
                SelectedOutputId = settings.SelectedOutputId,
                SelectedMonitorId = settings.SelectedMonitorId,
                MicVolume = settings.MicVolume,
                IsMicMuted = settings.IsMicMuted,
                MicDelayMs = settings.MicDelayMs,
                BrowserVolume = settings.BrowserVolume,
                IsBrowserMuted = settings.IsBrowserMuted,
                BrowserKeySemitones = settings.BrowserKeySemitones,
                MasterVolume = settings.MasterVolume,
                IsMasterMuted = settings.IsMasterMuted,
                IsMonitoringEnabled = settings.IsMonitoringEnabled,
                MonitorVolume = settings.MonitorVolume,
                LatencyMs = settings.LatencyMs,
                BridgePort = settings.BridgePort,
                MinimizeToTray = settings.MinimizeToTray,
                AutoReconnect = settings.AutoReconnect,
                HearTabInBrowser = settings.HearTabInBrowser
            };

            var betaExtras = new BetaExtras
            {
                MonitorSourceMode = Math.Clamp(settings.MonitorSourceMode, 0, 2),
                VoiceEffectMode = Math.Clamp(settings.VoiceEffectMode, 0, 5),
                VoiceEffectIntensity = float.IsFinite(settings.VoiceEffectIntensity)
                    ? Math.Clamp(settings.VoiceEffectIntensity, 0f, 1f)
                    : 0.75f,
                AutoTuneEnabled = settings.AutoTuneEnabled,
                AutoTuneKey = Math.Clamp(settings.AutoTuneKey, 0, 11),
                AutoTuneScale = Math.Clamp(settings.AutoTuneScale, 0, 2),
                AutoTuneSpeed = float.IsFinite(settings.AutoTuneSpeed)
                    ? Math.Clamp(settings.AutoTuneSpeed, 0f, 1f)
                    : 0.20f,
                AutoTuneMix = float.IsFinite(settings.AutoTuneMix)
                    ? Math.Clamp(settings.AutoTuneMix, 0f, 1f)
                    : 1.0f
            };

            lock (IoLock)
            {
                WriteJsonAtomically(OfficialSettingsDirectory, OfficialSettingsFilePath, coreSettings, "core");
                WriteJsonAtomically(BetaSettingsDirectory, BetaExtrasFilePath, betaExtras, "beta extras");
            }
        }

        private static void Sanitize(AppSettings settings)
        {
            // Sanitize legacy over-boosted gain values or non-finite floats that could cause clipping
            if (!float.IsFinite(settings.BrowserVolume) || settings.BrowserVolume < 0f || settings.BrowserVolume > 1.5f)
            {
                settings.BrowserVolume = 1.0f;
            }
            if (!float.IsFinite(settings.MicVolume) || settings.MicVolume < 0f || settings.MicVolume > 2.0f)
            {
                settings.MicVolume = 1.0f;
            }
            if (!float.IsFinite(settings.MasterVolume) || settings.MasterVolume < 0f || settings.MasterVolume > 1.5f)
            {
                settings.MasterVolume = 1.0f;
            }
            if (!float.IsFinite(settings.MonitorVolume) || settings.MonitorVolume < 0f || settings.MonitorVolume > 2.0f)
            {
                settings.MonitorVolume = 1.0f;
            }

            settings.LatencyMs = Math.Clamp(settings.LatencyMs, 15, 200);
            settings.MicDelayMs = Math.Clamp(settings.MicDelayMs, 0, 300);
            settings.BrowserKeySemitones = Math.Clamp(settings.BrowserKeySemitones, -6, 6);
            settings.MonitorSourceMode = Math.Clamp(settings.MonitorSourceMode, 0, 2);
            settings.VoiceEffectMode = Math.Clamp(settings.VoiceEffectMode, 0, 5);
            settings.VoiceEffectIntensity = float.IsFinite(settings.VoiceEffectIntensity)
                ? Math.Clamp(settings.VoiceEffectIntensity, 0f, 1f)
                : 0.75f;
            settings.AutoTuneKey = Math.Clamp(settings.AutoTuneKey, 0, 11);
            settings.AutoTuneScale = Math.Clamp(settings.AutoTuneScale, 0, 2);
            settings.AutoTuneSpeed = float.IsFinite(settings.AutoTuneSpeed)
                ? Math.Clamp(settings.AutoTuneSpeed, 0f, 1f)
                : 0.20f;
            settings.AutoTuneMix = float.IsFinite(settings.AutoTuneMix)
                ? Math.Clamp(settings.AutoTuneMix, 0f, 1f)
                : 1.0f;

            // Start with monitoring off to prevent accidental mic slapback or double playback
            settings.IsMonitoringEnabled = false;
        }

        private static void WriteJsonAtomically<T>(string directory, string filePath, T payload, string label)
        {
            string? tempFilePath = null;
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(payload, SerializerOptions);
                tempFilePath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempFilePath, json);
                File.Move(tempFilePath, filePath, overwrite: true);
                tempFilePath = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsManager] Save {label} error: {ex.Message}");
            }
            finally
            {
                if (tempFilePath != null)
                {
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
