using System;

namespace MixerOnline.Core.Audio
{
    public enum VoiceEffectMode
    {
        Off = 0,
        RoomIsolate = 1,
        Echo = 2,
        Telephone = 3,
        Radio = 4,
        HallReverb = 5
    }

    /// <summary>
    /// Zero-latency-dry real-time stereo vocal DSP processor for 48kHz interleaved float audio.
    /// Dry paths run sample-by-sample with 0 samples lookahead delay so mic-to-browser
    /// synchronization never drifts. Mode transitions and enable/disable crossfade over ~10ms
    /// to prevent clicks/pops mid-sentence.
    /// </summary>
    public sealed class VoiceEffectProcessor
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        // ~10ms smooth crossfade at 48kHz (480 frames)
        private const float BlendStep = 1.0f / 480.0f;
        private const float IntensitySmoothCoeff = 0.004f;

        // 4th-order Butterworth cascade Q values
        private const double ButterworthQ1 = 0.541196100146197;
        private const double ButterworthQ2 = 1.3065629648763766;
        private const double Butterworth2ndQ = 0.7071067811865476;

        // Echo circular delay constants (32,768 frames = ~682.67ms at 48kHz)
        private const int EchoBufferFrames = 32768;
        private const int EchoMask = EchoBufferFrames - 1;
        private const int EchoDelayFramesL = 10560; // 220ms
        private const int EchoDelayFramesR = 14880; // 310ms

        // Echo 1-pole in-loop filters: HPF @ ~240Hz, LPF @ ~3.0kHz
        private static readonly float EchoHpAlpha = 1f - MathF.Exp(-2f * MathF.PI * 240f / SampleRate);
        private static readonly float EchoLpAlpha = 1f - MathF.Exp(-2f * MathF.PI * 3000f / SampleRate);

        // RoomIsolate dynamics ballistics
        private static readonly float IsoGateAttack = MathF.Exp(-1f / (0.0015f * SampleRate));
        private static readonly float IsoGateRelease = MathF.Exp(-1f / (0.060f * SampleRate));
        private static readonly float IsoGateGainOpen = MathF.Exp(-1f / (0.0012f * SampleRate));
        private static readonly float IsoGateGainClose = MathF.Exp(-1f / (0.055f * SampleRate));
        private static readonly float IsoCompAttack = MathF.Exp(-1f / (0.004f * SampleRate));
        private static readonly float IsoCompRelease = MathF.Exp(-1f / (0.085f * SampleRate));
        private const float IsoCompThresholdLin = 0.15848932f; // -16 dBFS
        private const float IsoCompMakeup = 1.1885f; // +1.5 dB

        // Telephone ballistics & DC blocker (~20Hz)
        private static readonly float TeleDcAlpha = MathF.Exp(-2f * MathF.PI * 20f / SampleRate);
        private static readonly float TeleLevelAttack = MathF.Exp(-1f / (0.002f * SampleRate));
        private static readonly float TeleLevelRelease = MathF.Exp(-1f / (0.060f * SampleRate));

        // Radio optical compressor ballistics
        private static readonly float RadioCompAttack = MathF.Exp(-1f / (0.008f * SampleRate));
        private static readonly float RadioCompRelease = MathF.Exp(-1f / (0.120f * SampleRate));
        private const float RadioCompThresholdDb = -18.0f;
        private const float RadioCompMakeup = 1.3335214f; // +2.5 dB

        // HallReverb pre-send HPF (~160Hz) and topology constants
        private static readonly float ReverbSendHpAlpha = 1f - MathF.Exp(-2f * MathF.PI * 160f / SampleRate);
        private const float ReverbAllPassGain = 0.5f;
        private const float ReverbHfDamping = 0.38f;
        private static readonly int[] CombLengthsL = { 1429, 1601, 1811, 2053 };
        private static readonly int[] CombLengthsR = { 1502, 1674, 1884, 2126 }; // +73 stereo spread
        private static readonly int[] AllPassLengthsL = { 241, 557 };
        private static readonly int[] AllPassLengthsR = { 271, 593 };

        // Thread-safe UI parameters
        private volatile int _mode;
        private volatile float _intensity = 0.65f;

        // Audio-thread transition state
        private int _activeMode;
        private float _effectBlend;
        private float _smoothedIntensity = 0.65f;

