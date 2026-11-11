using System;
using NAudio.Wave;

namespace MixerOnline.Core.Audio
{
    public sealed class MonitorSampleProvider : ISampleProvider, IWaveProvider
    {
        private readonly AdaptiveJitterBuffer _monitorBuffer;
        private readonly WaveFormat _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        private float _volume = 1.0f;
        private bool _isMuted;
        private float[] _floatConversionBuffer = new float[2048];

        public WaveFormat WaveFormat => _waveFormat;

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 2.0f);
        }

        public bool IsMuted
        {
            get => _isMuted;
            set => _isMuted = value;
        }

        public MonitorSampleProvider(AdaptiveJitterBuffer monitorBuffer)
        {
            _monitorBuffer = monitorBuffer ?? throw new ArgumentNullException(nameof(monitorBuffer));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            Span<float> span = buffer.AsSpan(offset, count);
            _monitorBuffer.Read(span);

            if (_isMuted || _volume <= 0.0001f)
            {
                span.Clear();
                return count;
            }

            if (Math.Abs(_volume - 1.0f) > 0.001f)
            {
                for (int i = 0; i < count; i++)
                {
                    buffer[offset + i] *= _volume;
                }
            }

            return count;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_floatConversionBuffer.Length < floatCount)
            {
                _floatConversionBuffer = new float[floatCount];
            }

            int readFloats = Read(_floatConversionBuffer, 0, floatCount);
            int bytesToCopy = readFloats * 4;
            System.Buffer.BlockCopy(_floatConversionBuffer, 0, buffer, offset, bytesToCopy);
            return bytesToCopy;
        }
    }
}
