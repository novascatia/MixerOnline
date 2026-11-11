using System;

namespace MixerOnline.Core.Audio
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; }
        public string Name { get; }
        public bool IsCapture { get; }
        public bool IsVirtualCable { get; }
        public bool IsDefault { get; }

        public AudioDeviceInfo(string id, string name, bool isCapture, bool isVirtualCable, bool isDefault = false)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            IsCapture = isCapture;
            IsVirtualCable = isVirtualCable;
            IsDefault = isDefault;
        }

        public override string ToString()
        {
            if (IsVirtualCable)
                return $"{Name} [Virtual Mic]";
            if (IsDefault)
                return $"{Name} [Default]";
            return Name;
        }
    }
}