        // Mode 1: RoomIsolate DSP state
        private StereoBiquad _isoHpf1;
        private StereoBiquad _isoHpf2;
        private StereoBiquad _isoBoxNotch;
        private StereoBiquad _isoPresencePeak;
        private StereoBiquad _isoAirLpf;
        private float _isoGateEnv;
        private float _isoGateGain = 1.0f;
        private float _isoCompEnv;

        // Mode 2: Echo DSP state
        private readonly float[] _echoBuffer;
        private int _echoWriteIdx;
        private float _echoHpStateL;
        private float _echoHpStateR;
        private float _echoLpStateL;
        private float _echoLpStateR;

        // Mode 3: Telephone DSP state
        private StereoBiquad _teleHpf1;
        private StereoBiquad _teleHpf2;
        private StereoBiquad _teleLpf1;
        private StereoBiquad _teleLpf2;
        private StereoBiquad _telePresence;
        private float _teleDcPrevInL;
        private float _teleDcPrevOutL;
        private float _teleDcPrevInR;
        private float _teleDcPrevOutR;
        private float _teleLevelEnv;

        // Mode 4: Radio DSP state
        private StereoBiquad _radioHpf;
        private StereoBiquad _radioWarmth;
        private StereoBiquad _radioDeMud;
        private StereoBiquad _radioClarity;
        private float _radioCompEnv;

        // Mode 5: HallReverb DSP state
        private readonly CombFilter[] _reverbCombsL;
        private readonly CombFilter[] _reverbCombsR;
        private readonly AllPassFilter[] _reverbAllPassL;
        private readonly AllPassFilter[] _reverbAllPassR;
        private float _reverbSendHpL;
        private float _reverbSendHpR;

        public int Mode
        {
            get => _mode;
            set => _mode = Math.Clamp(value, 0, 5);
        }

        public float Intensity
        {
            get => _intensity;
            set => _intensity = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.65f;
        }

        public VoiceEffectProcessor()
        {
            // Configure Mode 1: RoomIsolate filters
            _isoHpf1.SetHighPass(110.0, ButterworthQ1);
            _isoHpf2.SetHighPass(110.0, ButterworthQ2);
            _isoBoxNotch.SetPeakingEq(290.0, 1.35, -5.5);
            _isoPresencePeak.SetPeakingEq(3500.0, 0.9, 3.0);
            _isoAirLpf.SetLowPass(14000.0, Butterworth2ndQ);

            // Configure Mode 2: Echo buffer
            _echoBuffer = new float[EchoBufferFrames * Channels];

            // Configure Mode 3: Telephone filters
            _teleHpf1.SetHighPass(340.0, ButterworthQ1);
            _teleHpf2.SetHighPass(340.0, ButterworthQ2);
            _teleLpf1.SetLowPass(3200.0, ButterworthQ1);
            _teleLpf2.SetLowPass(3200.0, ButterworthQ2);
            _telePresence.SetPeakingEq(1350.0, 1.8, 6.5);

            // Configure Mode 4: Radio filters
            _radioHpf.SetHighPass(75.0, Butterworth2ndQ);
            _radioWarmth.SetPeakingEq(145.0, 0.8, 3.5);
            _radioDeMud.SetPeakingEq(380.0, 1.2, -2.5);
            _radioClarity.SetPeakingEq(4500.0, 0.8, 3.5);

            // Configure Mode 5: HallReverb Schroeder network
            _reverbCombsL = new CombFilter[CombLengthsL.Length];
            _reverbCombsR = new CombFilter[CombLengthsR.Length];
            for (int i = 0; i < CombLengthsL.Length; i++)
            {
                _reverbCombsL[i] = new CombFilter(CombLengthsL[i]);
                _reverbCombsR[i] = new CombFilter(CombLengthsR[i]);
            }

            _reverbAllPassL = new AllPassFilter[AllPassLengthsL.Length];
            _reverbAllPassR = new AllPassFilter[AllPassLengthsR.Length];
            for (int i = 0; i < AllPassLengthsL.Length; i++)
            {
                _reverbAllPassL[i] = new AllPassFilter(AllPassLengthsL[i]);
                _reverbAllPassR[i] = new AllPassFilter(AllPassLengthsR[i]);
            }
        }

