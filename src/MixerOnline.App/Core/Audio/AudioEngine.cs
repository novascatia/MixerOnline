using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using MixerOnline.Core.Network;

namespace MixerOnline.Core.Audio
{
    public sealed class AudioEngine : IDisposable
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private readonly DeviceManager _deviceManager;
        private readonly MicrophoneCapture _micCapture;
        private readonly BrowserAudioReceiver _browserReceiver;
        private readonly AudioMixerBus _mixerBus;
        private readonly AudioOutputRouter _outputRouter;
        private readonly BrowserBridgeServer _bridgeServer;

        private bool _disposed;
        private bool _timerPeriodSet;
        private string? _desiredMicId;
        private string? _desiredOutputId;
        private string? _desiredMonitorId;

        public DeviceManager DeviceManager => _deviceManager;
        public MicrophoneCapture MicrophoneCapture => _micCapture;
        public BrowserAudioReceiver BrowserReceiver => _browserReceiver;
        public AudioMixerBus MixerBus => _mixerBus;
        public AudioOutputRouter OutputRouter => _outputRouter;
        public BrowserBridgeServer BridgeServer => _bridgeServer;

        public int MicDelayMs
        {
            get => _mixerBus.MicDelayMs;
            set => _mixerBus.MicDelayMs = value;
        }

        public int BrowserPitchSemitones
        {
            get => _mixerBus.BrowserPitchSemitones;
            set => _mixerBus.BrowserPitchSemitones = value;
        }

        public event EventHandler<string>? StatusMessage;

        public AudioEngine(int bridgePort = 49876, int latencyMs = 30)
        {
            try
            {
                // Request 1.0ms multimedia timer resolution from Windows kernel for jitter-free WASAPI refills
                if (TimeBeginPeriod(1) == 0)
                {
                    _timerPeriodSet = true;
                }
            }
            catch { }

            _deviceManager = new DeviceManager();
            _micCapture = new MicrophoneCapture();
            _browserReceiver = new BrowserAudioReceiver();
            _browserReceiver.ConfigureLatency(latencyMs);
            _mixerBus = new AudioMixerBus(_micCapture, _browserReceiver);
            _outputRouter = new AudioOutputRouter(_mixerBus)
            {
                LatencyMs = latencyMs
            };
            _bridgeServer = new BrowserBridgeServer(_browserReceiver, bridgePort);

            _deviceManager.DevicesUpdated += OnDevicesUpdated;
            _deviceManager.DeviceAdded += OnDeviceAdded;
            _deviceManager.DeviceRemoved += OnDeviceRemoved;

            _outputRouter.ErrorOccurred += (s, err) => StatusMessage?.Invoke(this, err);
            _micCapture.ErrorOccurred += (s, err) => StatusMessage?.Invoke(this, err);
        }

        public void Start()
        {
            _bridgeServer.Start();
            InitializeAudioRouting();
        }

        private void InitializeAudioRouting()
        {
            var renderDevices = _deviceManager.GetRenderDevices();
            var vbCableDevice = _deviceManager.FindVirtualCablePlaybackDevice();

            string? chosenOutputId = _desiredOutputId;
            if (string.IsNullOrEmpty(chosenOutputId) && vbCableDevice != null)
            {
                chosenOutputId = vbCableDevice.Id;
            }
            if (string.IsNullOrEmpty(chosenOutputId))
            {
                chosenOutputId = renderDevices.FirstOrDefault(d => d.IsDefault)?.Id
                              ?? renderDevices.FirstOrDefault()?.Id;
            }

            if (!string.IsNullOrEmpty(chosenOutputId))
            {
                SelectOutput(chosenOutputId);
            }

            var captureDevices = _deviceManager.GetCaptureDevices();
            string? chosenMicId = _desiredMicId;
            if (string.IsNullOrEmpty(chosenMicId))
            {
                chosenMicId = captureDevices.FirstOrDefault(d => !d.IsVirtualCable && d.IsDefault)?.Id
                           ?? captureDevices.FirstOrDefault(d => !d.IsVirtualCable)?.Id
                           ?? captureDevices.FirstOrDefault()?.Id;
            }

            if (!string.IsNullOrEmpty(chosenMicId))
            {
                SelectMicrophone(chosenMicId);
            }
        }

        public void SelectMicrophone(string deviceId)
        {
            _desiredMicId = deviceId;
            var mmDevice = _deviceManager.GetDeviceById(deviceId);
            if (mmDevice != null)
            {
                _micCapture.Start(mmDevice);
                StatusMessage?.Invoke(this, $"Microphone: {mmDevice.FriendlyName}");
            }
            else
            {
                _micCapture.Stop();
            }
        }

        public void SelectOutput(string deviceId)
        {
            _desiredOutputId = deviceId;
            var mmDevice = _deviceManager.GetDeviceById(deviceId);
            if (mmDevice != null)
            {
                _outputRouter.SetPrimaryDevice(mmDevice);
                StatusMessage?.Invoke(this, $"Output: {mmDevice.FriendlyName}");
            }
            else
            {
                _outputRouter.StopPrimary();
            }
        }

        public void SelectMonitor(string deviceId)
        {
            _desiredMonitorId = deviceId;
            if (_outputRouter.IsMonitoringEnabled)
            {
                var mmDevice = _deviceManager.GetDeviceById(deviceId);
                if (mmDevice != null)
                {
                    _outputRouter.SetMonitoring(true, mmDevice);
                    StatusMessage?.Invoke(this, $"Monitor: {mmDevice.FriendlyName}");
                }
            }
        }

        public void SetMonitoring(bool enabled)
        {
            MMDevice? monDev = null;
            if (enabled && !string.IsNullOrEmpty(_desiredMonitorId))
            {
                monDev = _deviceManager.GetDeviceById(_desiredMonitorId);
            }
            _outputRouter.SetMonitoring(enabled, monDev);
        }

        public void UpdateLatency(int latencyMs)
        {
            _browserReceiver.ConfigureLatency(latencyMs);
            _outputRouter.SetLatency(latencyMs);
        }

        public Task SelectBrowserTabAsync(int tabId)
        {
            return _bridgeServer.SelectTabAsync(tabId);
        }

        public Task RequestTabsAsync()
        {
            return _bridgeServer.RequestTabsAsync();
        }

        private void OnDevicesUpdated(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_desiredMicId) && !_micCapture.IsCapturing)
            {
                var dev = _deviceManager.GetDeviceById(_desiredMicId);
                if (dev != null && dev.State == DeviceState.Active)
                {
                    SelectMicrophone(_desiredMicId);
                }
            }

            if (!string.IsNullOrEmpty(_desiredOutputId) && !_outputRouter.IsPrimaryActive)
            {
                var dev = _deviceManager.GetDeviceById(_desiredOutputId);
                if (dev != null && dev.State == DeviceState.Active)
                {
                    SelectOutput(_desiredOutputId);
                }
            }
        }

        private void OnDeviceAdded(object? sender, string deviceId)
        {
            StatusMessage?.Invoke(this, "Audio device connected.");
        }

        private void OnDeviceRemoved(object? sender, string deviceId)
        {
            StatusMessage?.Invoke(this, "Audio device disconnected.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _bridgeServer.Dispose();
            _outputRouter.Dispose();
            _micCapture.Dispose();
            _browserReceiver.Dispose();
            _deviceManager.Dispose();

            if (_timerPeriodSet)
            {
                try { TimeEndPeriod(1); } catch { }
                _timerPeriodSet = false;
            }
        }
    }
}
