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
        public int MicDelayMs { get; set; } = 11;

        public float BrowserVolume { get; set; } = 1.0f;
        public bool IsBrowserMuted { get; set; } = false;
        public int BrowserKeySemitones { get; set; } = 0;

        public float MasterVolume { get; set; } = 1.0f;
        public bool IsMasterMuted { get; set; } = false;

        public bool IsMonitoringEnabled { get; set; } = false;
        public float MonitorVolume { get; set; } = 1.0f;
        public int MonitorSourceMode { get; set; } = 0; // 0 = Music (Default), 1 = Mic, 2 = Both

        public int VoiceEffectMode { get; set; } = 0; // 0=Off, 1=RoomIsolate, 2=Echo, 3=Telephone, 4=Radio, 5=HallReverb
        public float VoiceEffectIntensity { get; set; } = 0.75f; // 0.0..1.0

        public int LatencyMs { get; set; } = 30;
        public int BridgePort { get; set; } = 49876;

        public bool MinimizeToTray { get; set; } = true;
        public bool AutoReconnect { get; set; } = true;
        public bool HearTabInBrowser { get; set; } = true;
    }
}