        public void Process(Span<float> stereoSpan)
        {
            int frames = stereoSpan.Length / Channels;
            if (frames <= 0) return;

            int requestedMode = _mode;
            float targetIntensity = _intensity;
            if (!float.IsFinite(targetIntensity)) targetIntensity = 0.65f;

            // Bit-exact bypass when Off and crossfade has fully settled
            if (requestedMode == 0 && _activeMode == 0 && _effectBlend <= 0f)
            {
                _effectBlend = 0f;
                _smoothedIntensity = targetIntensity;
                return;
            }

            for (int i = 0; i < frames; i++)
            {
                _smoothedIntensity += (targetIntensity - _smoothedIntensity) * IntensitySmoothCoeff;

                if (_activeMode != requestedMode)
                {
                    if (_activeMode == 0)
                    {
                        ClearDspState();
                        _activeMode = requestedMode;
                        _effectBlend = 0f;
                        _smoothedIntensity = targetIntensity;
                    }
                    else
                    {
                        _effectBlend -= BlendStep;
                        if (_effectBlend <= 0f)
                        {
                            _effectBlend = 0f;
                            ClearDspState();
                            _activeMode = requestedMode;
                            _smoothedIntensity = targetIntensity;
                            if (_activeMode == 0)
                            {
                                continue;
                            }
                        }
                    }
                }

                if (_activeMode == requestedMode && _activeMode != 0 && _effectBlend < 1.0f)
                {
                    _effectBlend = MathF.Min(1.0f, _effectBlend + BlendStep);
                }

                if (_activeMode == 0 || _effectBlend <= 0f)
                {
                    continue;
                }

                int idx = i * Channels;
                float dryL = SanitizeSample(stereoSpan[idx]);
                float dryR = SanitizeSample(stereoSpan[idx + 1]);

                float wetL;
                float wetR;
                switch ((VoiceEffectMode)_activeMode)
                {
                    case VoiceEffectMode.RoomIsolate:
                        ProcessRoomIsolate(dryL, dryR, _smoothedIntensity, out wetL, out wetR);
                        break;
                    case VoiceEffectMode.Echo:
                        ProcessEcho(dryL, dryR, _smoothedIntensity, out wetL, out wetR);
                        break;
                    case VoiceEffectMode.Telephone:
                        ProcessTelephone(dryL, dryR, _smoothedIntensity, out wetL, out wetR);
                        break;
                    case VoiceEffectMode.Radio:
                        ProcessRadio(dryL, dryR, _smoothedIntensity, out wetL, out wetR);
                        break;
                    case VoiceEffectMode.HallReverb:
                        ProcessHallReverb(dryL, dryR, _smoothedIntensity, out wetL, out wetR);
                        break;
                    default:
                        wetL = dryL;
                        wetR = dryR;
                        break;
                }

                wetL = SoftProtect(wetL);
                wetR = SoftProtect(wetR);

                float blend = _effectBlend;
                stereoSpan[idx] = dryL + blend * (wetL - dryL);
                stereoSpan[idx + 1] = dryR + blend * (wetR - dryR);
            }
        }

        public void Reset()
        {
            ClearDspState();
            _activeMode = 0;
            _effectBlend = 0f;
            float intensity = _intensity;
            _smoothedIntensity = float.IsFinite(intensity) ? intensity : 0.65f;
        }

        private void ClearDspState()
        {
            _isoHpf1.Reset();
            _isoHpf2.Reset();
            _isoBoxNotch.Reset();
            _isoPresencePeak.Reset();
            _isoAirLpf.Reset();
            _isoGateEnv = 0f;
            _isoGateGain = 1.0f;
            _isoCompEnv = 0f;

            Array.Clear(_echoBuffer, 0, _echoBuffer.Length);
            _echoWriteIdx = 0;
            _echoHpStateL = 0f;
            _echoHpStateR = 0f;
            _echoLpStateL = 0f;
            _echoLpStateR = 0f;

            _teleHpf1.Reset();
            _teleHpf2.Reset();
            _teleLpf1.Reset();
            _teleLpf2.Reset();
            _telePresence.Reset();
            _teleDcPrevInL = 0f;
            _teleDcPrevOutL = 0f;
            _teleDcPrevInR = 0f;
            _teleDcPrevOutR = 0f;
            _teleLevelEnv = 0f;

            _radioHpf.Reset();
            _radioWarmth.Reset();
            _radioDeMud.Reset();
            _radioClarity.Reset();
            _radioCompEnv = 0f;

            for (int i = 0; i < _reverbCombsL.Length; i++)
            {
                _reverbCombsL[i].Reset();
                _reverbCombsR[i].Reset();
            }
            for (int i = 0; i < _reverbAllPassL.Length; i++)
            {
                _reverbAllPassL[i].Reset();
                _reverbAllPassR[i].Reset();
            }
            _reverbSendHpL = 0f;
            _reverbSendHpR = 0f;
        }

