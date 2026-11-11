using System;

namespace MixerOnline.Core.Audio
{
    /// <summary>
    /// Studio mastering peak limiter with smooth envelope ballistics and soft-knee saturation.
    /// Completely untouched 1:1 signal below -0.5 dBFS (0.94f), smooth analog-modeled curve above.
    /// Eliminates harsh digital buzzing and clipping distortion.
    /// </summary>
    public sealed class AudioLimiter
    {
        private float _envelope;
        private const float Threshold = 0.94f;
        private const float Ceiling = 0.999f;

        // 0.8ms attack, 80ms release at 48kHz
        private static readonly float AttackCoeff = (float)Math.Exp(-1.0 / (0.0008 * 48000));
        private static readonly float ReleaseCoeff = (float)Math.Exp(-1.0 / (0.080 * 48000));

        public void ProcessStereoBlock(Span<float> samples)
        {
            int frames = samples.Length / 2;

            for (int i = 0; i < frames; i++)
            {
                float left = samples[i * 2];
                float right = samples[i * 2 + 1];

                float peak = Math.Max(Math.Abs(left), Math.Abs(right));

                if (peak > _envelope)
                {
                    _envelope = (_envelope * AttackCoeff) + (peak * (1f - AttackCoeff));
                }
                else
                {
                    _envelope = (_envelope * ReleaseCoeff) + (peak * (1f - ReleaseCoeff));
                }

                float gain = 1.0f;
                if (_envelope > Threshold)
                {
                    gain = Threshold / _envelope;
                }

                samples[i * 2] = SoftLimit(left * gain);
                samples[i * 2 + 1] = SoftLimit(right * gain);
            }
        }

        private static float SoftLimit(float x)
        {
            if (x > Threshold)
            {
                float excess = x - Threshold;
                return Threshold + (Ceiling - Threshold) * (float)Math.Tanh(excess / (Ceiling - Threshold));
            }
            if (x < -Threshold)
            {
                float excess = -x - Threshold;
                return -(Threshold + (Ceiling - Threshold) * (float)Math.Tanh(excess / (Ceiling - Threshold)));
            }
            return x;
        }

        public static float ProcessSample(float sample)
        {
            return SoftLimit(sample);
        }

        public void Reset()
        {
            _envelope = 0f;
        }
    }
}
