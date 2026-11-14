using System;

namespace MixerOnline.Core.Audio
{
    /// <summary>
    /// Musical scale types for pitch quantization.
    /// </summary>
    public enum AutoTuneScaleType
    {
        Chromatic = 0,
        Major = 1,
        Minor = 2
    }

    /// <summary>
    /// High-performance, zero-allocation real-time Auto-Tune / vocal pitch correction processor
    /// for 48kHz stereo interleaved audio.
    /// Features:
    /// - High-speed two-tier pitch detection (12kHz decimation + 48kHz parabolic refinement)
    /// - Musical scale quantizer (Chromatic, Major, Minor across all 12 root keys)
    /// - Variable retune speed: 0.0 (Hard Snap / T-Pain / Cher effect) to 1.0 (Natural subtle correction)
    /// - Low-latency dual-grain synchronous overlap-add (SOLA) vocal resampler
    /// - Click-free bypass crossfading and wet/dry mix control
    /// </summary>
    public sealed class AutoTuneProcessor
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        // Decimated pitch detection constants (48kHz -> 12kHz, decimation factor = 4)
        private const int DecimationFactor = 4;
        private const int DecimatedRate = SampleRate / DecimationFactor; // 12000 Hz

        // Vocal fundamental search range: 70 Hz to 1050 Hz
        // At 12kHz: tau in [11 .. 171] samples
        private const int MinTau12k = 11;
        private const int MaxTau12k = 171;
        private const int CoarseWindowSize12k = 256;

        // Pitch update hop size: every 128 frames at 48kHz (~2.67ms = 375 Hz update rate)
        private const int HopFrames = 128;

        // Analysis ring buffer size (at 48kHz)
        private const int AnalysisBufferFrames = 2048;
        private const int AnalysisBufferMask = AnalysisBufferFrames - 1;

        // Resynthesis circular buffer (8192 frames = ~170ms)
        private const int BufferFrames = 8192;
        private const int BufferMask = BufferFrames - 1;

        // Grain size for vocal pitch shifting (~16ms = 768 frames at 48kHz)
        private const int GrainSizeFrames = 768;
        private const int NominalDelayFrames = 768;

        // Window table size
        private const int WindowTableSize = 2048;
        private const int WindowTableMask = WindowTableSize - 1;
        private static readonly float[] WindowTable = new float[WindowTableSize];

        // Musical scale intervals
        private static readonly bool[][] ScaleIntervals =
        {
            // Chromatic (all 12 semitones active)
            new[] { true, true, true, true, true, true, true, true, true, true, true, true },
            // Major: 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1 (W-W-H-W-W-W-H)
            new[] { true, false, true, false, true, true, false, true, false, true, false, true },
            // Natural Minor: 1, 0, 1, 1, 0, 1, 0, 1, 1, 0, 1, 0 (W-H-W-W-H-W-W)
            new[] { true, false, true, true, false, true, false, true, true, false, true, false }
        };

        private static readonly string[] NoteNames =
        {
            "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
        };