        private void ProcessRoomIsolate(float dryL, float dryR, float intensity, out float outL, out float outR)
        {
            float l = dryL;
            float r = dryR;

            // 4th-order Butterworth HPF @ 110 Hz + 290 Hz room boxiness notch
            _isoHpf1.Process(ref l, ref r);
            _isoHpf2.Process(ref l, ref r);
            _isoBoxNotch.Process(ref l, ref r);

            // Fast de-room downward expander / soft noise gate
            float det = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float envCoeff = det > _isoGateEnv ? IsoGateAttack : IsoGateRelease;
            _isoGateEnv = (_isoGateEnv * envCoeff) + (det * (1f - envCoeff));
            if (_isoGateEnv < 1e-18f) _isoGateEnv = 0f;

            float threshDb = -48.0f + (14.0f * intensity); // -48 dBFS .. -34 dBFS
            float floorGain = 0.15f - (0.11f * intensity); // 0.15 .. 0.04
            float envDb = 20.0f * MathF.Log10(MathF.Max(_isoGateEnv, 1e-6f));

            const float halfKnee = 4.0f;
            const float expSlope = 2.5f;
            float under = (threshDb + halfKnee) - envDb;
            float targetGateGain;
            if (under <= 0f)
            {
                targetGateGain = 1.0f;
            }
            else
            {
                float gainDb = under < (2.0f * halfKnee)
                    ? -(expSlope * under * under) / (4.0f * halfKnee)
                    : -expSlope * (under - halfKnee);
                targetGateGain = MathF.Max(floorGain, MathF.Pow(10.0f, gainDb / 20.0f));
            }

            float gateRampCoeff = targetGateGain > _isoGateGain ? IsoGateGainOpen : IsoGateGainClose;
            _isoGateGain = (_isoGateGain * gateRampCoeff) + (targetGateGain * (1f - gateRampCoeff));

            l *= _isoGateGain;
            r *= _isoGateGain;

            // Vocal presence @ 3.5 kHz + gentle air LPF @ 14 kHz
            _isoPresencePeak.Process(ref l, ref r);
            _isoAirLpf.Process(ref l, ref r);

            // Gentle peak compressor (-16 dBFS, 2.5:1)
            float compPeak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float compCoeff = compPeak > _isoCompEnv ? IsoCompAttack : IsoCompRelease;
            _isoCompEnv = (_isoCompEnv * compCoeff) + (compPeak * (1f - compCoeff));
            if (_isoCompEnv < 1e-18f) _isoCompEnv = 0f;

            float compGain = IsoCompMakeup;
            if (_isoCompEnv > IsoCompThresholdLin)
            {
                float compEnvDb = 20.0f * MathF.Log10(_isoCompEnv);
                float overDb = compEnvDb - (-16.0f);
                float redDb = overDb * 0.6f; // 1 - (1 / 2.5)
                compGain *= MathF.Pow(10.0f, -redDb / 20.0f);
            }

            l *= compGain;
            r *= compGain;

            outL = dryL + intensity * (l - dryL);
            outR = dryR + intensity * (r - dryR);
        }

