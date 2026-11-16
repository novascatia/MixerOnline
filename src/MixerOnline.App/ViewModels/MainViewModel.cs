using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MixerOnline.Core.Audio;
using MixerOnline.Core.Config;
using MixerOnline.Core.Network;
using MixerOnline.Core.Update;
using MixerOnline.Core.Utils;
using Application = System.Windows.Application;

namespace MixerOnline.ViewModels
{
    public sealed class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly AudioEngine _engine;
        private readonly AppSettings _settings;
        private readonly DispatcherTimer _meterTimer;

        // Observable Collections
        public ObservableCollection<AudioDeviceInfo> Microphones { get; } = new();
        public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();
        public ObservableCollection<TabItem> BraveTabs { get; } = new();

        // Selections
        private AudioDeviceInfo? _selectedMic;
        private AudioDeviceInfo? _selectedOutput;
        private AudioDeviceInfo? _selectedMonitor;
        private TabItem? _selectedTab;
        private int _activeCapturedTabId;
        private bool _isUpdatingTabList;

        // Volumes and Mutes
        private float _micVolume = 1.0f;
        private bool _isMicMuted;
        private int _micDelayMs = 45;
        private int _voiceEffectMode = 0;
        private float _voiceEffectIntensity = 0.75f;
        private bool _isVoiceFxExpanded;
        private bool _isAutoTuneEnabled;
        private bool _isAutoTuneExpanded;
        private int _autoTuneKeyIndex;
        private int _autoTuneScaleIndex;
        private float _autoTuneSpeed = 0.20f;
        private float _autoTuneMix = 1.0f;
        private string _autoTuneNoteDisplay = "--";
        private string _autoTuneNoteColor = "#71717a";
        private float _browserVolume = 1.0f;
        private bool _isBrowserMuted;
        private int _browserKeySemitones;
        private float _masterVolume = 1.0f;
        private bool _isMasterMuted;

        private bool _isMonitoringEnabled;
        private float _monitorVolume = 1.0f;
        private int _monitorSourceMode = 0; // 0 = Music (Default), 1 = Mic, 2 = Both

        // Meter Levels
        private float _micPeak;
        private float _micRms;
        private float _browserPeak;
        private float _browserRms;
        private float _masterPeakL;
        private float _masterPeakR;
        private float _masterRms;

        // Status
        private bool _isBraveConnected;
        private string _braveConnectionText = "Extension Offline";
        private string _capturedTabTitle = "No Active Tab";
        private bool _isTabCapturing;
        private bool _isVirtualCableDetected;
        private bool _vbWarningShown;
        private string _statusText = "Ready";
        private string _engineStatusText = "48.0 kHz • 32-Bit Float";

        // Auto-Update State
        private readonly UpdateService _updateService = new();
        private bool _isUpdateAvailable;
        private string _updateAvailableButtonText = "✨ Update Available";
        private UpdateReleaseInfo? _availableRelease;

        public Action? RequestOpenUpdate { get; set; }

