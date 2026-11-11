using System;

namespace MixerOnline.Core.Config
{
    public sealed class AppSettings
    {
        public string? SelectedMicId { get; set; }
        public string? SelectedOutputId { get; set; }
        public string? SelectedMonitorId { get; set; }

        public float MicVolume { get; set; } = 1.0f;
        public bool IsMicMuted { get; set; } = false;
        public int MicDelayMs { get; set; } = 45;

        public float BrowserVolume { get; set; } = 1.0f;
        public bool IsBrowserMuted { get; set; } = false;
        public int BrowserKeySemitones { get; set; } = 0;

        public float MasterVolume { get; set; } = 1.0f;
        public bool IsMasterMuted { get; set; } = false;

        public bool IsMonitoringEnabled { get; set; } = false;
        public float MonitorVolume { get; set; } = 1.0f;

        public int LatencyMs { get; set; } = 30;
        public int BridgePort { get; set; } = 49876;

        public bool MinimizeToTray { get; set; } = true;
        public bool AutoReconnect { get; set; } = true;
        public bool HearTabInBrowser { get; set; } = true;
    }
}