        private void ProcessEcho(float dryL, float dryR, float intensity, out float outL, out float outR)
        {
            int readIdxL = ((_echoWriteIdx - EchoDelayFramesL) & EchoMask) * Channels;
            int readIdxR = ((_echoWriteIdx - EchoDelayFramesR) & EchoMask) * Channels + 1;

            float tapL = _echoBuffer[readIdxL];
            float tapR = _echoBuffer[readIdxR];

            // In-loop 1-pole HPF (~240 Hz)
            _echoHpStateL += EchoHpAlpha * (tapL - _echoHpStateL);
            _echoHpStateR += EchoHpAlpha * (tapR - _echoHpStateR);
            if (MathF.Abs(_echoHpStateL) < 1e-18f) _echoHpStateL = 0f;
            if (MathF.Abs(_echoHpStateR) < 1e-18f) _echoHpStateR = 0f;
            float hpL = tapL - _echoHpStateL;
            float hpR = tapR - _echoHpStateR;

            // In-loop 1-pole LPF (~3.0 kHz)
            _echoLpStateL += EchoLpAlpha * (hpL - _echoLpStateL);
            _echoLpStateR += EchoLpAlpha * (hpR - _echoLpStateR);
            if (MathF.Abs(_echoLpStateL) < 1e-18f) _echoLpStateL = 0f;
            if (MathF.Abs(_echoLpStateR) < 1e-18f) _echoLpStateR = 0f;

            float filtL = _echoLpStateL;
            float filtR = _echoLpStateR;

            float feedback = Math.Clamp(0.22f + (0.25f * intensity), 0f, 0.48f);

            // Stereo ping-pong cross-feedback with soft tanh loop protection
            float writeL = Math.Clamp(dryL + (feedback * MathF.Tanh(filtR)), -2.0f, 2.0f);
            float writeR = Math.Clamp(dryR + (feedback * MathF.Tanh(filtL)), -2.0f, 2.0f);

            int writeIdx = _echoWriteIdx * Channels;
            _echoBuffer[writeIdx] = SanitizeSample(writeL);
            _echoBuffer[writeIdx + 1] = SanitizeSample(writeR);
            _echoWriteIdx = (_echoWriteIdx + 1) & EchoMask;

            float wetGain = 0.15f + (0.55f * intensity);
            outL = dryL + (filtL * wetGain);
            outR = dryR + (filtR * wetGain);
        }

        private void ProcessTelephone(float dryL, float dryR, float intensity, out float outL, out float outR)
        {
            float l = dryL;
            float r = dryR;

            // 4th-order bandpass (340 Hz HPF + 3.2 kHz LPF) + nasal peak @ 1.35 kHz
            _teleHpf1.Process(ref l, ref r);
            _teleHpf2.Process(ref l, ref r);
            _teleLpf1.Process(ref l, ref r);
            _teleLpf2.Process(ref l, ref r);
            _telePresence.Process(ref l, ref r);

            // Warm asymmetric soft-clip saturation
            float drive = 1.4f + (2.6f * intensity);
            float bias = 0.12f * intensity;
            float biasOffset = MathF.Tanh(bias);

            float satL = (MathF.Tanh((l * drive) + bias) - biasOffset) / drive;
            float satR = (MathF.Tanh((r * drive) + bias) - biasOffset) / drive;

            // Remove any dynamic DC offset introduced by asymmetric clipping
            float dcL = satL - _teleDcPrevInL + (TeleDcAlpha * _teleDcPrevOutL);
            _teleDcPrevInL = satL;
            _teleDcPrevOutL = MathF.Abs(dcL) < 1e-18f ? 0f : dcL;

            float dcR = satR - _teleDcPrevInR + (TeleDcAlpha * _teleDcPrevOutR);
            _teleDcPrevInR = satR;
            _teleDcPrevOutR = MathF.Abs(dcR) < 1e-18f ? 0f : dcR;

            // Balanced makeup gain + soft speech leveling
            const float makeup = 1.25f;
            float leveledL = dcL * makeup;
            float leveledR = dcR * makeup;

            float peak = MathF.Max(MathF.Abs(leveledL), MathF.Abs(leveledR));
            float levelCoeff = peak > _teleLevelEnv ? TeleLevelAttack : TeleLevelRelease;
            _teleLevelEnv = (_teleLevelEnv * levelCoeff) + (peak * (1f - levelCoeff));
            if (_teleLevelEnv < 1e-18f) _teleLevelEnv = 0f;

            float levelGain = _teleLevelEnv > 0.45f ? 0.45f / _teleLevelEnv : 1.0f;
            leveledL *= levelGain;
            leveledR *= levelGain;

            outL = dryL + intensity * (leveledL - dryL);
            outR = dryR + intensity * (leveledR - dryR);
        }

