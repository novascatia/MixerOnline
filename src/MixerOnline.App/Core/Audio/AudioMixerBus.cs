using System;
using NAudio.Wave;

namespace MixerOnline.Core.Audio
{
    public sealed class AudioMixerBus : ISampleProvider, IWaveProvider
    {
        private readonly MicrophoneCapture _micCapture;
        private readonly BrowserAudioReceiver _browserReceiver;
        private readonly AdaptiveJitterBuffer _monitorBuffer;
        private readonly AudioLimiter _limiter = new();
        private readonly AudioLimiter _monitorLimiter = new();
        private readonly DelayLine _micDelayLine = new();
        private readonly VoiceEffectProcessor _voiceEffectProcessor = new();
        private readonly PitchShifter _browserPitchShifter = new();

        private readonly WaveFormat _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        private float[] _tempMicBuffer = new float[2048];
        private float[] _tempBrowserBuffer = new float[2048];
        private float[] _tempMonitorBuffer = new float[2048];
        private float[] _floatConversionBuffer = new float[2048];

        private volatile int _monitorSourceMode = 0; // 0 = Music (Default), 1 = Mic, 2 = Both

        // Volumes
        private float _targetMicVolume = 1.0f;
        private bool _isMicMuted;
        private float _targetBrowserVolume = 1.0f;
        private bool _isBrowserMuted;
        private float _targetMasterVolume = 1.0f;
        private bool _isMasterMuted;

        private float _currentMicGain = 1.0f;
        private float _currentBrowserGain = 1.0f;
        private float _currentMasterGain = 1.0f;
        private const float SmoothingCoeff = 0.015f;

        // Metering
        private float _masterPeakLeft;
        private float _masterPeakRight;
        private float _masterRms;
        private readonly object _meterLock = new();

        public WaveFormat WaveFormat => _waveFormat;
        public AdaptiveJitterBuffer MonitorBuffer => _monitorBuffer;

        public float MicrophoneVolume
        {
            get => _targetMicVolume;
            set => _targetMicVolume = Math.Clamp(value, 0f, 2.0f);
        }

        public bool IsMicrophoneMuted
        {
            get => _isMicMuted;
            set => _isMicMuted = value;
        }

        public int MicDelayMs
        {
            get => _micDelayLine.DelayMs;
            set => _micDelayLine.SetDelayMs(value);
        }

        public int VoiceEffectMode
        {
            get => _voiceEffectProcessor.Mode;
            set => _voiceEffectProcessor.Mode = value;
        }

        public float VoiceEffectIntensity
        {
            get => _voiceEffectProcessor.Intensity;
            set => _voiceEffectProcessor.Intensity = value;
        }

        public float BrowserVolume
        {
            get => _targetBrowserVolume;
            set => _targetBrowserVolume = Math.Clamp(value, 0f, 2.0f);
        }

        public bool IsBrowserMuted
        {
            get => _isBrowserMuted;
            set => _isBrowserMuted = value;
        }

        public int BrowserPitchSemitones
        {
            get => _browserPitchShifter.Semitones;
            set => _browserPitchShifter.Semitones = value;
        }

        public float MasterVolume
        {
            get => _targetMasterVolume;
            set => _targetMasterVolume = Math.Clamp(value, 0f, 2.0f);
        }

        public bool IsMasterMuted
        {
            get => _isMasterMuted;
            set => _isMasterMuted = value;
        }

        public int MonitorSourceMode
        {
            get => _monitorSourceMode;
            set => _monitorSourceMode = Math.Clamp(value, 0, 2);
        }

        public float MasterPeakLeft
        {
            get { lock (_meterLock) return _masterPeakLeft; }
        }

        public float MasterPeakRight
        {
            get { lock (_meterLock) return _masterPeakRight; }
        }

        public float MasterRms
        {
            get { lock (_meterLock) return _masterRms; }
        }

