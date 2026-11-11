using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerOnline.Core.Audio
{
    public sealed class MicrophoneCapture : IDisposable
    {
        private WasapiCapture? _capture;
        private MMDevice? _device;
        private readonly AdaptiveJitterBuffer _outputBuffer;
        private readonly object _stateLock = new();
        private bool _isCapturing;
        private bool _disposed;

        private string? _currentDeviceId;
        private const int TargetSampleRate = 48000;

        // Stateful resampler history for non-48kHz microphones
        private double _resamplePhase;
        private float _histL0, _histL1, _histL2;
        private float _histR0, _histR1, _histR2;
        private bool _hasHistory;

        // Gentle studio downward expander to reduce idle room floor without clipping speech
        private float _gateGain = 1.0f;
        private const float GateThreshold = 0.0015f; // ~ -56 dBFS

        private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<bool>? StateChanged;

        public AdaptiveJitterBuffer OutputBuffer => _outputBuffer;
        public bool IsCapturing => _isCapturing;
        public string? CurrentDeviceId => _currentDeviceId;

        public float PeakLevel => _outputBuffer.PeakLevel;
        public float RmsLevel => _outputBuffer.RmsLevel;

        public MicrophoneCapture()
        {
            _outputBuffer = new AdaptiveJitterBuffer();
        }

        public void Start(MMDevice device)
        {
            lock (_stateLock)
            {
                Stop();

                try
                {
                    _device = device;
                    _currentDeviceId = device.ID;

                    _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 16);
                    _capture.ShareMode = AudioClientShareMode.Shared;
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;

                    _outputBuffer.Clear();
                    _resamplePhase = 0;
                    _hasHistory = false;
                    _gateGain = 1.0f;

                    _capture.StartRecording();
                    _isCapturing = true;
                    StateChanged?.Invoke(this, true);
                }
                catch (Exception ex)
                {
                    _isCapturing = false;
                    ErrorOccurred?.Invoke(this, $"Failed to start microphone: {ex.Message}");
                    StateChanged?.Invoke(this, false);
                }
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isCapturing && _capture == null) return;

                _isCapturing = false;
                if (_capture != null)
                {
                    try
                    {
                        _capture.DataAvailable -= OnDataAvailable;
                        _capture.RecordingStopped -= OnRecordingStopped;
                        _capture.StopRecording();
                        _capture.Dispose();
                    }
                    catch { }
                    _capture = null;
                }
                _outputBuffer.Clear();
                _hasHistory = false;
                StateChanged?.Invoke(this, false);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (_stateLock)
            {
                _isCapturing = false;
                if (e.Exception != null)
                {
                    ErrorOccurred?.Invoke(this, $"Microphone stopped: {e.Exception.Message}");
                }
                StateChanged?.Invoke(this, false);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isCapturing || e.BytesRecorded <= 0 || _capture == null) return;

            var waveFormat = _capture.WaveFormat;
            int channels = waveFormat.Channels;
            int sampleRate = waveFormat.SampleRate;

            float[] inputSamples = ExtractFloatSamples(e.Buffer, e.BytesRecorded, waveFormat);
            if (inputSamples.Length == 0) return;

            // Normalize to stereo
            float[] stereoSamples;
            if (channels == 1)
            {
                stereoSamples = new float[inputSamples.Length * 2];
                for (int i = 0; i < inputSamples.Length; i++)
                {
                    stereoSamples[i * 2] = inputSamples[i];
                    stereoSamples[i * 2 + 1] = inputSamples[i];
                }
            }
            else if (channels == 2)
            {
                stereoSamples = inputSamples;
            }
            else
            {
                int frameCount = inputSamples.Length / channels;
                stereoSamples = new float[frameCount * 2];
                for (int i = 0; i < frameCount; i++)
                {
                    stereoSamples[i * 2] = inputSamples[i * channels];
                    stereoSamples[i * 2 + 1] = inputSamples[i * channels + 1];
                }
            }

            // Resample if device is not 48,000 Hz
            float[] finalSamples;
            if (sampleRate != TargetSampleRate)
            {
                finalSamples = ResampleStereoStateful(stereoSamples, sampleRate, TargetSampleRate);
            }
            else
            {
                finalSamples = stereoSamples;
            }

            if (finalSamples.Length == 0) return;

            ApplyDownwardExpander(finalSamples);
            _outputBuffer.Write(finalSamples);
        }

        private void ApplyDownwardExpander(Span<float> samples)
        {
            int frames = samples.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                float abs = Math.Max(Math.Abs(samples[i * 2]), Math.Abs(samples[i * 2 + 1]));
                float targetGain = (abs < GateThreshold) ? 0.25f : 1.0f;
                _gateGain += (targetGain - _gateGain) * 0.005f;

                samples[i * 2] *= _gateGain;
                samples[i * 2 + 1] *= _gateGain;
            }
        }

        private static float[] ExtractFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            bool isIeeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            if (!isIeeeFloat && format is WaveFormatExtensible ext)
            {
                // WASAPI shared mode almost always uses WaveFormatExtensible with IEEE_FLOAT subtype
                isIeeeFloat = ext.SubFormat == SubtypeIeeeFloat || (ext.SubFormat != SubtypePcm && format.BitsPerSample == 32);
            }
            else if (!isIeeeFloat && format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
            {
                isIeeeFloat = true;
            }

            if (isIeeeFloat)
            {
                int sampleCount = bytesRecorded / 4;
                float[] samples = new float[sampleCount];
                Buffer.BlockCopy(buffer, 0, samples, 0, sampleCount * 4);
                return samples;
            }

            if (format.BitsPerSample == 16)
            {
                int sampleCount = bytesRecorded / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short val = BitConverter.ToInt16(buffer, i * 2);
                    samples[i] = val / 32768f;
                }
                return samples;
            }

            if (format.BitsPerSample == 24)
            {
                int sampleCount = bytesRecorded / 3;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int b0 = buffer[i * 3];
                    int b1 = buffer[i * 3 + 1];
                    int b2 = (sbyte)buffer[i * 3 + 2];
                    int val = (b2 << 16) | (b1 << 8) | b0;
                    samples[i] = val / 8388608f;
                }
                return samples;
            }

            if (format.BitsPerSample == 32)
            {
                int sampleCount = bytesRecorded / 4;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int val = BitConverter.ToInt32(buffer, i * 4);
                    samples[i] = val / 2147483648f;
                }
                return samples;
            }

            return Array.Empty<float>();
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
                GetFrameWithHistory(input, inFrames, idx + 1, out float r3L, out float r3R);

                if (outFrameCount * 2 + 1 < output.Length)
                {
                    output[outFrameCount * 2] = CubicHermite(l0, l1, l2, r3L, t);
                    output[outFrameCount * 2 + 1] = CubicHermite(r0, r1, r2, r3R, t);
                    outFrameCount++;
                }

                _resamplePhase += step;
            }

            _resamplePhase -= inFrames;

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
