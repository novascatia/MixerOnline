using System;

namespace MixerOnline.Core.Audio
{
    public sealed class BrowserAudioReceiver : IDisposable
    {
        private readonly AdaptiveJitterBuffer _jitterBuffer;
        private bool _isActive;

        // Default 48,000 Hz bit-transparent mode matching Chromium AudioContext(48000)
        private int _detectedSampleRate = 48000;
        private int _sampleRateMode = 0; // 0 = Auto (48kHz Direct), 1 = Force 44.1k->48k, 2 = Force 48k Direct
        private const int TargetSampleRate = 48000;

        // Stateful cross-packet resampler history (eliminates packet-boundary clicks)
        private double _resamplePhase;
        private float _histL0, _histL1, _histL2;
        private float _histR0, _histR1, _histR2;
        private bool _hasHistory;

        public AdaptiveJitterBuffer Buffer => _jitterBuffer;
        public bool IsActive => _isActive;

        public float PeakLevel => _jitterBuffer.PeakLevel;
        public float RmsLevel => _jitterBuffer.RmsLevel;

        public int DetectedSampleRate
        {
            get => _detectedSampleRate;
            set
            {
                if (value >= 8000 && value <= 192000)
                {
                    _detectedSampleRate = value;
                }
            }
        }

        public int SampleRateMode
        {
            get => _sampleRateMode;
            set => _sampleRateMode = Math.Clamp(value, 0, 2);
        }

        public int EffectiveInputSampleRate => _sampleRateMode switch
        {
            1 => 44100,
            2 => 48000,
            _ => _detectedSampleRate
        };

        public string SampleRateStatusText
        {
            get
            {
                int eff = EffectiveInputSampleRate;
                if (eff == 48000) return "48.0 kHz • 32-Bit HD";
                return $"{eff / 1000.0:F1} kHz ➔ 48.0 kHz";
            }
        }

        public BrowserAudioReceiver()
        {
            _jitterBuffer = new AdaptiveJitterBuffer();
        }

        public void ConfigureLatency(int latencyMs)
        {
            int preroll = Math.Max(960, (int)(latencyMs * 48 * 2));
            int max = Math.Max(4800, preroll * 3);
            _jitterBuffer.ConfigureCushion(preroll, max);
        }

        public void ProcessAudioFrame(byte[] binaryData, int offset, int length)
        {
            if (length <= 0 || length % 8 != 0) return; // Must be stereo 32-bit float (8 bytes/frame)

            int sampleCount = length / 4;
            float[] incomingSamples = new float[sampleCount];
            System.Buffer.BlockCopy(binaryData, offset, incomingSamples, 0, length);

            int inRate = EffectiveInputSampleRate;

            if (inRate == TargetSampleRate)
            {
                // 1:1 Bit-transparent HD pass-through
                _jitterBuffer.Write(incomingSamples);
            }
            else
            {
                float[] resampled = ResampleStereoStateful(incomingSamples, inRate, TargetSampleRate);
                if (resampled.Length > 0)
                {
                    _jitterBuffer.Write(resampled);
                }
            }

            _isActive = true;
        }

        private float[] ResampleStereoStateful(float[] input, int inRate, int outRate)
        {
            int inFrames = input.Length / 2;
            if (inFrames == 0) return Array.Empty<float>();

            if (!_hasHistory)
            {
                _histL0 = _histL1 = _histL2 = input[0];
                _histR0 = _histR1 = _histR2 = input[1];
                _hasHistory = true;
                _resamplePhase = 0.0;
            }

            double step = (double)inRate / outRate;
            int maxOutFrames = (int)Math.Ceiling((inFrames - _resamplePhase) / step) + 2;
            if (maxOutFrames <= 0) maxOutFrames = 1;

            float[] output = new float[maxOutFrames * 2];
            int outFrameCount = 0;

            while (_resamplePhase < inFrames)
            {
                int idx = (int)Math.Floor(_resamplePhase);
                float t = (float)(_resamplePhase - idx);

                GetFrameWithHistory(input, inFrames, idx - 2, out float l0, out float r0);
                GetFrameWithHistory(input, inFrames, idx - 1, out float l1, out float r1);
                GetFrameWithHistory(input, inFrames, idx, out float l2, out float r2);
                GetFrameWithHistory(input, inFrames, idx + 1, out float l3, out float r3);

                if (outFrameCount * 2 + 1 < output.Length)
                {
                    output[outFrameCount * 2] = CubicHermite(l0, l1, l2, l3, t);
                    output[outFrameCount * 2 + 1] = CubicHermite(r0, r1, r2, r3, t);
                    outFrameCount++;
                }

                _resamplePhase += step;
            }

            _resamplePhase -= inFrames;

            // Update history with the last 3 frames of the current block
            GetFrameWithHistory(input, inFrames, inFrames - 3, out _histL0, out _histR0);
            GetFrameWithHistory(input, inFrames, inFrames - 2, out _histL1, out _histR1);
            GetFrameWithHistory(input, inFrames, inFrames - 1, out _histL2, out _histR2);

            if (outFrameCount * 2 == output.Length) return output;

            float[] trimmed = new float[outFrameCount * 2];
            Array.Copy(output, 0, trimmed, 0, outFrameCount * 2);
            return trimmed;
        }

        private void GetFrameWithHistory(float[] input, int inFrames, int frameIndex, out float l, out float r)
        {
            if (frameIndex < 0)
            {
                if (frameIndex == -1) { l = _histL2; r = _histR2; return; }
                if (frameIndex == -2) { l = _histL1; r = _histR1; return; }
                l = _histL0; r = _histR0;
                return;
            }
            if (frameIndex >= inFrames)
            {
                frameIndex = inFrames - 1;
            }
            l = input[frameIndex * 2];
            r = input[frameIndex * 2 + 1];
        }

        private static float CubicHermite(float a, float b, float c, float d, float t)
        {
            return b + 0.5f * t * (c - a + t * (2.0f * a - 5.0f * b + 4.0f * c - d + t * (3.0f * (b - c) + d - a)));
        }

        public void Reset()
        {
            _jitterBuffer.Clear();
            _resamplePhase = 0;
            _hasHistory = false;
            _isActive = false;
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