        public AudioMixerBus(MicrophoneCapture micCapture, BrowserAudioReceiver browserReceiver, int monitorCapacity = 48000)
        {
            _micCapture = micCapture ?? throw new ArgumentNullException(nameof(micCapture));
            _browserReceiver = browserReceiver ?? throw new ArgumentNullException(nameof(browserReceiver));
            _monitorBuffer = new AdaptiveJitterBuffer(monitorCapacity, initialPrerollFrames: 240, initialMaxFrames: 1440);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            if (_tempMicBuffer.Length < count)
            {
                _tempMicBuffer = new float[count];
                _tempBrowserBuffer = new float[count];
            }
            if (_tempMonitorBuffer.Length < count)
            {
                _tempMonitorBuffer = new float[count];
            }

            Span<float> micSpan = _tempMicBuffer.AsSpan(0, count);
            Span<float> browserSpan = _tempBrowserBuffer.AsSpan(0, count);

            _micCapture.OutputBuffer.Read(micSpan);
            _voiceEffectProcessor.Process(micSpan);
            _micDelayLine.Process(micSpan);
            _browserReceiver.Buffer.Read(browserSpan);
            _browserPitchShifter.Process(browserSpan);

            float targetMic = _isMicMuted ? 0f : _targetMicVolume;
            float targetBrowser = _isBrowserMuted ? 0f : _targetBrowserVolume;
            float targetMaster = _isMasterMuted ? 0f : _targetMasterVolume;
            int monitorMode = _monitorSourceMode;

            int frames = count / 2;
            for (int i = 0; i < frames; i++)
            {
                _currentMicGain += (targetMic - _currentMicGain) * SmoothingCoeff;
                _currentBrowserGain += (targetBrowser - _currentBrowserGain) * SmoothingCoeff;
                _currentMasterGain += (targetMaster - _currentMasterGain) * SmoothingCoeff;

                int idx = i * 2;
                float micScaledL = micSpan[idx] * _currentMicGain;
                float micScaledR = micSpan[idx + 1] * _currentMicGain;

                float browScaledL = browserSpan[idx] * _currentBrowserGain;
                float browScaledR = browserSpan[idx + 1] * _currentBrowserGain;

                float mixL = (micScaledL + browScaledL) * _currentMasterGain;
                float mixR = (micScaledR + browScaledR) * _currentMasterGain;

                buffer[offset + idx] = mixL;
                buffer[offset + idx + 1] = mixR;

                if (monitorMode == 0) // 0 = Music only (Default)
                {
                    _tempMonitorBuffer[idx] = browScaledL * _currentMasterGain;
                    _tempMonitorBuffer[idx + 1] = browScaledR * _currentMasterGain;
                }
                else if (monitorMode == 1) // 1 = Mic only
                {
                    _tempMonitorBuffer[idx] = micScaledL * _currentMasterGain;
                    _tempMonitorBuffer[idx + 1] = micScaledR * _currentMasterGain;
                }
            }

            Span<float> outputSpan = buffer.AsSpan(offset, count);
            _limiter.ProcessStereoBlock(outputSpan);

            float peakL = 0f;
            float peakR = 0f;
            double sumSquares = 0;

            for (int i = 0; i < frames; i++)
            {
                int idx = offset + (i * 2);
                float absL = Math.Abs(buffer[idx]);
                float absR = Math.Abs(buffer[idx + 1]);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
                sumSquares += (absL * absL) + (absR * absR);
            }

            float rms = count > 0 ? (float)Math.Sqrt(sumSquares / count) : 0f;

            lock (_meterLock)
            {
                _masterPeakLeft = Math.Max(peakL, _masterPeakLeft * 0.88f);
                _masterPeakRight = Math.Max(peakR, _masterPeakRight * 0.88f);
                _masterRms = (_masterRms * 0.75f) + (rms * 0.25f);
            }

            Span<float> monitorSpan;
            if (monitorMode == 0 || monitorMode == 1)
            {
                monitorSpan = _tempMonitorBuffer.AsSpan(0, count);
                _monitorLimiter.ProcessStereoBlock(monitorSpan);
            }
            else // 2 = Both (Full Master Mix)
            {
                monitorSpan = outputSpan;
            }

            _monitorBuffer.Write(monitorSpan);

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

        public void ResetLevels()
        {
            lock (_meterLock)
            {
                _masterPeakLeft = 0f;
                _masterPeakRight = 0f;
                _masterRms = 0f;
            }
            _limiter.Reset();
            _monitorLimiter.Reset();
            _micDelayLine.Reset();
            _voiceEffectProcessor.Reset();
            _browserPitchShifter.Reset();
        }
    }
}