        static AutoTuneProcessor()
        {
            // Precompute Hann window: 0.5 * (1 - cos(2*pi * i / N))
            for (int i = 0; i < WindowTableSize; i++)
            {
                WindowTable[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / WindowTableSize)));
            }
        }

        // Settings (thread-safe UI parameters)
        private volatile bool _enabled;
        private volatile int _key;        // 0=C, 1=C#, ..., 11=B
        private volatile int _scale;      // 0=Chromatic, 1=Major, 2=Minor
        private volatile float _retuneSpeed = 0.20f; // 0.0 (Hard Snap) .. 1.0 (Soft Natural)
        private volatile float _mix = 1.0f;          // 0.0 (Dry) .. 1.0 (Wet)

        // DSP Buffers
        private readonly float[] _buffer = new float[BufferFrames * Channels];
        private int _writeFrameIndex;

        private readonly float[] _analysisMono = new float[AnalysisBufferFrames];
        private int _analysisWriteIndex;

        private readonly float[] _decimatedMono = new float[AnalysisBufferFrames / DecimationFactor];
        private int _decimatedWriteIndex;
        private int _decimateAccumCount;
        private float _decimateAccumSum;

        // DC blocker filter states
        private float _dcInPrev;
        private float _dcOutPrev;

        // Pitch shift state
        private float _phase1;
        private float _currentPitchRatio = 1.0f;
        private float _targetPitchRatio = 1.0f;

        // Crossfade blend for enable/disable bypass
        private float _effectBlend;
        private const float BlendStep = 1.0f / 480.0f; // ~10ms at 48kHz

        // Hop counter for periodic pitch analysis
        private int _hopFramesCounter;

        // Metering / UI readouts
        private volatile bool _isVoiced;
        private volatile float _detectedPitchHz;
        private volatile string _detectedNoteName = "--";
        private volatile float _pitchCentsError;

        #region Public Properties

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public int Key
        {
            get => _key;
            set => _key = Math.Clamp(value, 0, 11);
        }

        public int Scale
        {
            get => _scale;
            set => _scale = Math.Clamp(value, 0, 2);
        }

        public float RetuneSpeed
        {
            get => _retuneSpeed;
            set => _retuneSpeed = Math.Clamp(value, 0f, 1.0f);
        }

        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0f, 1.0f);
        }

        public bool IsPitchDetected => _isVoiced;
        public float DetectedPitchHz => _detectedPitchHz;
        public string DetectedNoteName => _detectedNoteName;
        public float PitchCentsError => _pitchCentsError;

        #endregion

        public AutoTuneProcessor(bool enabled = false, int key = 0, int scale = 0, float retuneSpeed = 0.2f, float mix = 1.0f)
        {
            _enabled = enabled;
            Key = key;
            Scale = scale;
            RetuneSpeed = retuneSpeed;
            Mix = mix;
        }

        public void Reset()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            Array.Clear(_analysisMono, 0, _analysisMono.Length);
            Array.Clear(_decimatedMono, 0, _decimatedMono.Length);
            _writeFrameIndex = 0;
            _analysisWriteIndex = 0;
            _decimatedWriteIndex = 0;
            _decimateAccumCount = 0;
            _decimateAccumSum = 0f;
            _dcInPrev = 0f;
            _dcOutPrev = 0f;
            _phase1 = 0f;
            _currentPitchRatio = 1.0f;
            _targetPitchRatio = 1.0f;
            _effectBlend = 0f;
            _hopFramesCounter = 0;
            _isVoiced = false;
            _detectedPitchHz = 0f;
            _detectedNoteName = "--";
            _pitchCentsError = 0f;
        }

        public void Process(Span<float> stereoSpan)
        {
            int sampleCount = stereoSpan.Length;
            if (sampleCount <= 0) return;

            int frames = sampleCount / Channels;
            bool targetActive = _enabled;

            // Fast bypass path when disabled and transition is complete
            if (!targetActive && _effectBlend <= 0.0001f)
            {
                _effectBlend = 0f;
                _isVoiced = false;
                _detectedPitchHz = 0f;
                _detectedNoteName = "--";
                _pitchCentsError = 0f;

                // Maintain circular buffer history so enabling is glitch-free
                for (int i = 0; i < frames; i++)
                {
                    int wIdx = _writeFrameIndex * Channels;
                    _buffer[wIdx] = stereoSpan[i * Channels];
                    _buffer[wIdx + 1] = stereoSpan[i * Channels + 1];
                    _writeFrameIndex = (_writeFrameIndex + 1) & BufferMask;
                }
                return;
            }

            float speed = _retuneSpeed;
            // Slew alpha: 0.02f at speed 0.0 (Hard Snap / ~2ms) down to 0.0004f at speed 1.0 (Soft Natural / ~80ms)
            float slewAlpha = (float)(0.02 * Math.Pow(0.02, speed));

            float mixAmount = _mix;

            for (int i = 0; i < frames; i++)
            {
                // Smoothly update enable/disable crossfade blend
                if (targetActive && _effectBlend < 1.0f)
                {
                    _effectBlend = Math.Min(1.0f, _effectBlend + BlendStep);
                }
                else if (!targetActive && _effectBlend > 0.0f)
                {
                    _effectBlend = Math.Max(0.0f, _effectBlend - BlendStep);
                }

                int inIdx = i * Channels;
                float inL = stereoSpan[inIdx];
                float inR = stereoSpan[inIdx + 1];

                // Write stereo dry samples to circular delay buffer
                int wIdx = _writeFrameIndex * Channels;
                _buffer[wIdx] = inL;
                _buffer[wIdx + 1] = inR;

                // Mono downmix and DC blocker (fc ~ 20 Hz)
                float monoIn = 0.5f * (inL + inR);
                float monoDc = monoIn - _dcInPrev + (0.995f * _dcOutPrev);
                _dcInPrev = monoIn;
                _dcOutPrev = monoDc;

                // Write to full 48kHz analysis ring buffer
                _analysisMono[_analysisWriteIndex] = monoDc;
                _analysisWriteIndex = (_analysisWriteIndex + 1) & AnalysisBufferMask;

                // Decimate to 12kHz
                _decimateAccumSum += monoDc;
                _decimateAccumCount++;
                if (_decimateAccumCount >= DecimationFactor)
                {
                    int decMask = _decimatedMono.Length - 1;
                    _decimatedMono[_decimatedWriteIndex] = _decimateAccumSum * 0.25f;
                    _decimatedWriteIndex = (_decimatedWriteIndex + 1) & decMask;
                    _decimateAccumSum = 0f;
                    _decimateAccumCount = 0;
                }

                // Check periodic pitch analysis
                _hopFramesCounter++;
                if (_hopFramesCounter >= HopFrames)
                {
                    _hopFramesCounter = 0;
                    AnalyzeVocalPitch();
                }

                // Slew pitch ratio towards target
                float targetRatio = _targetPitchRatio;
                _currentPitchRatio += slewAlpha * (targetRatio - _currentPitchRatio);

                // Advance grain phase: phaseInc = (1 - R) / GrainSize
                float phaseInc = (1.0f - _currentPitchRatio) / GrainSizeFrames;
                _phase1 += phaseInc;
                if (_phase1 >= 1.0f) _phase1 -= 1.0f;
                else if (_phase1 < 0.0f) _phase1 += 1.0f;

                float phase2 = _phase1 + 0.5f;
                if (phase2 >= 1.0f) phase2 -= 1.0f;

                // Hann window weights
                int w1Idx = (int)(_phase1 * WindowTableSize) & WindowTableMask;
                int w2Idx = (int)(phase2 * WindowTableSize) & WindowTableMask;
                float w1 = WindowTable[w1Idx];
                float w2 = WindowTable[w2Idx];

                // Tap delay read positions
                float delay1 = NominalDelayFrames + (_phase1 * GrainSizeFrames);
                float delay2 = NominalDelayFrames + (phase2 * GrainSizeFrames);

                // Tap 1 with linear interpolation
                float readPos1 = _writeFrameIndex - delay1;
                if (readPos1 < 0) readPos1 += BufferFrames;
                int f1_0 = (int)readPos1 & BufferMask;
                int f1_1 = (f1_0 + 1) & BufferMask;
                float frac1 = readPos1 - (int)readPos1;

                int s1_0 = f1_0 * Channels;
                int s1_1 = f1_1 * Channels;
                float tap1L = _buffer[s1_0] + (frac1 * (_buffer[s1_1] - _buffer[s1_0]));
                float tap1R = _buffer[s1_0 + 1] + (frac1 * (_buffer[s1_1 + 1] - _buffer[s1_0 + 1]));

                // Tap 2 with linear interpolation
                float readPos2 = _writeFrameIndex - delay2;
                if (readPos2 < 0) readPos2 += BufferFrames;
                int f2_0 = (int)readPos2 & BufferMask;
                int f2_1 = (f2_0 + 1) & BufferMask;
                float frac2 = readPos2 - (int)readPos2;

                int s2_0 = f2_0 * Channels;
                int s2_1 = f2_1 * Channels;
                float tap2L = _buffer[s2_0] + (frac2 * (_buffer[s2_1] - _buffer[s2_0]));
                float tap2R = _buffer[s2_0 + 1] + (frac2 * (_buffer[s2_1 + 1] - _buffer[s2_0 + 1]));

                // Overlap-add wet signal
                float wetL = (tap1L * w1) + (tap2L * w2);
                float wetR = (tap1R * w1) + (tap2R * w2);

                // Blend with dry input according to Mix and EffectBlend
                float activeMix = mixAmount * _effectBlend;
                stereoSpan[inIdx] = inL + (activeMix * (wetL - inL));
                stereoSpan[inIdx + 1] = inR + (activeMix * (wetR - inR));

                _writeFrameIndex = (_writeFrameIndex + 1) & BufferMask;
            }
        }

        /// <summary>
        /// Analyzes vocal pitch using decimated autocorrelation + fine parabolic interpolation.
        /// Quantizes fundamental frequency to the selected key and musical scale.
        /// </summary>
        private void AnalyzeVocalPitch()
        {
            int decMask = _decimatedMono.Length - 1;
            int decHead = _decimatedWriteIndex;

            // 1. Compute RMS energy in recent 12kHz frames
            double energySum = 0;
            for (int j = 0; j < CoarseWindowSize12k; j++)
            {
                int idx = (decHead - 1 - j) & decMask;
                float s = _decimatedMono[idx];
                energySum += s * s;
            }

            float rms = (float)Math.Sqrt(energySum / CoarseWindowSize12k);

            // Silence / noise gate threshold: -46 dBFS (~0.005)
            if (rms < 0.005f)
            {
                _isVoiced = false;
                _detectedPitchHz = 0f;
                _detectedNoteName = "--";
                _pitchCentsError = 0f;
                _targetPitchRatio = 1.0f;
                return;
            }

            // 2. Coarse Autocorrelation in 12kHz domain (tau in [11 .. 171])
            int bestTau12k = 0;
            float maxCorr12k = 0f;

            for (int tau = MinTau12k; tau <= MaxTau12k; tau++)
            {
                double crossSum = 0;
                for (int j = 0; j < CoarseWindowSize12k; j++)
                {
                    int idx0 = (decHead - 1 - j) & decMask;
                    int idxTau = (decHead - 1 - j - tau) & decMask;
                    crossSum += _decimatedMono[idx0] * _decimatedMono[idxTau];
                }

                float normalizedCorr = (float)(crossSum / (energySum + 1e-9));

                // Find first significant peak above harmonicity threshold (0.50)
                if (normalizedCorr > 0.50f && normalizedCorr > maxCorr12k)
                {
                    maxCorr12k = normalizedCorr;
                    bestTau12k = tau;
                }
                else if (maxCorr12k > 0.50f && normalizedCorr < (maxCorr12k - 0.15f))
                {
                    // Past peak, break early
                    break;
                }
            }

            if (bestTau12k < MinTau12k || maxCorr12k < 0.45f)
            {
                // Unvoiced / harmonicity too low
                _isVoiced = false;
                _detectedPitchHz = 0f;
                _detectedNoteName = "--";
                _pitchCentsError = 0f;
                _targetPitchRatio = 1.0f;
                return;
            }

            // 3. Fine Refinement at 48kHz around (bestTau12k * DecimationFactor)
            int centerTau48k = bestTau12k * DecimationFactor;
            int fineStart = Math.Max(centerTau48k - 3, 44);
            int fineEnd = Math.Min(centerTau48k + 3, 685);

            int analysisHead = _analysisWriteIndex;
            int analysisMask = AnalysisBufferMask;
            const int fineWindow = 512;

            double fineEnergy = 0;
            for (int j = 0; j < fineWindow; j++)
            {
                int idx = (analysisHead - 1 - j) & analysisMask;
                float s = _analysisMono[idx];
                fineEnergy += s * s;
            }

            int bestTau48k = centerTau48k;
            float maxCorr48k = -1f;
            float[] corrWindow = new float[fineEnd - fineStart + 3];

            for (int tau = fineStart; tau <= fineEnd; tau++)
            {
                double cross = 0;
                for (int j = 0; j < fineWindow; j++)
                {
                    int idx0 = (analysisHead - 1 - j) & analysisMask;
                    int idxTau = (analysisHead - 1 - j - tau) & analysisMask;
                    cross += _analysisMono[idx0] * _analysisMono[idxTau];
                }

                float c = (float)(cross / (fineEnergy + 1e-9));
                int arrIdx = tau - fineStart + 1;
                corrWindow[arrIdx] = c;

                if (c > maxCorr48k)
                {
                    maxCorr48k = c;
                    bestTau48k = tau;
                }
            }

            // 4. Parabolic Interpolation for sub-sample accuracy
            float refinedTau = bestTau48k;
            int p = bestTau48k - fineStart + 1;
            if (p > 0 && p < corrWindow.Length - 1)
            {
                float y1 = corrWindow[p - 1];
                float y2 = corrWindow[p];
                float y3 = corrWindow[p + 1];
                float denom = (2f * y2) - y1 - y3;
                if (Math.Abs(denom) > 1e-6f)
                {
                    float delta = (y3 - y1) / (2f * denom);
                    refinedTau = bestTau48k + Math.Clamp(delta, -0.5f, 0.5f);
                }
            }

            if (refinedTau <= 0f) return;

            float detectedFreq = SampleRate / refinedTau;
            if (detectedFreq < 65f || detectedFreq > 1100f)
            {
                _isVoiced = false;
                _targetPitchRatio = 1.0f;
                return;
            }

            // 5. Quantize to musical key and scale
            // Exact MIDI note: n = 69 + 12 * log2(f / 440)
            float exactMidi = 69.0f + (12.0f * (float)(Math.Log(detectedFreq / 440.0) / Math.Log(2.0)));
            int targetMidi = FindNearestScaleNote(exactMidi, _key, _scale);
            float targetFreq = 440.0f * (float)Math.Pow(2.0, (targetMidi - 69.0) / 12.0);

            // Compute pitch correction ratio
            float ratio = targetFreq / detectedFreq;

            // Clamp pitch ratio within +/- 2.5 semitones (0.865 to 1.155) to prevent extreme warping
            ratio = Math.Clamp(ratio, 0.865f, 1.155f);

            // UI readouts
            _isVoiced = true;
            _detectedPitchHz = detectedFreq;

            int noteIndex = ((targetMidi % 12) + 12) % 12;
            int octave = (targetMidi / 12) - 1;
            float cents = (exactMidi - targetMidi) * 100.0f;
            _pitchCentsError = cents;

            string sign = cents >= 0 ? "+" : "";
            _detectedNoteName = $"{NoteNames[noteIndex]}{octave} ({sign}{cents:F0}¢)";

            _targetPitchRatio = ratio;
        }

        /// <summary>
        /// Finds the nearest allowed MIDI note in the chosen key and scale.
        /// </summary>
        public static int FindNearestScaleNote(float exactMidiNote, int rootKey, int scaleIndex)
        {
            rootKey = Math.Clamp(rootKey, 0, 11);
            scaleIndex = Math.Clamp(scaleIndex, 0, 2);
            bool[] mask = ScaleIntervals[scaleIndex];

            int roundNote = (int)MathF.Round(exactMidiNote);
            int bestNote = roundNote;
            float minDistance = float.MaxValue;

            for (int offset = -6; offset <= 6; offset++)
            {
                int candidate = roundNote + offset;
                int semitone = ((candidate - rootKey) % 12 + 12) % 12;
                if (mask[semitone])
                {
                    float dist = MathF.Abs(candidate - exactMidiNote);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        bestNote = candidate;
                    }
                }
            }

            return bestNote;
        }
    }
}