        public string AppVersion => MixerOnline.Views.SettingsDialog.CurrentAppVersion;

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set
            {
                if (SetProperty(ref _isUpdateAvailable, value))
                {
                    OnPropertyChanged(nameof(UpdatePillVisibility));
                }
            }
        }

        public Visibility UpdatePillVisibility => _isUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

        public string UpdateAvailableButtonText
        {
            get => _updateAvailableButtonText;
            private set => SetProperty(ref _updateAvailableButtonText, value);
        }

        public UpdateReleaseInfo? AvailableRelease
        {
            get => _availableRelease;
            private set => SetProperty(ref _availableRelease, value);
        }

        // Commands
        public ICommand RefreshTabsCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand ToggleMicMuteCommand { get; }
        public ICommand ToggleBrowserMuteCommand { get; }
        public ICommand ToggleMasterMuteCommand { get; }
        public ICommand ToggleMonitoringCommand { get; }
        public ICommand SetMonitorSourceCommand { get; }
        public ICommand ToggleSampleRateModeCommand { get; }
        public ICommand SetMicDelayPresetCommand { get; }
        public ICommand SetVoiceEffectCommand { get; }
        public ICommand ToggleVoiceFxExpandedCommand { get; }
        public ICommand ToggleAutoTuneCommand { get; }
        public ICommand ToggleAutoTuneExpandedCommand { get; }
        public ICommand PitchShiftDownCommand { get; }
        public ICommand PitchShiftUpCommand { get; }
        public ICommand ResetPitchCommand { get; }
        public ICommand StopTabCaptureCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenGuideCommand { get; }
        public ICommand OpenUpdateDialogCommand { get; }
        public ICommand OpenExtensionFolderCommand { get; }
        public ICommand OpenBraveExtensionsCommand { get; }

        public string BrowserSampleRateText => _engine.BrowserReceiver.SampleRateStatusText;

        public AudioEngine Engine => _engine;
        public AppSettings Settings => _settings;

        public AudioDeviceInfo? SelectedMic
        {
            get => _selectedMic;
            set
            {
                if (SetProperty(ref _selectedMic, value) && value != null)
                {
                    _engine.SelectMicrophone(value.Id);
                    _settings.SelectedMicId = value.Id;
                    SaveSettings();
                }
            }
        }

        public AudioDeviceInfo? SelectedOutput
        {
            get => _selectedOutput;
            set
            {
                if (SetProperty(ref _selectedOutput, value) && value != null)
                {
                    _engine.SelectOutput(value.Id);
                    _settings.SelectedOutputId = value.Id;
                    SaveSettings();
                }
            }
        }

        public AudioDeviceInfo? SelectedMonitor
        {
            get => _selectedMonitor;
            set
            {
                if (SetProperty(ref _selectedMonitor, value) && value != null)
                {
                    _engine.SelectMonitor(value.Id);
                    _settings.SelectedMonitorId = value.Id;
                    SaveSettings();
                }
            }
        }

        public TabItem? SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value) && value != null && !_isUpdatingTabList)
                {
                    if (value.Id != _activeCapturedTabId || !_isTabCapturing)
                    {
                        _activeCapturedTabId = value.Id;
                        _ = _engine.SelectBrowserTabAsync(value.Id);
                        CapturedTabTitle = value.Title;
                    }
                }
            }
        }

        public float MicVolume
        {
            get => _micVolume;
            set
            {
                if (SetProperty(ref _micVolume, value))
                {
                    _engine.MixerBus.MicrophoneVolume = value;
                    _settings.MicVolume = value;
                    OnPropertyChanged(nameof(MicVolumePercentText));
                    SaveSettings();
                }
            }
        }

        public string MicVolumePercentText => $"{(int)(MicVolume * 100)}%";

        public bool IsMicMuted
        {
            get => _isMicMuted;
            set
            {
                if (SetProperty(ref _isMicMuted, value))
                {
                    _engine.MixerBus.IsMicrophoneMuted = value;
                    _settings.IsMicMuted = value;
                    SaveSettings();
                }
            }
        }

        public int MicDelayMs
        {
            get => _micDelayMs;
            set
            {
                int clamped = Math.Clamp(value, 0, 300);
                if (SetProperty(ref _micDelayMs, clamped))
                {
                    _engine.MicDelayMs = clamped;
                    _settings.MicDelayMs = clamped;
                    OnPropertyChanged(nameof(MicDelayText));
                    SaveSettings();
                }
            }
        }

        public string MicDelayText => _micDelayMs == 0 ? "0 ms (Direct)" : $"+{_micDelayMs} ms";

        public int VoiceEffectMode
        {
            get => _voiceEffectMode;
            set
            {
                int clamped = Math.Clamp(value, 0, 5);
                if (SetProperty(ref _voiceEffectMode, clamped))
                {
                    _engine.VoiceEffectMode = clamped;
                    _settings.VoiceEffectMode = clamped;
                    OnPropertyChanged(nameof(IsVoiceFxOff));
                    OnPropertyChanged(nameof(IsVoiceFxRoomIsolate));
                    OnPropertyChanged(nameof(IsVoiceFxEcho));
                    OnPropertyChanged(nameof(IsVoiceFxTelephone));
                    OnPropertyChanged(nameof(IsVoiceFxRadio));
                    OnPropertyChanged(nameof(IsVoiceFxHall));
                    OnPropertyChanged(nameof(VoiceEffectStatusText));
                    SaveSettings();
                }
            }
        }

        public float VoiceEffectIntensity
        {
            get => _voiceEffectIntensity;
            set
            {
                float sanitized = float.IsFinite(value) ? value : 0.75f;
                float clamped = Math.Clamp(sanitized, 0.0f, 1.0f);
                if (SetProperty(ref _voiceEffectIntensity, clamped))
                {
                    _engine.VoiceEffectIntensity = clamped;
                    _settings.VoiceEffectIntensity = clamped;
                    OnPropertyChanged(nameof(VoiceEffectIntensityText));
                    SaveSettings();
                }
            }
        }

        public bool IsVoiceFxOff => _voiceEffectMode == 0;
        public bool IsVoiceFxRoomIsolate => _voiceEffectMode == 1;
        public bool IsVoiceFxEcho => _voiceEffectMode == 2;
        public bool IsVoiceFxTelephone => _voiceEffectMode == 3;
        public bool IsVoiceFxRadio => _voiceEffectMode == 4;
        public bool IsVoiceFxHall => _voiceEffectMode == 5;

        public string VoiceEffectStatusText => _voiceEffectMode switch
        {
            0 => "Clean",
            1 => "Room Isolate",
            2 => "Echo",
            3 => "Telephone",
            4 => "Radio",
            5 => "Hall",
            _ => "Clean"
        };

        public string VoiceEffectIntensityText => $"{(int)Math.Round(_voiceEffectIntensity * 100)}%";

        public bool IsVoiceFxExpanded
        {
            get => _isVoiceFxExpanded;
            set
            {
                if (SetProperty(ref _isVoiceFxExpanded, value))
                {
                    OnPropertyChanged(nameof(VoiceFxContentVisibility));
                    OnPropertyChanged(nameof(VoiceFxChevron));
                    _settings.IsVoiceFxExpanded = value;
                    SaveSettings();
                }
            }
        }

        public Visibility VoiceFxContentVisibility => _isVoiceFxExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string VoiceFxChevron => _isVoiceFxExpanded ? "▲" : "▼";

        public static readonly string[] KeyOptions =
        {
            "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
        };

        public static readonly string[] ScaleOptions =
        {
            "Chromatic (All Notes)", "Major Scale", "Minor Scale"
        };

        public IReadOnlyList<string> AutoTuneKeyOptions => KeyOptions;
        public IReadOnlyList<string> AutoTuneScaleOptions => ScaleOptions;

        public bool IsAutoTuneEnabled
        {
            get => _isAutoTuneEnabled;
            set
            {
                if (SetProperty(ref _isAutoTuneEnabled, value))
                {
                    _engine.AutoTuneEnabled = value;
                    _settings.AutoTuneEnabled = value;
                    OnPropertyChanged(nameof(AutoTuneStatusText));
                    SaveSettings();
                }
            }
        }

        public string AutoTuneStatusText => _isAutoTuneEnabled ? "ON" : "OFF";

        public int AutoTuneKeyIndex
        {
            get => _autoTuneKeyIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, 11);
                if (SetProperty(ref _autoTuneKeyIndex, clamped))
                {
                    _engine.AutoTuneKey = clamped;
                    _settings.AutoTuneKey = clamped;
                    SaveSettings();
                }
            }
        }

        public int AutoTuneScaleIndex
        {
            get => _autoTuneScaleIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, 2);
                if (SetProperty(ref _autoTuneScaleIndex, clamped))
                {
                    _engine.AutoTuneScale = clamped;
                    _settings.AutoTuneScale = clamped;
                    SaveSettings();
                }
            }
        }

        public float AutoTuneSpeed
        {
            get => _autoTuneSpeed;
            set
            {
                float sanitized = float.IsFinite(value) ? value : 0.20f;
                float clamped = Math.Clamp(sanitized, 0.0f, 1.0f);
                if (SetProperty(ref _autoTuneSpeed, clamped))
                {
                    _engine.AutoTuneSpeed = clamped;
                    _settings.AutoTuneSpeed = clamped;
                    OnPropertyChanged(nameof(AutoTuneSpeedText));
                    SaveSettings();
                }
            }
        }

        public string AutoTuneSpeedText => _autoTuneSpeed switch
        {
            <= 0.05f => "Hard Snap",
            <= 0.25f => "Fast (Tune)",
            <= 0.60f => "Natural",
            _ => "Soft"
        };

        public float AutoTuneMix
        {
            get => _autoTuneMix;
            set
            {
                float sanitized = float.IsFinite(value) ? value : 1.0f;
                float clamped = Math.Clamp(sanitized, 0.0f, 1.0f);
                if (SetProperty(ref _autoTuneMix, clamped))
                {
                    _engine.AutoTuneMix = clamped;
                    _settings.AutoTuneMix = clamped;
                    OnPropertyChanged(nameof(AutoTuneMixText));
                    SaveSettings();
                }
            }
        }

        public string AutoTuneMixText => $"{(int)Math.Round(_autoTuneMix * 100)}%";

        public bool IsAutoTuneExpanded
        {
            get => _isAutoTuneExpanded;
            set
            {
                if (SetProperty(ref _isAutoTuneExpanded, value))
                {
                    OnPropertyChanged(nameof(AutoTuneContentVisibility));
                    OnPropertyChanged(nameof(AutoTuneChevron));
                    _settings.IsAutoTuneExpanded = value;
                    SaveSettings();
                }
            }
        }

        public Visibility AutoTuneContentVisibility => _isAutoTuneExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string AutoTuneChevron => _isAutoTuneExpanded ? "▲" : "▼";

        public string AutoTuneNoteDisplay
        {
            get => _autoTuneNoteDisplay;
            private set => SetProperty(ref _autoTuneNoteDisplay, value);
        }

        public string AutoTuneNoteColor
        {
            get => _autoTuneNoteColor;
            private set => SetProperty(ref _autoTuneNoteColor, value);
        }

        public float BrowserVolume
        {
            get => _browserVolume;
            set
            {
                if (SetProperty(ref _browserVolume, value))
                {
                    _engine.MixerBus.BrowserVolume = value;
                    _settings.BrowserVolume = value;
                    OnPropertyChanged(nameof(BrowserVolumePercentText));
                    SaveSettings();
                }
            }
        }

        public string BrowserVolumePercentText => $"{(int)(BrowserVolume * 100)}%";

        public bool IsBrowserMuted
        {
            get => _isBrowserMuted;
            set
            {
                if (SetProperty(ref _isBrowserMuted, value))
                {
                    _engine.MixerBus.IsBrowserMuted = value;
                    _settings.IsBrowserMuted = value;
                    SaveSettings();
                }
            }
        }

        public int BrowserKeySemitones
        {
            get => _browserKeySemitones;
            set
            {
                int clamped = Math.Clamp(value, -6, 6);
                if (SetProperty(ref _browserKeySemitones, clamped))
                {
                    _engine.BrowserPitchSemitones = clamped;
                    _settings.BrowserKeySemitones = clamped;
                    OnPropertyChanged(nameof(BrowserKeySemitonesText));
                    SaveSettings();
                }
            }
        }

        public string BrowserKeySemitonesText => _browserKeySemitones switch
        {
            > 0 => $"+{_browserKeySemitones}♯ Up",
            < 0 => $"{_browserKeySemitones}♭ Down",
            _ => "0 (Original Key)"
        };

        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                if (SetProperty(ref _masterVolume, value))
                {
                    _engine.MixerBus.MasterVolume = value;
                    _settings.MasterVolume = value;
                    OnPropertyChanged(nameof(MasterVolumePercentText));
                    SaveSettings();
                }
            }
        }

        public string MasterVolumePercentText => $"{(int)(MasterVolume * 100)}%";

        public bool IsMasterMuted
        {
            get => _isMasterMuted;
            set
            {
                if (SetProperty(ref _isMasterMuted, value))
                {
                    _engine.MixerBus.IsMasterMuted = value;
                    _settings.IsMasterMuted = value;
                    SaveSettings();
                }
            }
        }

        public bool IsMonitoringEnabled
        {
            get => _isMonitoringEnabled;
            set
            {
                if (SetProperty(ref _isMonitoringEnabled, value))
                {
                    _engine.SetMonitoring(value);
                    UpdateBrowserPassthrough();
                    _settings.IsMonitoringEnabled = value;
                    SaveSettings();
                }
            }
        }

        public float MonitorVolume
        {
            get => _monitorVolume;
            set
            {
                if (SetProperty(ref _monitorVolume, value))
                {
                    _engine.OutputRouter.MonitorProvider.Volume = value;
                    _settings.MonitorVolume = value;
                    OnPropertyChanged(nameof(MonitorVolumePercentText));
                    SaveSettings();
                }
            }
        }

        public string MonitorVolumePercentText => $"{(int)(MonitorVolume * 100)}%";

        public int MonitorSourceMode
        {
            get => _monitorSourceMode;
            set
            {
                int clamped = Math.Clamp(value, 0, 2);
                if (SetProperty(ref _monitorSourceMode, clamped))
                {
                    _engine.MonitorSourceMode = clamped;
                    _settings.MonitorSourceMode = clamped;
                    UpdateBrowserPassthrough();
                    OnPropertyChanged(nameof(IsMonitorSourceMusic));
                    OnPropertyChanged(nameof(IsMonitorSourceMic));
                    OnPropertyChanged(nameof(IsMonitorSourceBoth));
                    OnPropertyChanged(nameof(MonitorSourceText));
                    SaveSettings();
                }
            }
        }

        public bool IsMonitorSourceMusic => _monitorSourceMode == 0;
        public bool IsMonitorSourceMic => _monitorSourceMode == 1;
        public bool IsMonitorSourceBoth => _monitorSourceMode == 2;

        public string MonitorSourceText => _monitorSourceMode switch
        {
            0 => "Music Only",
            1 => "Mic Only",
            2 => "Both (Full Mix)",
            _ => "Music Only"
        };

        private void UpdateBrowserPassthrough()
        {
            // When monitoring is off or set to Mic Only (1), preserve the user's HearTabInBrowser preference.
            // When monitoring Music (0) or Both (2), mute direct browser playback to prevent double-audio slapback.
            bool passthrough = !_isMonitoringEnabled || _monitorSourceMode == 1
                ? _settings.HearTabInBrowser
                : false;
            _ = _engine.BridgeServer.SetBrowserPassthroughAsync(passthrough);
        }

        public float MicPeak
        {
            get => _micPeak;
            private set => SetProperty(ref _micPeak, value);
        }

        public float MicRms
        {
            get => _micRms;
            private set => SetProperty(ref _micRms, value);
        }

        public float BrowserPeak
        {
            get => _browserPeak;
            private set => SetProperty(ref _browserPeak, value);
        }

        public float BrowserRms
        {
            get => _browserRms;
            private set => SetProperty(ref _browserRms, value);
        }

        public float MasterPeakL
        {
            get => _masterPeakL;
            private set => SetProperty(ref _masterPeakL, value);
        }

        public float MasterPeakR
        {
            get => _masterPeakR;
            private set => SetProperty(ref _masterPeakR, value);
        }

        public float MasterRms
        {
            get => _masterRms;
            private set => SetProperty(ref _masterRms, value);
        }

        // Property names preserved as *Brave* for XAML binding compatibility across supported Chromium browsers.
        public bool IsBraveConnected
        {
            get => _isBraveConnected;
            private set => SetProperty(ref _isBraveConnected, value);
        }

        public string BraveConnectionText
        {
            get => _braveConnectionText;
            private set => SetProperty(ref _braveConnectionText, value);
        }

        public string CapturedTabTitle
        {
            get => _capturedTabTitle;
            private set => SetProperty(ref _capturedTabTitle, value);
        }

        public bool IsTabCapturing
        {
            get => _isTabCapturing;
            private set => SetProperty(ref _isTabCapturing, value);
        }

        public bool IsVirtualCableDetected
        {
            get => _isVirtualCableDetected;
            private set => SetProperty(ref _isVirtualCableDetected, value);
        }

        public string VirtualCableBadgeText => IsVirtualCableDetected
            ? "VB-Cable Ready"
            : "Speaker Output";

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string EngineStatusText
        {
            get => _engineStatusText;
            set => SetProperty(ref _engineStatusText, value);
        }

        public Action? RequestOpenSettings { get; set; }
        public Action? RequestOpenGuide { get; set; }

        public MainViewModel()
        {
            _settings = SettingsManager.Load();
            _engine = new AudioEngine(_settings.BridgePort, _settings.LatencyMs);

            RefreshTabsCommand = new RelayCommand(() => _ = _engine.RequestTabsAsync());
            RefreshDevicesCommand = new RelayCommand(RefreshDevices);
            ToggleMicMuteCommand = new RelayCommand(() => IsMicMuted = !IsMicMuted);
            ToggleBrowserMuteCommand = new RelayCommand(() => IsBrowserMuted = !IsBrowserMuted);
            ToggleMasterMuteCommand = new RelayCommand(() => IsMasterMuted = !IsMasterMuted);
            ToggleMonitoringCommand = new RelayCommand(() => IsMonitoringEnabled = !IsMonitoringEnabled);
            SetMonitorSourceCommand = new RelayCommand(param =>
            {
                if (param != null && int.TryParse(param.ToString(), out int mode))
                {
                    MonitorSourceMode = mode;
                }
            });
            ToggleSampleRateModeCommand = new RelayCommand(() =>
            {
                _engine.BrowserReceiver.SampleRateMode = (_engine.BrowserReceiver.SampleRateMode + 1) % 3;
                OnPropertyChanged(nameof(BrowserSampleRateText));
                StatusText = $"Mode: {_engine.BrowserReceiver.SampleRateStatusText}";
            });
            SetMicDelayPresetCommand = new RelayCommand(param =>
            {
                if (param != null && int.TryParse(param.ToString(), out int ms))
                {
                    MicDelayMs = ms;
                }
            });
            SetVoiceEffectCommand = new RelayCommand(param =>
            {
                if (param != null && int.TryParse(param.ToString(), out int mode))
                {
                    VoiceEffectMode = mode;
                }
            });
            ToggleVoiceFxExpandedCommand = new RelayCommand(() => IsVoiceFxExpanded = !IsVoiceFxExpanded);
            ToggleAutoTuneCommand = new RelayCommand(() => IsAutoTuneEnabled = !IsAutoTuneEnabled);
            ToggleAutoTuneExpandedCommand = new RelayCommand(() => IsAutoTuneExpanded = !IsAutoTuneExpanded);
            PitchShiftDownCommand = new RelayCommand(() => BrowserKeySemitones--);
            PitchShiftUpCommand = new RelayCommand(() => BrowserKeySemitones++);
            ResetPitchCommand = new RelayCommand(() => BrowserKeySemitones = 0);
            StopTabCaptureCommand = new RelayCommand(() =>
            {
                _ = _engine.BridgeServer.StopCaptureAsync();
                IsTabCapturing = false;
                CapturedTabTitle = "No Active Tab";
            });
            OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
            OpenGuideCommand = new RelayCommand(() => RequestOpenGuide?.Invoke());
            OpenExtensionFolderCommand = new RelayCommand(OpenExtensionFolder);
            OpenBraveExtensionsCommand = new RelayCommand(OpenBraveExtensions);
            OpenUpdateDialogCommand = new RelayCommand(OpenUpdateDialog);

            // Apply saved settings
            _micVolume = _settings.MicVolume;
            _isMicMuted = _settings.IsMicMuted;
            _micDelayMs = _settings.MicDelayMs;
            _voiceEffectMode = Math.Clamp(_settings.VoiceEffectMode, 0, 5);
            _voiceEffectIntensity = float.IsFinite(_settings.VoiceEffectIntensity)
                ? Math.Clamp(_settings.VoiceEffectIntensity, 0.0f, 1.0f)
                : 0.75f;
            _isVoiceFxExpanded = _settings.IsVoiceFxExpanded;
            _isAutoTuneEnabled = _settings.AutoTuneEnabled;
            _isAutoTuneExpanded = _settings.IsAutoTuneExpanded;
            _autoTuneKeyIndex = Math.Clamp(_settings.AutoTuneKey, 0, 11);
            _autoTuneScaleIndex = Math.Clamp(_settings.AutoTuneScale, 0, 2);
            _autoTuneSpeed = float.IsFinite(_settings.AutoTuneSpeed)
                ? Math.Clamp(_settings.AutoTuneSpeed, 0.0f, 1.0f)
                : 0.20f;
            _autoTuneMix = float.IsFinite(_settings.AutoTuneMix)
                ? Math.Clamp(_settings.AutoTuneMix, 0.0f, 1.0f)
                : 1.0f;
            _browserVolume = _settings.BrowserVolume;
            _isBrowserMuted = _settings.IsBrowserMuted;
            _browserKeySemitones = _settings.BrowserKeySemitones;
            _masterVolume = _settings.MasterVolume;
            _isMasterMuted = _settings.IsMasterMuted;
            _isMonitoringEnabled = false;
            _monitorVolume = _settings.MonitorVolume;
            _monitorSourceMode = Math.Clamp(_settings.MonitorSourceMode, 0, 2);

            _engine.MixerBus.MicrophoneVolume = _micVolume;
            _engine.MixerBus.IsMicrophoneMuted = _isMicMuted;
            _engine.MicDelayMs = _micDelayMs;
            _engine.VoiceEffectMode = _voiceEffectMode;
            _engine.VoiceEffectIntensity = _voiceEffectIntensity;
            _engine.AutoTuneEnabled = _isAutoTuneEnabled;
            _engine.AutoTuneKey = _autoTuneKeyIndex;
            _engine.AutoTuneScale = _autoTuneScaleIndex;
            _engine.AutoTuneSpeed = _autoTuneSpeed;
            _engine.AutoTuneMix = _autoTuneMix;
            _engine.MixerBus.BrowserVolume = _browserVolume;
            _engine.MixerBus.IsBrowserMuted = _isBrowserMuted;
            _engine.BrowserPitchSemitones = _browserKeySemitones;
            _engine.MixerBus.MasterVolume = _masterVolume;
            _engine.MixerBus.IsMasterMuted = _isMasterMuted;
            _engine.MonitorSourceMode = _monitorSourceMode;
            _engine.OutputRouter.MonitorProvider.Volume = _monitorVolume;

            string? startupBridgeError = null;

            // Wire events
            _engine.StatusMessage += (s, msg) => RunOnUiThread(() => StatusText = msg);
            _engine.BridgeServer.ClientConnectionChanged += OnBraveConnectionChanged;
            _engine.BridgeServer.TabListReceived += OnTabListReceived;
            _engine.BridgeServer.CaptureStatusChanged += OnCaptureStatusChanged;
            _engine.BridgeServer.TabClosed += OnTabClosed;
            _engine.BridgeServer.ExtensionError += (s, err) =>
            {
                bool isStartupBindError = err.StartsWith("Failed to start browser bridge", StringComparison.OrdinalIgnoreCase);
                string message = isStartupBindError
                    ? $"Extension: Port {_settings.BridgePort} in use — close MixerOnline or change port in Settings"
                    : $"Extension: {err}";

                if (isStartupBindError)
                {
                    startupBridgeError = message;
                }

                RunOnUiThread(() => StatusText = message);
            };
            _engine.DeviceManager.DevicesUpdated += (s, e) => RunOnUiThread(RefreshDevices);

            RefreshDevices();
            _engine.Start();

            // Preserve bridge bind failure message if InitializeAudioRouting() emitted initial device messages during Start()
            if (!string.IsNullOrEmpty(startupBridgeError))
            {
                StatusText = startupBridgeError;
            }
            else if (!IsVirtualCableDetected)
            {
                StatusText = "VB-Cable not detected — open Setup Guide to configure virtual mic output";
            }

            // 30 FPS smooth UI meter ballistics
            _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _meterTimer.Tick += OnMeterTick;
            _meterTimer.Start();

            if (_settings.AutoCheckForUpdates)
            {
                _ = Task.Run(CheckForUpdatesSilentlyAsync);
            }
        }

        private void OnMeterTick(object? sender, EventArgs e)
        {
            MicPeak = _engine.MicrophoneCapture.PeakLevel;
            MicRms = _engine.MicrophoneCapture.RmsLevel;

            BrowserPeak = _engine.BrowserReceiver.PeakLevel;
            BrowserRms = _engine.BrowserReceiver.RmsLevel;

            MasterPeakL = _engine.MixerBus.MasterPeakLeft;
            MasterPeakR = _engine.MixerBus.MasterPeakRight;
            MasterRms = _engine.MixerBus.MasterRms;

            if (!_engine.AutoTuneEnabled)
            {
                AutoTuneNoteDisplay = "OFF";
                AutoTuneNoteColor = "#52525b";
            }
            else if (_engine.IsPitchDetected)
            {
                AutoTuneNoteDisplay = _engine.DetectedNoteName;
                AutoTuneNoteColor = "#38bdf8";
            }
            else
            {
                AutoTuneNoteDisplay = "--";
                AutoTuneNoteColor = "#71717a";
            }
        }

        private static bool IsHighLatencyVirtualEnhancer(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains("FxSound", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshDevices()
        {
            var capture = _engine.DeviceManager.GetCaptureDevices();
            var render = _engine.DeviceManager.GetRenderDevices();

            Microphones.Clear();
            foreach (var dev in capture) Microphones.Add(dev);

            RenderDevices.Clear();
            foreach (var dev in render) RenderDevices.Add(dev);

            IsVirtualCableDetected = _engine.DeviceManager.IsVirtualCableInstalled();
            OnPropertyChanged(nameof(VirtualCableBadgeText));

            if (!IsVirtualCableDetected && !_vbWarningShown)
            {
                _vbWarningShown = true;
                StatusText = "VB-Cable not detected — open Setup Guide to configure virtual mic output";
            }

            if (SelectedMic == null)
            {
                SelectedMic = Microphones.FirstOrDefault(m => m.Id == _settings.SelectedMicId)
                           ?? Microphones.FirstOrDefault(m => !m.IsVirtualCable && m.IsDefault)
                           ?? Microphones.FirstOrDefault(m => !m.IsVirtualCable)
                           ?? Microphones.FirstOrDefault();
            }

            if (SelectedOutput == null)
            {
                SelectedOutput = RenderDevices.FirstOrDefault(o => o.Id == _settings.SelectedOutputId)
                              ?? _engine.DeviceManager.FindVirtualCablePlaybackDevice()
                              ?? RenderDevices.FirstOrDefault(o => o.IsDefault)
                              ?? RenderDevices.FirstOrDefault();
            }

            if (SelectedMonitor == null)
            {
                // Prefer direct hardware headphones/speakers over high-latency DSP virtual enhancers like FxSound
                var savedMonitor = RenderDevices.FirstOrDefault(m => m.Id == _settings.SelectedMonitorId);
                if (savedMonitor != null && !IsHighLatencyVirtualEnhancer(savedMonitor.Name))
                {
                    SelectedMonitor = savedMonitor;
                }
                else
                {
                    SelectedMonitor = RenderDevices.FirstOrDefault(m => !m.IsVirtualCable && !IsHighLatencyVirtualEnhancer(m.Name) && m.Name.Contains("Headphone", StringComparison.OrdinalIgnoreCase))
                                   ?? RenderDevices.FirstOrDefault(m => !m.IsVirtualCable && !IsHighLatencyVirtualEnhancer(m.Name))
                                   ?? RenderDevices.FirstOrDefault(m => !m.IsVirtualCable)
                                   ?? RenderDevices.FirstOrDefault();
                }
            }
        }

        private void OnBraveConnectionChanged(object? sender, bool connected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsBraveConnected = connected;
                BraveConnectionText = connected ? "Browser Connected" : "Extension Offline";
                if (!connected)
                {
                    IsTabCapturing = false;
                    CapturedTabTitle = "No Active Tab";
                }
            });
        }

        private void OnTabListReceived(object? sender, TabListMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _isUpdatingTabList = true;
                try
                {
                    int targetId = _activeCapturedTabId > 0 ? _activeCapturedTabId : (_selectedTab?.Id ?? 0);
                    var newTabs = msg?.Tabs ?? new System.Collections.Generic.List<TabItem>();

                    // Synchronize tabs in-place without clearing to prevent ComboBox selection collapse
                    for (int i = BraveTabs.Count - 1; i >= 0; i--)
                    {
                        var cur = BraveTabs[i];
                        if (!newTabs.Any(t => t.Id == cur.Id) && cur.Id != _activeCapturedTabId)
                        {
                            BraveTabs.RemoveAt(i);
                        }
                    }

                    foreach (var tab in newTabs)
                    {
                        var existing = BraveTabs.FirstOrDefault(t => t.Id == tab.Id);
                        if (existing != null)
                        {
                            existing.Title = tab.Title;
                            existing.Audible = tab.Audible;
                            existing.Url = tab.Url;
                        }
                        else
                        {
                            BraveTabs.Add(tab);
                        }
                    }

                    if (targetId > 0)
                    {
                        _selectedTab = BraveTabs.FirstOrDefault(t => t.Id == targetId);
                        if (_selectedTab != null && _isTabCapturing && (string.IsNullOrEmpty(CapturedTabTitle) || CapturedTabTitle == "No Active Tab"))
                        {
                            CapturedTabTitle = _selectedTab.Title;
                        }
                        OnPropertyChanged(nameof(SelectedTab));
                    }
                    else if (BraveTabs.Count > 0 && _selectedTab == null)
                    {
                        var audibleTab = BraveTabs.FirstOrDefault(t => t.Audible) ?? BraveTabs.FirstOrDefault();
                        if (audibleTab != null)
                        {
                            _selectedTab = audibleTab;
                            OnPropertyChanged(nameof(SelectedTab));
                        }
                    }
                }
                finally
                {
                    _isUpdatingTabList = false;
                }
            });
        }

        private void OnCaptureStatusChanged(object? sender, CaptureStatusMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsTabCapturing = msg.Active;
                if (msg.Active && msg.TabId > 0)
                {
                    _activeCapturedTabId = msg.TabId;
                    _isUpdatingTabList = true;
                    try
                    {
                        var match = BraveTabs.FirstOrDefault(t => t.Id == msg.TabId);
                        if (match == null)
                        {
                            match = new TabItem
                            {
                                Id = msg.TabId,
                                Title = string.IsNullOrWhiteSpace(msg.Title) ? $"Tab #{msg.TabId}" : msg.Title,
                                Audible = true
                            };
                            BraveTabs.Add(match);
                        }
                        else if (!string.IsNullOrWhiteSpace(msg.Title))
                        {
                            match.Title = msg.Title;
                        }
                        _selectedTab = match;
                        OnPropertyChanged(nameof(SelectedTab));
                    }
                    finally
                    {
                        _isUpdatingTabList = false;
                    }
                }
                else if (!msg.Active)
                {
                    _activeCapturedTabId = 0;
                }

                CapturedTabTitle = !string.IsNullOrWhiteSpace(msg.Title)
                    ? msg.Title
                    : (_selectedTab != null ? _selectedTab.Title : (_activeCapturedTabId > 0 ? $"Tab #{_activeCapturedTabId}" : "No Active Tab"));

                OnPropertyChanged(nameof(BrowserSampleRateText));
                StatusText = msg.Active ? $"Streaming: {CapturedTabTitle}" : "Stream Stopped";
            });
        }

        private void OnTabClosed(object? sender, TabNotificationMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsTabCapturing = false;
                CapturedTabTitle = "No Active Tab";
                StatusText = "Captured tab closed";
                _ = _engine.RequestTabsAsync();
            });
        }

        private void OpenExtensionFolder()
        {
            if (!BrowserLauncher.OpenExtensionFolder())
            {
                StatusText = "Extension folder not found next to application";
            }
        }

        private void OpenBraveExtensions()
        {
            var (launched, browserName, extensionsUrl) = BrowserLauncher.OpenExtensionsPage();
            StatusText = launched
                ? $"Opened {extensionsUrl} in {browserName}"
                : $"Open {BrowserLauncher.GetExtensionsUrlSummary()} and click Load unpacked";
        }

        private void OpenUpdateDialog()
        {
            if (_availableRelease != null)
            {
                RequestOpenUpdate?.Invoke();
            }
            else
            {
                RequestOpenSettings?.Invoke();
            }
        }

        public async Task CheckForUpdatesSilentlyAsync()
        {
            try
            {
                var result = await _updateService.CheckForUpdatesAsync(AppVersion, _settings.CheckForBetaUpdates);
                if (result.IsUpdateAvailable && result.LatestRelease != null)
                {
                    RunOnUiThread(() =>
                    {
                        AvailableRelease = result.LatestRelease;
                        UpdateAvailableButtonText = $"✨ {result.LatestRelease.TagName} Available";
                        IsUpdateAvailable = true;
                        StatusText = $"New update available: {result.LatestRelease.TagName} — click badge to install";
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] Silent error: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            SettingsManager.Save(_settings);
        }

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        public void Dispose()
        {
            _meterTimer.Stop();
            _engine.Dispose();
        }
    }
}