        private void ProcessRadio(float dryL, float dryR, float intensity, out float outL, out float outR)
        {
            float l = dryL;
            float r = dryR;

            // Broadcast EQ curve: 75Hz HPF, 145Hz warmth, 380Hz de-mud, 4.5kHz clarity
            _radioHpf.Process(ref l, ref r);
            _radioWarmth.Process(ref l, ref r);
            _radioDeMud.Process(ref l, ref r);
            _radioClarity.Process(ref l, ref r);

            // Smooth 3:1 optical-style compressor with soft 4 dB knee
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float coeff = peak > _radioCompEnv ? RadioCompAttack : RadioCompRelease;
            _radioCompEnv = (_radioCompEnv * coeff) + (peak * (1f - coeff));
            if (_radioCompEnv < 1e-18f) _radioCompEnv = 0f;

            float envDb = 20.0f * MathF.Log10(MathF.Max(_radioCompEnv, 1e-6f));
            const float kneeWidth = 4.0f;
            const float compSlope = 2.0f / 3.0f; // 1 - (1 / 3)
            float overDb = envDb - RadioCompThresholdDb;

            float gainDb = 0f;
            if (overDb > -0.5f * kneeWidth)
            {
                if (overDb < 0.5f * kneeWidth)
                {
                    float x = overDb + (0.5f * kneeWidth);
                    gainDb = -(compSlope * x * x) / (2.0f * kneeWidth);
                }
                else
                {
                    gainDb = -compSlope * overDb;
                }
            }

            float compGain = RadioCompMakeup * MathF.Pow(10.0f, gainDb / 20.0f);
            l = SoftRadioLimit(l * compGain);
            r = SoftRadioLimit(r * compGain);

            outL = dryL + intensity * (l - dryL);
            outR = dryR + intensity * (r - dryR);
        }

        private void ProcessHallReverb(float dryL, float dryR, float intensity, out float outL, out float outR)
        {
            // Gentle 160Hz pre-send HPF keeps reverb tail free of low plosive mud
            float inL = (0.75f * dryL) + (0.25f * dryR);
            float inR = (0.75f * dryR) + (0.25f * dryL);

            _reverbSendHpL += ReverbSendHpAlpha * (inL - _reverbSendHpL);
            _reverbSendHpR += ReverbSendHpAlpha * (inR - _reverbSendHpR);
            if (MathF.Abs(_reverbSendHpL) < 1e-18f) _reverbSendHpL = 0f;
            if (MathF.Abs(_reverbSendHpR) < 1e-18f) _reverbSendHpR = 0f;

            float sendL = (inL - _reverbSendHpL) * 0.25f;
            float sendR = (inR - _reverbSendHpR) * 0.25f;

            float combFeedback = Math.Clamp(0.74f + (0.15f * intensity), 0f, 0.90f);

            float sumL = 0f;
            float sumR = 0f;
            for (int k = 0; k < _reverbCombsL.Length; k++)
            {
                sumL += _reverbCombsL[k].Process(sendL, combFeedback, ReverbHfDamping);
                sumR += _reverbCombsR[k].Process(sendR, combFeedback, ReverbHfDamping);
            }

            // 2 series all-pass diffusers per channel
            float diffL = _reverbAllPassL[0].Process(sumL, ReverbAllPassGain);
            diffL = _reverbAllPassL[1].Process(diffL, ReverbAllPassGain);

            float diffR = _reverbAllPassR[0].Process(sumR, ReverbAllPassGain);
            diffR = _reverbAllPassR[1].Process(diffR, ReverbAllPassGain);

            float wetGain = 0.65f * intensity;
            outL = dryL + (diffL * wetGain);
            outR = dryR + (diffR * wetGain);
        }

        private static float SoftRadioLimit(float x)
        {
            const float thresh = 0.78f;
            const float ceiling = 0.95f;
            const float span = ceiling - thresh;
            if (x > thresh)
            {
                return thresh + span * MathF.Tanh((x - thresh) / span);
            }
            if (x < -thresh)
            {
                return -(thresh + span * MathF.Tanh((-x - thresh) / span));
            }
            return x;
        }

        private static float SanitizeSample(float x)
        {
            if (!float.IsFinite(x)) return 0f;
            if (MathF.Abs(x) < 1e-18f) return 0f;
            return Math.Clamp(x, -4.0f, 4.0f);
        }

        private static float SoftProtect(float x)
        {
            if (!float.IsFinite(x)) return 0f;
            if (MathF.Abs(x) < 1e-18f) return 0f;
            const float knee = 0.95f;
            const float ceiling = 1.25f;
            const float span = ceiling - knee;
            if (x > knee)
            {
                return knee + span * MathF.Tanh((x - knee) / span);
            }
            if (x < -knee)
            {
                return -(knee + span * MathF.Tanh((-x - knee) / span));
            }
            return x;
        }

        private struct StereoBiquad
        {
            private double _b0;
            private double _b1;
            private double _b2;
            private double _a1;
            private double _a2;
            private double _z1L;
            private double _z2L;
            private double _z1R;
            private double _z2R;

