using System;

namespace MixerOnline.Core.Audio
{
    /// <summary>
    /// Real-time dual-grain synchronous overlap-add pitch shifter for stereo 48kHz audio.
    /// Transposes musical key up or down in semitones (-6 to +6) without changing playback speed.
    /// Features zero-overhead bypass when semitones == 0, precomputed window lookup table,
    /// and pop-free crossfading.
    /// </summary>
    public sealed class PitchShifter
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        // Buffer size: 16384 stereo frames (~341ms at 48kHz)
        private const int BufferFrames = 16384;
        private const int BufferMask = BufferFrames - 1;

        // Window size: ~50ms (2400 frames) - optimal for preserving rhythm and musical transients
        private const int GrainSizeFrames = 2400;
        private const int NominalDelayFrames = 2400;

        private const int WindowTableSize = 4096;
        private const int WindowTableMask = WindowTableSize - 1;
        private static readonly float[] WindowTable = new float[WindowTableSize];

        static PitchShifter()
        {
            // Precompute Hann window: 0.5 * (1 - cos(2*pi * i / N))
            for (int i = 0; i < WindowTableSize; i++)
            {
                WindowTable[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / WindowTableSize)));
            }
        }

        private readonly float[] _buffer = new float[BufferFrames * Channels];
        private int _writeFrameIndex;

        private volatile int _semitones;
        private float _targetPitchRatio = 1.0f;
        private float _currentPitchRatio = 1.0f;

        private float _phase1;

        public int Semitones
        {
            get => _semitones;
            set => SetSemitones(value);
        }

        public PitchShifter(int initialSemitones = 0)
        {
            SetSemitones(initialSemitones);
            _currentPitchRatio = _targetPitchRatio;
        }

        public void SetSemitones(int semitones)
        {
            _semitones = Math.Clamp(semitones, -6, 6);
            _targetPitchRatio = (float)Math.Pow(2.0, _semitones / 12.0);
        }

        public void Process(Span<float> stereoSpan)
        {
            int sampleCount = stereoSpan.Length;
            if (sampleCount <= 0) return;

            int frames = sampleCount / Channels;

            // Fast path: bypassed when semitones is 0 and transition has settled to 1.0
            if (_semitones == 0 && Math.Abs(_currentPitchRatio - 1.0f) < 0.001f)
            {
                // Maintain buffer continuity so when pitch shift engages there are no glitches
                for (int i = 0; i < frames; i++)
                {
                    int wIdx = _writeFrameIndex * Channels;
                    _buffer[wIdx] = stereoSpan[i * Channels];
                    _buffer[wIdx + 1] = stereoSpan[i * Channels + 1];
                    _writeFrameIndex = (_writeFrameIndex + 1) & BufferMask;
                }
                _currentPitchRatio = 1.0f;
                return;
            }

            for (int i = 0; i < frames; i++)
            {
                // Smoothly slew pitch ratio to avoid clicks during adjustment
                float ratioDiff = _targetPitchRatio - _currentPitchRatio;
                if (Math.Abs(ratioDiff) > 0.0005f)
                {
                    _currentPitchRatio += Math.Sign(ratioDiff) * Math.Min(Math.Abs(ratioDiff), 0.002f);
                }
                else
                {
                    _currentPitchRatio = _targetPitchRatio;
                }

                // Write current input into circular buffer
                int inIdx = i * Channels;
                float inL = stereoSpan[inIdx];
                float inR = stereoSpan[inIdx + 1];

                int wIdx = _writeFrameIndex * Channels;
                _buffer[wIdx] = inL;
                _buffer[wIdx + 1] = inR;

                // Calculate phase increment: (1 - R) / GrainSize
                float phaseInc = (1.0f - _currentPitchRatio) / GrainSizeFrames;
                _phase1 += phaseInc;

                // Wrap phase 1 into [0, 1)
                if (_phase1 >= 1.0f) _phase1 -= 1.0f;
                else if (_phase1 < 0.0f) _phase1 += 1.0f;

                // Phase 2 is offset by 0.5 (180 degrees)
                float phase2 = _phase1 + 0.5f;
                if (phase2 >= 1.0f) phase2 -= 1.0f;

                // Look up window weights
                int wIdx1 = (int)(_phase1 * WindowTableSize) & WindowTableMask;
                int wIdx2 = (int)(phase2 * WindowTableSize) & WindowTableMask;
                float w1 = WindowTable[wIdx1];
                float w2 = WindowTable[wIdx2];

                // Calculate tap delays
                float delay1 = NominalDelayFrames + (_phase1 * GrainSizeFrames);
                float delay2 = NominalDelayFrames + (phase2 * GrainSizeFrames);

                // Read Tap 1 with linear interpolation
                float readPos1 = (_writeFrameIndex - delay1);
                if (readPos1 < 0) readPos1 += BufferFrames;
                int f1_0 = (int)readPos1 & BufferMask;
                int f1_1 = (f1_0 + 1) & BufferMask;
                float frac1 = readPos1 - (int)readPos1;

                int s1_0 = f1_0 * Channels;
                int s1_1 = f1_1 * Channels;
                float tap1L = _buffer[s1_0] + frac1 * (_buffer[s1_1] - _buffer[s1_0]);
                float tap1R = _buffer[s1_0 + 1] + frac1 * (_buffer[s1_1 + 1] - _buffer[s1_0 + 1]);

                // Read Tap 2 with linear interpolation
                float readPos2 = (_writeFrameIndex - delay2);
                if (readPos2 < 0) readPos2 += BufferFrames;
                int f2_0 = (int)readPos2 & BufferMask;
                int f2_1 = (f2_0 + 1) & BufferMask;
                float frac2 = readPos2 - (int)readPos2;

                int s2_0 = f2_0 * Channels;
                int s2_1 = f2_1 * Channels;
                float tap2L = _buffer[s2_0] + frac2 * (_buffer[s2_1] - _buffer[s2_0]);
                float tap2R = _buffer[s2_0 + 1] + frac2 * (_buffer[s2_1 + 1] - _buffer[s2_0 + 1]);

                // Overlap-add
                stereoSpan[inIdx] = (tap1L * w1) + (tap2L * w2);
                stereoSpan[inIdx + 1] = (tap1R * w1) + (tap2R * w2);

                _writeFrameIndex = (_writeFrameIndex + 1) & BufferMask;
            }
        }

        public void Reset()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _writeFrameIndex = 0;
            _phase1 = 0f;
            _currentPitchRatio = _targetPitchRatio;
        }
    }
}
