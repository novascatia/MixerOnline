using System;
using NAudio.Wave;

namespace MixerOnline.Core.Audio
{
    public sealed class MonitorSampleProvider : ISampleProvider, IWaveProvider
    {
        private readonly AdaptiveJitterBuffer _monitorBuffer;
        private readonly WaveFormat _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        private volatile float _volume = 1.0f;
        private volatile bool _isMuted;
        private volatile bool _trimOnNextRead;
        private float[] _floatConversionBuffer = new float[2048];

        public WaveFormat WaveFormat => _waveFormat;

        public float Volume
        {
            get => _volume;
            set => _volume = float.IsFinite(value) ? Math.Clamp(value, 0f, 2.0f) : 1.0f;
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

        public void RequestLowLatencySync() => _trimOnNextRead = true;

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            if (_trimOnNextRead)
            {
                _trimOnNextRead = false;
                _monitorBuffer.TrimToMaxBuffered(Math.Max(240, count / 2));
            }
            else if (_monitorBuffer.BufferedFrames > 1920)
            {
                // Prevent clock drift between VB-Cable and headphone DAC from accumulating stale delay
                _monitorBuffer.TrimToMaxBuffered(480);
            }

            Span<float> span = buffer.AsSpan(offset, count);
            _monitorBuffer.Read(span);

            float volume = _volume;
            if (_isMuted || volume <= 0.0001f)
            {
                span.Clear();
                return count;
            }

            if (Math.Abs(volume - 1.0f) > 0.001f)
            {
                for (int i = 0; i < count; i++)
                {
                    float sample = span[i] * volume;
                    span[i] = float.IsFinite(sample) ? sample : 0f;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (!float.IsFinite(span[i])) span[i] = 0f;
                }
            }

            return count;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (floatCount <= 0) return 0;

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