            public void SetHighPass(double freqHz, double q)
            {
                double w0 = 2.0 * Math.PI * freqHz / SampleRate;
                double cosW0 = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double invA0 = 1.0 / (1.0 + alpha);

                _b0 = (1.0 + cosW0) * 0.5 * invA0;
                _b1 = -(1.0 + cosW0) * invA0;
                _b2 = _b0;
                _a1 = (-2.0 * cosW0) * invA0;
                _a2 = (1.0 - alpha) * invA0;
            }

            public void SetLowPass(double freqHz, double q)
            {
                double w0 = 2.0 * Math.PI * freqHz / SampleRate;
                double cosW0 = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double invA0 = 1.0 / (1.0 + alpha);

                _b0 = (1.0 - cosW0) * 0.5 * invA0;
                _b1 = (1.0 - cosW0) * invA0;
                _b2 = _b0;
                _a1 = (-2.0 * cosW0) * invA0;
                _a2 = (1.0 - alpha) * invA0;
            }

            public void SetPeakingEq(double freqHz, double q, double gainDb)
            {
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * freqHz / SampleRate;
                double cosW0 = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double invA0 = 1.0 / (1.0 + (alpha / a));

                _b0 = (1.0 + (alpha * a)) * invA0;
                _b1 = (-2.0 * cosW0) * invA0;
                _b2 = (1.0 - (alpha * a)) * invA0;
                _a1 = (-2.0 * cosW0) * invA0;
                _a2 = (1.0 - (alpha / a)) * invA0;
            }

            public void Process(ref float left, ref float right)
            {
                double xL = left;
                double yL = (_b0 * xL) + _z1L;
                _z1L = (_b1 * xL) - (_a1 * yL) + _z2L;
                _z2L = (_b2 * xL) - (_a2 * yL);

                double xR = right;
                double yR = (_b0 * xR) + _z1R;
                _z1R = (_b1 * xR) - (_a1 * yR) + _z2R;
                _z2R = (_b2 * xR) - (_a2 * yR);

                if (!double.IsFinite(_z1L) || Math.Abs(_z1L) < 1e-20) _z1L = 0.0;
                if (!double.IsFinite(_z2L) || Math.Abs(_z2L) < 1e-20) _z2L = 0.0;
                if (!double.IsFinite(_z1R) || Math.Abs(_z1R) < 1e-20) _z1R = 0.0;
                if (!double.IsFinite(_z2R) || Math.Abs(_z2R) < 1e-20) _z2R = 0.0;

                left = double.IsFinite(yL) ? (float)yL : 0f;
                right = double.IsFinite(yR) ? (float)yR : 0f;
            }

            public void Reset()
            {
                _z1L = 0.0;
                _z2L = 0.0;
                _z1R = 0.0;
                _z2R = 0.0;
            }
        }

        private struct CombFilter
        {
            private readonly float[] _buffer;
            private int _index;
            private float _dampState;

            public CombFilter(int lengthFrames)
            {
                _buffer = new float[lengthFrames];
                _index = 0;
                _dampState = 0f;
            }

            public float Process(float input, float feedback, float damp)
            {
                float output = _buffer[_index];
                _dampState = (output * (1f - damp)) + (_dampState * damp);
                if (!float.IsFinite(_dampState) || MathF.Abs(_dampState) < 1e-18f)
                {
                    _dampState = 0f;
                }

                float next = input + (feedback * MathF.Tanh(_dampState));
                _buffer[_index] = float.IsFinite(next) ? Math.Clamp(next, -2.0f, 2.0f) : 0f;

                _index++;
                if (_index >= _buffer.Length) _index = 0;

                return output;
            }

            public void Reset()
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _index = 0;
                _dampState = 0f;
            }
        }

        private struct AllPassFilter
        {
            private readonly float[] _buffer;
            private int _index;

            public AllPassFilter(int lengthFrames)
            {
                _buffer = new float[lengthFrames];
                _index = 0;
            }

            public float Process(float input, float gain)
            {
                float delayed = _buffer[_index];
                float v = input - (gain * delayed);
                if (!float.IsFinite(v) || MathF.Abs(v) < 1e-18f)
                {
                    v = 0f;
                }

                _buffer[_index] = Math.Clamp(v, -4.0f, 4.0f);
                float output = delayed + (gain * v);

                _index++;
                if (_index >= _buffer.Length) _index = 0;

                return float.IsFinite(output) ? output : 0f;
            }

            public void Reset()
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _index = 0;
            }
        }
    }
}
