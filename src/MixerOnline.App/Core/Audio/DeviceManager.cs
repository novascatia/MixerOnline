using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MixerOnline.Core.Audio
{
    public sealed class DeviceManager : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private bool _disposed;

        public event EventHandler? DevicesUpdated;
        public event EventHandler<string>? DeviceAdded;
        public event EventHandler<string>? DeviceRemoved;
        public event EventHandler<string>? DefaultChanged;

        public DeviceManager()
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                string defaultId = string.Empty;
                try
                {
                    var defaultDev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    defaultId = defaultDev?.ID ?? string.Empty;
                }
                catch { }

                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var dev in endpoints)
                {
                    bool isVirtualCable = IsVirtualCableDevice(dev.FriendlyName);
                    list.Add(new AudioDeviceInfo(
                        dev.ID,
                        dev.FriendlyName,
                        isCapture: true,
                        isVirtualCable: isVirtualCable,
                        isDefault: dev.ID == defaultId));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeviceManager] Error enumerating capture devices: {ex.Message}");
            }
            return list;
        }

        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                string defaultId = string.Empty;
                try
                {
                    var defaultDev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    defaultId = defaultDev?.ID ?? string.Empty;
                }
                catch { }

                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var dev in endpoints)
                {
                    bool isVirtualCable = IsVirtualCableDevice(dev.FriendlyName);
                    list.Add(new AudioDeviceInfo(
                        dev.ID,
                        dev.FriendlyName,
                        isCapture: false,
                        isVirtualCable: isVirtualCable,
                        isDefault: dev.ID == defaultId));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeviceManager] Error enumerating render devices: {ex.Message}");
            }
            return list;
        }

        public MMDevice? GetDeviceById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                return _enumerator.GetDevice(id);
            }
            catch
            {
                return null;
            }
        }

        public AudioDeviceInfo? FindVirtualCablePlaybackDevice()
        {
            var devices = GetRenderDevices();
            // Look for CABLE Input (VB-Audio Virtual Cable)
            return devices.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.IsVirtualCable);
        }

        public bool IsVirtualCableInstalled()
        {
            return FindVirtualCablePlaybackDevice() != null;
        }

        public static bool IsVirtualCableDevice(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;
            return deviceName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                   deviceName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                   deviceName.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase);
        }

        #region IMMNotificationClient Implementation

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            DeviceAdded?.Invoke(this, pwstrDeviceId);
            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void OnDeviceRemoved(string deviceId)
        {
            DeviceRemoved?.Invoke(this, deviceId);
            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            DefaultChanged?.Invoke(this, defaultDeviceId);
            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property changed (e.g. volume or name)
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
                _enumerator.Dispose();
            }
            catch { }
        }
    }
}
