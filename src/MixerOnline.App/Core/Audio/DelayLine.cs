using System;

namespace MixerOnline.Core.Audio
{
    /// <summary>
    /// Sample-accurate, pitch-slew circular buffer delay line for stereo 48kHz audio.
    /// Provides zero-overhead passthrough when delay is 0ms, and smooth pop-free
    /// adjustment from 0ms to 300ms to perfectly synchronize microphone input with
    /// browser tab audio stream latency.
    /// </summary>
    public sealed class DelayLine
    {
        private const int BufferFrames = 32768; // 2^15 = 32,768 stereo frames (~682.6ms at 48kHz)
        private const int FrameMask = BufferFrames - 1;
        private const int SampleRate = 48000;
        private const int Channels = 2;

        private readonly float[] _buffer = new float[BufferFrames * Channels];
        private int _writeFrameIndex;

        private volatile int _targetDelayMs;
        private int _targetDelayFrames;
        private float _currentDelayFrames;

        public int DelayMs
        {
            get => _targetDelayMs;
            set => SetDelayMs(value);
        }

        public DelayLine(int initialDelayMs = 0)
        {
            SetDelayMs(initialDelayMs);
            _currentDelayFrames = _targetDelayFrames;
        }

        public void SetDelayMs(int delayMs)
        {
            _targetDelayMs = Math.Clamp(delayMs, 0, 300);
            _targetDelayFrames = (int)Math.Round((_targetDelayMs / 1000.0) * SampleRate);
        }

        public void Process(Span<float> stereoSpan)
        {
            int sampleCount = stereoSpan.Length;
            if (sampleCount <= 0) return;

            int frames = sampleCount / Channels;
            int targetFrames = _targetDelayFrames;

            // Direct fast path: no delay active and slew completed
            if (targetFrames == 0 && _currentDelayFrames <= 0.001f)
            {
                // Write incoming to delay line so history is ready when delay is engaged
                for (int i = 0; i < frames; i++)
                {
                    int writeSampleIdx = _writeFrameIndex * Channels;
                    _buffer[writeSampleIdx] = stereoSpan[i * Channels];
                    _buffer[writeSampleIdx + 1] = stereoSpan[i * Channels + 1];
                    _writeFrameIndex = (_writeFrameIndex + 1) & FrameMask;
                }
                _currentDelayFrames = 0f;
                return;
            }

            // Delayed path with smooth sample-rate slew to avoid clicks/pops when slider moves
            for (int i = 0; i < frames; i++)
            {
                // Smoothly slew current delay towards target delay (max 0.25 frame adjustment per frame)
                float diff = targetFrames - _currentDelayFrames;
                if (Math.Abs(diff) > 0.001f)
                {
                    _currentDelayFrames += Math.Sign(diff) * Math.Min(Math.Abs(diff), 0.25f);
                }
                else
                {
                    _currentDelayFrames = targetFrames;
                }

                int inIdx = i * Channels;
                float inL = stereoSpan[inIdx];
                float inR = stereoSpan[inIdx + 1];

                int writeSampleIdx = _writeFrameIndex * Channels;
                _buffer[writeSampleIdx] = inL;
                _buffer[writeSampleIdx + 1] = inR;

                // Calculate read index with wrapping
                int readFrame = (_writeFrameIndex - (int)_currentDelayFrames) & FrameMask;
                int readSampleIdx = readFrame * Channels;

                stereoSpan[inIdx] = _buffer[readSampleIdx];
                stereoSpan[inIdx + 1] = _buffer[readSampleIdx + 1];

                _writeFrameIndex = (_writeFrameIndex + 1) & FrameMask;
            }
        }

        public void Reset()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _writeFrameIndex = 0;
            _currentDelayFrames = _targetDelayFrames;
        }
    }
}
