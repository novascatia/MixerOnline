using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerOnline.Core.Audio
{
    public sealed class AudioOutputRouter : IDisposable
    {
        private readonly AudioMixerBus _mixerBus;
        private readonly MonitorSampleProvider _monitorProvider;

        private WasapiOut? _primaryOut;
        private MMDevice? _primaryDevice;
        private string? _primaryDeviceId;

        private WasapiOut? _monitorOut;
        private MMDevice? _monitorDevice;
        private string? _monitorDeviceId;
        private bool _isMonitoringEnabled;

        private int _latencyMs = 20;
        private readonly object _lock = new();
        private bool _disposed;

        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<bool>? PrimaryStateChanged;
        public event EventHandler<bool>? MonitorStateChanged;

        public bool IsPrimaryActive => _primaryOut != null && _primaryOut.PlaybackState == PlaybackState.Playing;
        public bool IsMonitorActive => _monitorOut != null && _monitorOut.PlaybackState == PlaybackState.Playing;
        public bool IsMonitoringEnabled => _isMonitoringEnabled;

        public string? PrimaryDeviceId => _primaryDeviceId;
        public string? MonitorDeviceId => _monitorDeviceId;

        public int LatencyMs
        {
            get => _latencyMs;
            set => SetLatency(value);
        }

        public void SetLatency(int latencyMs)
        {
            lock (_lock)
            {
                _latencyMs = Math.Clamp(latencyMs, 10, 200);
                if (_primaryDevice != null && IsPrimaryActive)
                {
                    SetPrimaryDevice(_primaryDevice);
                }
                if (_monitorDevice != null && IsMonitorActive)
                {
                    SetMonitoring(true, _monitorDevice);
                }
            }
        }

        public MonitorSampleProvider MonitorProvider => _monitorProvider;

        public AudioOutputRouter(AudioMixerBus mixerBus)
        {
            _mixerBus = mixerBus ?? throw new ArgumentNullException(nameof(mixerBus));
            _monitorProvider = new MonitorSampleProvider(mixerBus.MonitorBuffer);
        }

        public void SetPrimaryDevice(MMDevice device)
        {
            lock (_lock)
            {
                StopPrimary();

                try
                {
                    _primaryDevice = device;
                    _primaryDeviceId = device.ID;

                    // Event-driven shared WASAPI mode for exact low latency
                    _primaryOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, _latencyMs);
                    _primaryOut.PlaybackStopped += OnPrimaryPlaybackStopped;
                    _primaryOut.Init(_mixerBus);
                    _primaryOut.Play();

                    PrimaryStateChanged?.Invoke(this, true);
                }
                catch (Exception ex)
                {
                    StopPrimary();
                    ErrorOccurred?.Invoke(this, $"Primary output error: {ex.Message}");
                }
            }
        }

        public void StopPrimary()
        {
            lock (_lock)
            {
                if (_primaryOut != null)
                {
                    try
                    {
                        _primaryOut.PlaybackStopped -= OnPrimaryPlaybackStopped;
                        _primaryOut.Stop();
                        _primaryOut.Dispose();
                    }
                    catch { }
                    _primaryOut = null;
                }
                PrimaryStateChanged?.Invoke(this, false);
            }
        }

        private void OnPrimaryPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lock)
            {
                if (e.Exception != null)
                {
                    ErrorOccurred?.Invoke(this, $"Primary output stopped: {e.Exception.Message}");
                }
                PrimaryStateChanged?.Invoke(this, false);
            }
        }

        public void SetMonitoring(bool enabled, MMDevice? monitorDevice = null)
        {
            lock (_lock)
            {
                _isMonitoringEnabled = enabled;

                if (!enabled)
                {
                    StopMonitor();
                    return;
                }

                if (monitorDevice != null)
                {
                    _monitorDevice = monitorDevice;
                    _monitorDeviceId = monitorDevice.ID;
                }

                if (_monitorDevice == null) return;

                StopMonitor();

                try
                {
                    _mixerBus.MonitorBuffer.Clear();
                    _monitorOut = new WasapiOut(_monitorDevice, AudioClientShareMode.Shared, useEventSync: true, _latencyMs);
                    _monitorOut.PlaybackStopped += OnMonitorPlaybackStopped;
                    _monitorOut.Init(_monitorProvider);
                    _monitorOut.Play();

                    MonitorStateChanged?.Invoke(this, true);
                }
                catch (Exception ex)
                {
                    StopMonitor();
                    ErrorOccurred?.Invoke(this, $"Monitor output error: {ex.Message}");
                }
            }
        }

        public void StopMonitor()
        {
            lock (_lock)
            {
                if (_monitorOut != null)
                {
                    try
                    {
                        _monitorOut.PlaybackStopped -= OnMonitorPlaybackStopped;
                        _monitorOut.Stop();
                        _monitorOut.Dispose();
                    }
                    catch { }
                    _monitorOut = null;
                }
                MonitorStateChanged?.Invoke(this, false);
            }
        }

        private void OnMonitorPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lock)
            {
                if (e.Exception != null)
                {
                    ErrorOccurred?.Invoke(this, $"Monitor output stopped: {e.Exception.Message}");
                }
                MonitorStateChanged?.Invoke(this, false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopPrimary();
            StopMonitor();
        }
    }
}
