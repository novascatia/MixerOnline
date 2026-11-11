using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MixerOnline.Core.Audio;
using MixerOnline.Core.Config;
using MixerOnline.Core.Network;
using MixerOnline.Core.Utils;

namespace MixerOnline.Tests
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("         MixerOnline Automated Test Suite        ");
            Console.WriteLine("=================================================");

            int passed = 0;
            int failed = 0;
            int skipped = 0;

            bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

            void Assert(string testName, bool condition, string detail = "")
            {
                if (condition)
                {
                    Console.WriteLine($"[PASS] {testName} {detail}");
                    passed++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {testName} {detail}");
                    Console.ResetColor();
                    failed++;
                }
            }

            void Skip(string testName, string reason)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[SKIP] {testName} ({reason})");
                Console.ResetColor();
                skipped++;
            }

            static int FindAvailablePort(int preferredStart = 39150)
            {
                for (int port = preferredStart; port < preferredStart + 200; port++)
                {
                    try
                    {
                        var probe = new TcpListener(IPAddress.Loopback, port);
                        probe.Start();
                        probe.Stop();
                        return port;
                    }
                    catch { }
                }

                var fallback = new TcpListener(IPAddress.Loopback, 0);
                fallback.Start();
                int freePort = ((IPEndPoint)fallback.LocalEndpoint).Port;
                fallback.Stop();
                return freePort;
            }

            static async Task<(BrowserBridgeServer? Server, int Port, string StatusBody, string? Error)> StartBridgeWithRetryAsync(BrowserAudioReceiver receiver, int startPort = 39150)
            {
                string? lastError = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    int port = FindAvailablePort(startPort + attempt * 10);
                    var candidate = new BrowserBridgeServer(receiver, port);
                    string? err = null;
                    candidate.ExtensionError += (_, msg) => err ??= msg;
                    candidate.Start();

                    using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                    for (int poll = 0; poll < 10; poll++)
                    {
                        if (err != null) break;
                        try
                        {
                            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/status");
                            return (candidate, port, body, null);
                        }
                        catch
                        {
                            await Task.Delay(50);
                        }
                    }

                    lastError = err ?? $"Timed out polling http://127.0.0.1:{port}/status";
                    candidate.Dispose();
                }
                return (null, 0, string.Empty, lastError);
            }

            // Test 1: RingBuffer FIFO and overflow protection
            try
            {
                var rb = new RingBuffer(1024);
                float[] writeData = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
                rb.Write(writeData);
                Assert("RingBuffer Write Count", rb.Count == 4);

                float[] readData = new float[4];
                int read = rb.Read(readData);
                Assert("RingBuffer Read Count", read == 4);
                Assert("RingBuffer Data Integrity", Math.Abs(readData[2] - 0.3f) < 0.0001f);
            }
            catch (Exception ex)
            {
                Assert("RingBuffer Exception", false, ex.Message);
            }

            // Test 2: AudioLimiter anti-clipping
            try
            {
                float normal = AudioLimiter.ProcessSample(0.5f);
                Assert("Limiter Pass-through", Math.Abs(normal - 0.5f) < 0.0001f);

                float clipped = AudioLimiter.ProcessSample(3.5f);
                Assert("Limiter Ceiling", clipped <= 0.999f && clipped > 0.8f, $"Result: {clipped}");

                float negativeClipped = AudioLimiter.ProcessSample(-4.0f);
                Assert("Limiter Negative Ceiling", negativeClipped >= -0.999f && negativeClipped < -0.8f, $"Result: {negativeClipped}");
            }
            catch (Exception ex)
            {
                Assert("AudioLimiter Exception", false, ex.Message);
            }

            // Test 2b: DelayLine Sample-Accurate Delay & Passthrough
            try
            {
                var delay = new DelayLine(0);
                float[] direct = new float[] { 0.42f, 0.84f, -0.21f, -0.42f };
                delay.Process(direct);
                Assert("DelayLine Direct Passthrough (0ms)", Math.Abs(direct[0] - 0.42f) < 0.0001f && Math.Abs(direct[1] - 0.84f) < 0.0001f);

                // Set 10ms delay (480 frames = 960 float samples)
                var delay10ms = new DelayLine(10);
                // Push an impulse at frame 0
                float[] impulseBlock = new float[960]; // 480 frames
                impulseBlock[0] = 1.0f; // Left impulse
                impulseBlock[1] = -1.0f; // Right impulse
                delay10ms.Process(impulseBlock);

                // In the first 10ms block, the impulse was delayed, so output at frame 0 is silence (0.0f)
                Assert("DelayLine Initial Silence on Delay", Math.Abs(impulseBlock[0]) < 0.0001f);

                // Next 10ms block should output the impulse exactly at index 0
                float[] nextBlock = new float[960];
                delay10ms.Process(nextBlock);
                Assert("DelayLine 10ms Delayed Impulse Exact Match", Math.Abs(nextBlock[0] - 1.0f) < 0.0001f && Math.Abs(nextBlock[1] - (-1.0f)) < 0.0001f);
            }
            catch (Exception ex)
            {
                Assert("DelayLine Exception", false, ex.Message);
            }

            // Test 2c: PitchShifter Key Transposition & Bypass
            try
            {
                var pitch = new PitchShifter(0);
                float[] original = new float[] { 0.5f, -0.5f, 0.75f, -0.75f };
                pitch.Process(original);
                Assert("PitchShifter Bypass (0 semitones)", Math.Abs(original[0] - 0.5f) < 0.0001f && Math.Abs(original[2] - 0.75f) < 0.0001f);

                // Set +2 semitones
                var pitchUp = new PitchShifter(2);
                Assert("PitchShifter Semitones Clamped", pitchUp.Semitones == 2);

                // Prime with sine wave
                float[] sineBlock = new float[4800 * 2]; // 100ms
                for (int i = 0; i < 4800; i++)
                {
                    float s = (float)Math.Sin(2.0 * Math.PI * 440.0 * (i / 48000.0)) * 0.7f;
                    sineBlock[i * 2] = s;
                    sineBlock[i * 2 + 1] = s;
                }
                pitchUp.Process(sineBlock);

                // Verify output has signal and no NaNs
                bool noNans = true;
                float peak = 0f;
                for (int i = 0; i < sineBlock.Length; i++)
                {
                    if (float.IsNaN(sineBlock[i]) || float.IsInfinity(sineBlock[i])) noNans = false;
                    if (Math.Abs(sineBlock[i]) > peak) peak = Math.Abs(sineBlock[i]);
                }
                Assert("PitchShifter Numerical Stability (No NaNs/Inf)", noNans);
                Assert("PitchShifter Output Signal Presence", peak > 0.1f, $"Peak: {peak:F3}");

                // Test -3 semitones transposition
                var pitchDown = new PitchShifter(-3);
                Assert("PitchShifter Negative Semitones", pitchDown.Semitones == -3);
            }
            catch (Exception ex)
            {
                Assert("PitchShifter Exception", false, ex.Message);
            }

            // Test 3: DeviceManager WASAPI Enumeration & VB-Cable Detection
            try
            {
                using var dm = new DeviceManager();
                var captureDevs = dm.GetCaptureDevices();
                var renderDevs = dm.GetRenderDevices();

                if (isCi && captureDevs.Count == 0 && renderDevs.Count == 0)
                {
                    Skip("Capture Devices Enumerated", "Headless CI VM has no audio hardware");
                    Skip("Render Devices Enumerated", "Headless CI VM has no audio hardware");
                    Skip("VB-Cable Detection", "Headless CI VM has no VB-Cable driver");
                }
                else
                {
                    Assert("Capture Devices Enumerated", captureDevs.Count > 0, $"Found {captureDevs.Count} inputs");
                    Assert("Render Devices Enumerated", renderDevs.Count > 0, $"Found {renderDevs.Count} outputs");

                    bool vbInstalled = dm.IsVirtualCableInstalled();
                    var vbDevice = dm.FindVirtualCablePlaybackDevice();
                    Assert("VB-Cable Detection", vbInstalled && vbDevice != null, $"VB Device: {vbDevice?.Name}");
                }
            }
            catch (Exception ex)
            {
                Assert("DeviceManager Exception", false, ex.Message);
            }

            // Test 4: BrowserBridgeServer HTTP Health Endpoint, Origin Guard & WebSocket Protocol
            try
            {
                var receiver = new BrowserAudioReceiver();
                var (server, testPort, resp, startError) = await StartBridgeWithRetryAsync(receiver, 39150);

                if (server == null)
                {
                    if (isCi && startError != null && startError.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                    {
                        Skip("Bridge HTTP / WebSocket Suite", $"Restricted HttpListener ACL on headless CI: {startError}");
                    }
                    else
                    {
                        Assert("BrowserBridgeServer Startup", false, startError ?? "Unknown startup failure");
                    }
                }
                else
                {
                    using (server)
                    {
                        Assert("Bridge HTTP /status Endpoint", resp.Contains("MixerOnline") && resp.Contains($"\"port\":{testPort}"), $"Response: {resp}");

                        bool blockedWebOrigin = false;
                        try
                        {
                            using var wsEvil = new ClientWebSocket();
                            wsEvil.Options.SetRequestHeader("Origin", "https://evil.example");
                            await wsEvil.ConnectAsync(new Uri($"ws://127.0.0.1:{testPort}/ws"), CancellationToken.None);
                        }
                        catch (WebSocketException)
                        {
                            blockedWebOrigin = true;
                        }
                        Assert("Bridge Origin Guard Blocks Web Origin", blockedWebOrigin);

                        using var ws = new ClientWebSocket();
                        ws.Options.SetRequestHeader("Origin", "chrome-extension://testextensionid");
                        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{testPort}/ws"), CancellationToken.None);
                        Assert("Bridge WebSocket Connection (Chromium Extension Origin)", ws.State == WebSocketState.Open);

                        string helloJson = JsonSerializer.Serialize(new HelloMessage { Type = "HELLO", Client = "UnitTestExtension", Version = "2.2" });
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(helloJson)), WebSocketMessageType.Text, true, CancellationToken.None);

                        var tabListMsg = new TabListMessage
                        {
                            Type = "TAB_LIST",
                            Tabs = new System.Collections.Generic.List<TabItem>
                            {
                                new TabItem { Id = 101, Title = "YouTube - Lofi Beats", Url = "https://youtube.com", Audible = true },
                                new TabItem { Id = 102, Title = "Spotify Web Player", Url = "https://spotify.com", Audible = false }
                            }
                        };
                        string tabJson = JsonSerializer.Serialize(tabListMsg);
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(tabJson)), WebSocketMessageType.Text, true, CancellationToken.None);

                        float[] testPcm = new float[480 * 2]; // 10ms stereo
                        for (int i = 0; i < 480; i++)
                        {
                            float s = (float)Math.Sin(2 * Math.PI * 440 * (i / 48000.0)) * 0.5f;
                            testPcm[i * 2] = s;
                            testPcm[i * 2 + 1] = s;
                        }
                        byte[] rawBytes = new byte[testPcm.Length * 4];
                        Buffer.BlockCopy(testPcm, 0, rawBytes, 0, rawBytes.Length);

                        for (int packet = 0; packet < 5; packet++)
                        {
                            await ws.SendAsync(new ArraySegment<byte>(rawBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                            await Task.Delay(10);
                        }

                        await Task.Delay(100);
                        Assert("BrowserAudioReceiver Level Metering", receiver.PeakLevel > 0.1f, $"Peak: {receiver.PeakLevel:F3}");
                        Assert("BrowserAudioReceiver Buffer Count", receiver.Buffer.Count > 0, $"Buffered: {receiver.Buffer.Count} floats");

                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                        server.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                Assert("BrowserBridgeServer Exception", false, ex.Message);
            }

            // Test 5: Bit-Exact HD Continuity & Zero-Chopper Verification
            try
            {
                using var receiver = new BrowserAudioReceiver();
                receiver.ConfigureLatency(30); // 30ms latency configuration

                int frameCursor = 0;
                void PushPacket()
                {
                    float[] pkt = new float[128 * 2]; // 128 frames (2.67ms)
                    for (int i = 0; i < 128; i++)
                    {
                        float val = (float)Math.Sin(2.0 * Math.PI * 1000.0 * (frameCursor / 48000.0)) * 0.7f;
                        pkt[i * 2] = val;
                        pkt[i * 2 + 1] = -val;
                        frameCursor++;
                    }
                    byte[] bytes = new byte[pkt.Length * 4];
                    Buffer.BlockCopy(pkt, 0, bytes, 0, bytes.Length);
                    receiver.ProcessAudioFrame(bytes, 0, bytes.Length);
                }

                // Push enough 128-frame packets to exceed preroll cushion (2880 frames)
                for (int p = 0; p < 25; p++) PushPacket(); // 3200 frames (> 2880 preroll)

                bool allBlocksContinuous = true;
                double maxBitError = 0;
                int verifyFrame = 0;

                // Simulate 10 consecutive 30ms WASAPI reads (1440 frames) interleaved with incoming packets
                for (int block = 0; block < 10; block++)
                {
                    for (int p = 0; p < 12; p++) PushPacket(); // 1536 frames incoming

                    float[] outBuf = new float[1440 * 2];
                    receiver.Buffer.Read(outBuf);

                    for (int i = 0; i < 1440; i++)
                    {
                        float expectedL = (float)Math.Sin(2.0 * Math.PI * 1000.0 * (verifyFrame / 48000.0)) * 0.7f;
                        double errL = Math.Abs(outBuf[i * 2] - expectedL);
                        if (errL > maxBitError) maxBitError = errL;
                        verifyFrame++;
                    }
                }

                allBlocksContinuous = maxBitError < 0.00001;
                Assert("HD Bit-Exact 1:1 Audio Continuity (Zero Dropouts)", allBlocksContinuous, $"Max Sample Error: {maxBitError:F7}");
            }
            catch (Exception ex)
            {
                Assert("HD Continuity Test Exception", false, ex.Message);
            }

            // Test 6: AudioEngine Full Integration
            try
            {
                using var engine = new AudioEngine(FindAvailablePort(39250), 20);
                engine.Start();

                Assert("AudioEngine Initialized", true);
                Assert("MixerBus Format 48kHz Stereo", engine.MixerBus.WaveFormat.SampleRate == 48000 && engine.MixerBus.WaveFormat.Channels == 2);
                if (isCi && engine.DeviceManager.GetRenderDevices().Count == 0)
                {
                    Skip("Output Router Configured", "Headless CI VM has 0 audio render devices");
                }
                else
                {
                    Assert("Output Router Configured", !string.IsNullOrEmpty(engine.OutputRouter.PrimaryDeviceId));
                }

                engine.Dispose();
                Assert("AudioEngine Disposed Cleanly", true);

                string extSummary = BrowserLauncher.GetExtensionsUrlSummary();
                Assert("BrowserLauncher Multi-Browser Summary", extSummary.Contains("chrome://extensions") && extSummary.Contains("brave://extensions") && extSummary.Contains("edge://extensions"));
            }
            catch (Exception ex)
            {
                Assert("AudioEngine Exception", false, ex.Message);
            }

            // Test 7: Monitor Source Routing (Music vs Mic vs Both)
            try
            {
                var mic = new MicrophoneCapture();
                var browser = new BrowserAudioReceiver();
                browser.Buffer.ConfigureCushion(480, 1920);
                var bus = new AudioMixerBus(mic, browser);
                bus.MonitorBuffer.ConfigureCushion(480, 1920);

                // Default must be Music (0)
                Assert("Default Monitor Source is Music Only", bus.MonitorSourceMode == 0);

                // Feed signal into browser receiver (4 blocks = 1920 frames to exceed preroll)
                float[] browserTone = new float[480 * 2];
                for (int i = 0; i < 480; i++)
                {
                    browserTone[i * 2] = 0.6f;
                    browserTone[i * 2 + 1] = 0.6f;
                }
                float[] mixOut = new float[480 * 2];
                for (int b = 0; b < 4; b++)
                {
                    browser.Buffer.Write(browserTone);
                    bus.Read(mixOut, 0, mixOut.Length);
                }

                // Check monitor buffer
                float[] monOut = new float[480 * 2];
                bus.MonitorBuffer.Read(monOut);
                float monPeak = 0f;
                for (int i = 0; i < monOut.Length; i++)
                {
                    if (Math.Abs(monOut[i]) > monPeak) monPeak = Math.Abs(monOut[i]);
                }

                Assert("Monitor Routes Music in Music Mode", monPeak > 0.01f, $"Peak: {monPeak:F3}");

                // Switch to Mic only mode
                bus.MonitorSourceMode = 1;
                Assert("Monitor Source Mode Switched to Mic", bus.MonitorSourceMode == 1);

                // Switch to Both mode
                bus.MonitorSourceMode = 2;
                Assert("Monitor Source Mode Switched to Both", bus.MonitorSourceMode == 2);
            }
            catch (Exception ex)
            {
                Assert("Monitor Source Routing Exception", false, ex.Message);
            }

            // Test 8: VoiceEffectProcessor Zero-Latency & DSP Verification
            try
            {
                // 8a. Mode 0 (Off): exact bit-for-bit passthrough
                var fxOff = new VoiceEffectProcessor();
                float[] passthroughSrc = new float[] { 0.1234567f, -0.9876543f, 0.0001234f, -0.5f, 0.777777f, -0.00001f };
                float[] passthroughBuf = (float[])passthroughSrc.Clone();
                fxOff.Process(passthroughBuf);
                bool bitExact = true;
                for (int i = 0; i < passthroughBuf.Length; i++)
                {
                    if (BitConverter.SingleToInt32Bits(passthroughBuf[i]) != BitConverter.SingleToInt32Bits(passthroughSrc[i]))
                    {
                        bitExact = false;
                        break;
                    }
                }
                Assert("VoiceEffect Mode 0 Bit-Exact Passthrough", bitExact);

                // 8b. Zero dry latency check across modes 1..5 after blend settles
                bool allZeroDryLatency = true;
                string latencyDetails = "";
                for (int mode = 1; mode <= 5; mode++)
                {
                    var fxLat = new VoiceEffectProcessor { Mode = mode, Intensity = 0.75f };
                    float[] silenceSettle = new float[1440 * 2]; // 30ms settle -> blend == 1.0
                    fxLat.Process(silenceSettle);

                    float[] impulseBlock = new float[480 * 2];
                    impulseBlock[0] = 1.0f;
                    impulseBlock[1] = 1.0f;
                    fxLat.Process(impulseBlock);

                    if (!float.IsFinite(impulseBlock[0]) || !float.IsFinite(impulseBlock[1]) ||
                        Math.Abs(impulseBlock[0]) <= 0.1f || Math.Abs(impulseBlock[1]) <= 0.1f)
                    {
                        allZeroDryLatency = false;
                        latencyDetails += $"M{mode}=({impulseBlock[0]:F4},{impulseBlock[1]:F4}) ";
                    }
                }
                // Also verify Telephone (3) IIR direct feedthrough at 100% wet (0-sample lookahead)
                var fxTeleWet = new VoiceEffectProcessor { Mode = 3, Intensity = 1.0f };
                fxTeleWet.Process(new float[1440 * 2]);
                float[] teleImp = new float[480 * 2];
                teleImp[0] = 1.0f;
                teleImp[1] = 1.0f;
                fxTeleWet.Process(teleImp);
                bool teleZeroWetDelay = float.IsFinite(teleImp[0]) && Math.Abs(teleImp[0]) > 0.0005f;
                Assert("VoiceEffect Zero Dry Latency (Modes 1..5)", allZeroDryLatency && teleZeroWetDelay,
                    string.IsNullOrEmpty(latencyDetails) ? $"TeleWetFrame0={teleImp[0]:F5}" : latencyDetails);

                // 8c. Numerical stability: mixed-frequency 1.5f amplitude sweep + live preset switching (0..5)
                var fxSweep = new VoiceEffectProcessor();
                int[] presetSequence = new int[] { 0, 1, 2, 3, 4, 5, 2, 4, 1, 5, 3, 0 };
                float[] intensitySequence = new float[] { 0.65f, 1.0f, 0.85f, 1.0f, 0.80f, 0.90f, 0.50f, 1.0f, 0.75f, 0.95f, 0.70f, 0.0f };
                bool sweepFiniteAndBounded = true;
                bool activeSoftProtectBounded = true;
                float maxObserved = 0f;
                int sweepCursor = 0;

                for (int step = 0; step < presetSequence.Length; step++)
                {
                    fxSweep.Mode = presetSequence[step];
                    fxSweep.Intensity = intensitySequence[step];
                    for (int b = 0; b < 5; b++) // 50ms per preset
                    {
                        float[] block = new float[480 * 2];
                        for (int i = 0; i < 480; i++)
                        {
                            double t = sweepCursor / 48000.0;
                            float s = (0.45f * (float)Math.Sin(2.0 * Math.PI * 55.0 * t)) +
                                      (0.45f * (float)Math.Sin(2.0 * Math.PI * 330.0 * t)) +
                                      (0.35f * (float)Math.Sin(2.0 * Math.PI * 1450.0 * t)) +
                                      (0.25f * (float)Math.Sin(2.0 * Math.PI * 6500.0 * t));
                            block[i * 2] = s;
                            block[i * 2 + 1] = -s;
                            sweepCursor++;
                        }
                        fxSweep.Process(block);

                        for (int k = 0; k < block.Length; k++)
                        {
                            float val = block[k];
                            if (!float.IsFinite(val) || Math.Abs(val) > 1.55f)
                            {
                                sweepFiniteAndBounded = false;
                            }
                            if (presetSequence[step] != 0 && b >= 3 && (!float.IsFinite(val) || Math.Abs(val) > 1.26f))
                            {
                                activeSoftProtectBounded = false;
                            }
                            if (float.IsFinite(val) && Math.Abs(val) > maxObserved)
                            {
                                maxObserved = Math.Abs(val);
                            }
                        }
                    }
                }
                Assert("VoiceEffect Sweep Stability & Bounds (Modes 0..5)", sweepFiniteAndBounded && activeSoftProtectBounded, $"MaxAmp={maxObserved:F4}");

                // 8d. RoomIsolate (Mode 1) DSP: 50Hz rumble attenuation (>60%) vs 1kHz speech + downward expander
                float MeasureRoomIsolateRms(double freqHz, float amplitude, float intensity, int settleFrames, int measureFrames, out float inputRms)
                {
                    var fx = new VoiceEffectProcessor { Mode = 1, Intensity = intensity };
                    float[] settle = new float[settleFrames * 2];
                    FillSineStereo(settle, freqHz, amplitude, 0);
                    fx.Process(settle);

                    float[] measure = new float[measureFrames * 2];
                    FillSineStereo(measure, freqHz, amplitude, settleFrames);
                    inputRms = ComputeRms(measure);
                    fx.Process(measure);
                    return ComputeRms(measure);
                }

                float rms50Out = MeasureRoomIsolateRms(50.0, 0.25f, 0.85f, 4800, 4800, out float rms50In);
                float rms1kOut = MeasureRoomIsolateRms(1000.0, 0.25f, 0.85f, 4800, 4800, out float rms1kIn);
                float rumbleReduction = 1.0f - (rms50Out / rms50In);
                Assert("RoomIsolate 50Hz Rumble Attenuation (>60%) vs 1kHz",
                    rumbleReduction > 0.60f && rms1kOut > rms1kIn * 0.70f && rms50Out < rms1kOut * 0.35f,
                    $"50HzRed={rumbleReduction * 100f:F1}% Out50={rms50Out:F4} Out1k={rms1kOut:F4}");

                float lowAmp = MathF.Pow(10f, -55f / 20f);
                float voiceAmp = MathF.Pow(10f, -12f / 20f);
                float rmsLowOut = MeasureRoomIsolateRms(1000.0, lowAmp, 1.0f, 9600, 4800, out float rmsLowIn);
                float rmsVoiceOut = MeasureRoomIsolateRms(1000.0, voiceAmp, 1.0f, 4800, 4800, out float rmsVoiceIn);
                Assert("RoomIsolate Downward Expander (-55 dBFS vs -12 dBFS)",
                    (rmsLowOut / rmsLowIn) < 0.25f && (rmsVoiceOut / rmsVoiceIn) > 0.80f,
                    $"LowRatio={rmsLowOut / rmsLowIn:F3} VoiceRatio={rmsVoiceOut / rmsVoiceIn:F3}");

                // 8e. Echo (Mode 2) DSP: 10ms burst -> stereo tail at 220ms (L) and 310ms (R)
                var fxEcho = new VoiceEffectProcessor { Mode = 2, Intensity = 0.75f };
                fxEcho.Process(new float[960 * 2]); // settle blend

                float[] echoTimeline = new float[19680 * 2]; // 410ms
                FillSineStereo(echoTimeline.AsSpan(0, 480 * 2), 1000.0, 0.8f, 0);
                fxEcho.Process(echoTimeline);

                ComputeChannelPeak(echoTimeline, 2880, 9600, out float gapPeakL, out float gapPeakR);         // 60ms..200ms
                ComputeChannelPeak(echoTimeline, 10560, 11136, out float echo220PeakL, out float echo220PeakR); // 220ms..232ms
                ComputeChannelPeak(echoTimeline, 14880, 15456, out float echo310PeakL, out float echo310PeakR); // 310ms..322ms

                Assert("Echo Stereo Delayed Tail (220ms L / 310ms R)",
                    gapPeakL < 0.001f && gapPeakR < 0.001f &&
                    echo220PeakL > 0.15f && echo220PeakR < 0.001f &&
                    echo310PeakR > 0.15f,
                    $"Gap=({gapPeakL:F4},{gapPeakR:F4}) @220ms=({echo220PeakL:F3},{echo220PeakR:F4}) @310ms=({echo310PeakL:F4},{echo310PeakR:F3})");

                // 8f. Telephone (Mode 3) DSP: 50Hz bass and 8kHz highs attenuated vs 1350Hz vocal nasal band
                float MeasureTelephoneRms(double freqHz)
                {
                    var fx = new VoiceEffectProcessor { Mode = 3, Intensity = 1.0f };
                    float[] settle = new float[4800 * 2];
                    FillSineStereo(settle, freqHz, 0.15f, 0);
                    fx.Process(settle);

                    float[] measure = new float[4800 * 2];
                    FillSineStereo(measure, freqHz, 0.15f, 4800);
                    fx.Process(measure);
                    return ComputeRms(measure);
                }

                float tele50 = MeasureTelephoneRms(50.0);
                float tele1350 = MeasureTelephoneRms(1350.0);
                float tele8000 = MeasureTelephoneRms(8000.0);
                float teleInRms = 0.15f / MathF.Sqrt(2f);
                Assert("Telephone Bandpass (50Hz & 8kHz Attenuated vs 1350Hz)",
                    tele1350 > teleInRms * 0.80f && tele50 < tele1350 * 0.15f && tele8000 < tele1350 * 0.15f,
                    $"50Hz={tele50:F5} 1350Hz={tele1350:F4} 8kHz={tele8000:F5}");
            }
            catch (Exception ex)
            {
                Assert("VoiceEffectProcessor Test Exception", false, ex.Message);
            }

            // Test 9: AdaptiveJitterBuffer.TrimToMaxBuffered & MonitorSampleProvider Low-Latency Sync
            try
            {
                // 9a. Direct TrimToMaxBuffered(480) on 4800 frames (100ms) keeps newest 480 frames without re-entering preroll
                var largeBuf = new AdaptiveJitterBuffer(48000, 240, 9600);
                float[] frames4800 = new float[4800 * 2];
                for (int i = 0; i < 4800; i++)
                {
                    float val = i < 4320 ? 0.10f : 0.65f;
                    frames4800[i * 2] = val;
                    frames4800[i * 2 + 1] = val;
                }
                largeBuf.Write(frames4800);
                int beforeTrim = largeBuf.BufferedFrames;
                largeBuf.TrimToMaxBuffered(480);
                int afterTrim = largeBuf.BufferedFrames;

                float[] immediateRead = new float[240 * 2];
                largeBuf.Read(immediateRead);
                Assert("AdaptiveJitterBuffer.TrimToMaxBuffered(480) Keeps Newest & Skips Preroll",
                    beforeTrim == 4800 && afterTrim <= 480 && Math.Abs(immediateRead[0] - 0.65f) < 0.0001f,
                    $"Before={beforeTrim} After={afterTrim} Out0={immediateRead[0]:F3}");

                // 9b. Monitor AdaptiveJitterBuffer(48000, 240, 1440) + MonitorSampleProvider.RequestLowLatencySync()
                var monitorBuf = new AdaptiveJitterBuffer(48000, 240, 1440);
                monitorBuf.Write(frames4800);
                int boundedAfter4800Write = monitorBuf.BufferedFrames;
                float[] boundedOut = new float[240 * 2];
                monitorBuf.Read(boundedOut);
                Assert("Monitor JitterBuffer(48000,240,1440) Bounds 4800-Frame Write & Plays Immediately",
                    boundedAfter4800Write <= 480 && Math.Abs(boundedOut[0] - 0.65f) < 0.0001f,
                    $"BoundedFrames={boundedAfter4800Write} Out0={boundedOut[0]:F3}");

                monitorBuf.Clear();
                float[] frames1200 = new float[1200 * 2];
                for (int i = 0; i < 1200; i++)
                {
                    float val = i < 960 ? 0.20f : 0.75f;
                    frames1200[i * 2] = val;
                    frames1200[i * 2 + 1] = val;
                }
                monitorBuf.Write(frames1200);
                var monitorProvider = new MonitorSampleProvider(monitorBuf);
                monitorProvider.RequestLowLatencySync();

                float[] monSyncOut = new float[240 * 2];
                int readSamples = monitorProvider.Read(monSyncOut, 0, monSyncOut.Length);
                Assert("MonitorSampleProvider.RequestLowLatencySync() Trims & Outputs Immediately",
                    readSamples == monSyncOut.Length && monitorBuf.BufferedFrames <= 480 && Math.Abs(monSyncOut[0] - 0.75f) < 0.0001f,
                    $"RemainingFrames={monitorBuf.BufferedFrames} Out0={monSyncOut[0]:F3}");

                // 9c. MonitorSampleProvider automatic drift trim (>1920 frames -> TrimToMaxBuffered(480))
                var driftBuf = new AdaptiveJitterBuffer(48000, 240, 9600);
                float[] driftFrames = new float[4800 * 2];
                for (int i = 0; i < 4800; i++)
                {
                    float val = i < 4320 ? 0.15f : 0.85f;
                    driftFrames[i * 2] = val;
                    driftFrames[i * 2 + 1] = val;
                }
                driftBuf.Write(driftFrames);
                var driftProvider = new MonitorSampleProvider(driftBuf);
                float[] driftOut = new float[240 * 2];
                driftProvider.Read(driftOut, 0, driftOut.Length);
                Assert("MonitorSampleProvider Auto-Trim Above 1920 Frames",
                    driftBuf.BufferedFrames <= 480 && Math.Abs(driftOut[0] - 0.85f) < 0.0001f,
                    $"RemainingFrames={driftBuf.BufferedFrames} Out0={driftOut[0]:F3}");
            }
            catch (Exception ex)
            {
                Assert("Monitor Sync Test Exception", false, ex.Message);
            }

            // Test 10: SettingsManager Shared Settings Verification (with byte-exact AppData restore)
            try
            {
                string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string officialDir = Path.Combine(appDataDir, "MixerOnline");
                string officialPath = Path.Combine(officialDir, "settings.json");
                string betaDir = Path.Combine(appDataDir, "MixerOnline-beta");
                string betaExtrasPath = Path.Combine(betaDir, "beta-settings.json");

                bool officialExisted = File.Exists(officialPath);
                byte[]? officialBackup = officialExisted ? File.ReadAllBytes(officialPath) : null;
                bool betaDirExisted = Directory.Exists(betaDir);
                bool betaExisted = File.Exists(betaExtrasPath);
                byte[]? betaBackup = betaExisted ? File.ReadAllBytes(betaExtrasPath) : null;

                int expectedMicDelay = 11;
                int expectedLatency = 30;
                if (officialBackup != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(officialBackup);
                        if (doc.RootElement.TryGetProperty("MicDelayMs", out var delayEl) && delayEl.TryGetInt32(out int d))
                        {
                            expectedMicDelay = Math.Clamp(d, 0, 300);
                        }
                        if (doc.RootElement.TryGetProperty("LatencyMs", out var latEl) && latEl.TryGetInt32(out int l))
                        {
                            expectedLatency = Math.Clamp(l, 15, 200);
                        }
                    }
                    catch { }
                }

                try
                {
                    var loaded = SettingsManager.Load();
                    Assert("SettingsManager.Load() Preserves MicDelayMs & LatencyMs",
                        loaded != null && loaded.MicDelayMs == expectedMicDelay && loaded.LatencyMs == expectedLatency,
                        $"MicDelayMs={loaded?.MicDelayMs} (expected {expectedMicDelay}) LatencyMs={loaded?.LatencyMs} (expected {expectedLatency})");

                    var roundTrip = new AppSettings
                    {
                        SelectedMicId = loaded!.SelectedMicId,
                        SelectedOutputId = loaded.SelectedOutputId,
                        SelectedMonitorId = loaded.SelectedMonitorId,
                        MicVolume = loaded.MicVolume,
                        IsMicMuted = loaded.IsMicMuted,
                        MicDelayMs = loaded.MicDelayMs,
                        BrowserVolume = loaded.BrowserVolume,
                        IsBrowserMuted = loaded.IsBrowserMuted,
                        BrowserKeySemitones = loaded.BrowserKeySemitones,
                        MasterVolume = loaded.MasterVolume,
                        IsMasterMuted = loaded.IsMasterMuted,
                        IsMonitoringEnabled = false,
                        MonitorVolume = loaded.MonitorVolume,
                        LatencyMs = loaded.LatencyMs,
                        BridgePort = loaded.BridgePort,
                        MinimizeToTray = loaded.MinimizeToTray,
                        AutoReconnect = loaded.AutoReconnect,
                        HearTabInBrowser = loaded.HearTabInBrowser,
                        VoiceEffectMode = 4,
                        VoiceEffectIntensity = 0.82f,
                        MonitorSourceMode = 2
                    };

                    SettingsManager.Save(roundTrip);
                    var reloaded = SettingsManager.Load();
                    Assert("SettingsManager Stores & Loads Voice FX + MonitorSourceMode",
                        reloaded.VoiceEffectMode == 4 &&
                        Math.Abs(reloaded.VoiceEffectIntensity - 0.82f) < 0.001f &&
                        reloaded.MonitorSourceMode == 2 &&
                        reloaded.MicDelayMs == expectedMicDelay &&
                        reloaded.LatencyMs == expectedLatency,
                        $"FX=({reloaded.VoiceEffectMode}, {reloaded.VoiceEffectIntensity:F2}) Mon={reloaded.MonitorSourceMode}");

                    roundTrip.VoiceEffectMode = 99;
                    roundTrip.VoiceEffectIntensity = float.NaN;
                    roundTrip.MonitorSourceMode = -3;
                    SettingsManager.Save(roundTrip);
                    var sanitized = SettingsManager.Load();
                    Assert("SettingsManager Sanitizes Out-of-Range Voice FX & MonitorSourceMode",
                        sanitized.VoiceEffectMode == 5 &&
                        Math.Abs(sanitized.VoiceEffectIntensity - 0.75f) < 0.001f &&
                        sanitized.MonitorSourceMode == 0,
                        $"FX=({sanitized.VoiceEffectMode}, {sanitized.VoiceEffectIntensity:F2}) Mon={sanitized.MonitorSourceMode}");
                }
                finally
                {
                    if (officialExisted && officialBackup != null)
                    {
                        string tmp = Path.Combine(officialDir, $"settings.restore.{Guid.NewGuid():N}.tmp");
                        File.WriteAllBytes(tmp, officialBackup);
                        File.Move(tmp, officialPath, overwrite: true);
                    }
                    else if (!officialExisted && File.Exists(officialPath))
                    {
                        File.Delete(officialPath);
                    }

                    if (betaExisted && betaBackup != null)
                    {
                        string tmp = Path.Combine(betaDir, $"beta-settings.restore.{Guid.NewGuid():N}.tmp");
                        File.WriteAllBytes(tmp, betaBackup);
                        File.Move(tmp, betaExtrasPath, overwrite: true);
                    }
                    else if (!betaExisted && File.Exists(betaExtrasPath))
                    {
                        File.Delete(betaExtrasPath);
                        if (!betaDirExisted && Directory.Exists(betaDir) && Directory.GetFileSystemEntries(betaDir).Length == 0)
                        {
                            Directory.Delete(betaDir);
                        }
                    }
                }

                bool officialRestoredExact = officialExisted
                    ? (File.Exists(officialPath) && officialBackup != null && File.ReadAllBytes(officialPath).AsSpan().SequenceEqual(officialBackup))
                    : !File.Exists(officialPath);
                bool betaRestoredExact = betaExisted
                    ? (File.Exists(betaExtrasPath) && betaBackup != null && File.ReadAllBytes(betaExtrasPath).AsSpan().SequenceEqual(betaBackup))
                    : !File.Exists(betaExtrasPath);
                Assert("SettingsManager Test Restored Original AppData Bytes", officialRestoredExact && betaRestoredExact);
            }
            catch (Exception ex)
            {
                Assert("SettingsManager Exception", false, ex.Message);
            }

            // Test 11: AudioMixerBus + AudioEngine Integration with Voice FX
            try
            {
                using var engine = new AudioEngine(FindAvailablePort(39350), 20);
                engine.MicrophoneCapture.OutputBuffer.ConfigureCushion(480, 9600);

                // Prime buffer cleanly out of preroll (1:1 write/read of 480 frames stays unlocked)
                engine.MicrophoneCapture.OutputBuffer.Write(new float[480 * 2]);
                float[] primeOut = new float[480 * 2];
                engine.MixerBus.Read(primeOut, 0, primeOut.Length);

                engine.VoiceEffectIntensity = 0.8f;
                Assert("AudioEngine Forwards VoiceEffectIntensity to MixerBus",
                    Math.Abs(engine.VoiceEffectIntensity - 0.8f) < 0.0001f &&
                    Math.Abs(engine.MixerBus.VoiceEffectIntensity - 0.8f) < 0.0001f);

                bool allModesFiniteAndBounded = true;

                float RunBusToneRms(int mode, double freqHz, float amp, int settleBlocks, int measureBlocks)
                {
                    engine.VoiceEffectMode = mode;
                    int frameCursor = 0;
                    float[] inBuf = new float[480 * 2];
                    float[] outBuf = new float[480 * 2];

                    for (int b = 0; b < settleBlocks; b++)
                    {
                        FillSineStereo(inBuf, freqHz, amp, frameCursor);
                        frameCursor += 480;
                        engine.MicrophoneCapture.OutputBuffer.Write(inBuf);
                        engine.MixerBus.Read(outBuf, 0, outBuf.Length);
                        if (!AllFiniteBounded(outBuf, 1.0f)) allModesFiniteAndBounded = false;
                    }

                    double sumSq = 0;
                    long totalSamples = 0;
                    for (int b = 0; b < measureBlocks; b++)
                    {
                        FillSineStereo(inBuf, freqHz, amp, frameCursor);
                        frameCursor += 480;
                        engine.MicrophoneCapture.OutputBuffer.Write(inBuf);
                        engine.MixerBus.Read(outBuf, 0, outBuf.Length);
                        if (!AllFiniteBounded(outBuf, 1.0f)) allModesFiniteAndBounded = false;
                        for (int i = 0; i < outBuf.Length; i++)
                        {
                            sumSq += outBuf[i] * outBuf[i];
                        }
                        totalSamples += outBuf.Length;
                    }
                    return (float)Math.Sqrt(sumSq / totalSamples);
                }

                // Mode 1 (RoomIsolate): 50Hz attenuated vs 1kHz
                float busIso50 = RunBusToneRms(1, 50.0, 0.25f, 10, 10);
                float busIso1k = RunBusToneRms(1, 1000.0, 0.25f, 10, 10);
                Assert("AudioEngine + MixerBus Mode 1 (RoomIsolate)",
                    engine.MixerBus.VoiceEffectMode == 1 && busIso50 < busIso1k * 0.45f,
                    $"50Hz={busIso50:F4} 1kHz={busIso1k:F4}");

                // Mode 2 (Echo): 10ms burst + silence -> delayed stereo echoes at ~220ms L / ~310ms R
                engine.VoiceEffectMode = 2;
                float[] busEchoIn = new float[480 * 2];
                float[] busEchoOut = new float[480 * 2];
                for (int b = 0; b < 3; b++) // 30ms silence settle
                {
                    Array.Clear(busEchoIn, 0, busEchoIn.Length);
                    engine.MicrophoneCapture.OutputBuffer.Write(busEchoIn);
                    engine.MixerBus.Read(busEchoOut, 0, busEchoOut.Length);
                    if (!AllFiniteBounded(busEchoOut, 1.0f)) allModesFiniteAndBounded = false;
                }
                float[] echoTimeline = new float[36 * 480 * 2]; // 360ms
                for (int b = 0; b < 36; b++)
                {
                    if (b == 0) FillSineStereo(busEchoIn, 1000.0, 0.6f, 0);
                    else Array.Clear(busEchoIn, 0, busEchoIn.Length);
                    engine.MicrophoneCapture.OutputBuffer.Write(busEchoIn);
                    engine.MixerBus.Read(busEchoOut, 0, busEchoOut.Length);
                    if (!AllFiniteBounded(busEchoOut, 1.0f)) allModesFiniteAndBounded = false;
                    Array.Copy(busEchoOut, 0, echoTimeline, b * 960, 960);
                }
                ComputeChannelPeak(echoTimeline, 2880, 9600, out float busGapL, out float busGapR);
                ComputeChannelPeak(echoTimeline, 10560, 11136, out float bus220L, out float bus220R);
                ComputeChannelPeak(echoTimeline, 14880, 15456, out float bus310L, out float bus310R);
                Assert("AudioEngine + MixerBus Mode 2 (Echo)",
                    engine.MixerBus.VoiceEffectMode == 2 &&
                    busGapL < 0.002f && busGapR < 0.002f &&
                    bus220L > 0.10f && bus220R < 0.002f &&
                    bus310R > 0.10f,
                    $"Gap=({busGapL:F4},{busGapR:F4}) @220ms=({bus220L:F3},{bus220R:F4}) @310ms=({bus310L:F4},{bus310R:F3})");

                // Mode 3 (Telephone): 50Hz attenuated vs 1350Hz
                float busTele50 = RunBusToneRms(3, 50.0, 0.20f, 10, 10);
                float busTele1350 = RunBusToneRms(3, 1350.0, 0.20f, 10, 10);
                Assert("AudioEngine + MixerBus Mode 3 (Telephone)",
                    engine.MixerBus.VoiceEffectMode == 3 && busTele50 < busTele1350 * 0.45f,
                    $"50Hz={busTele50:F4} 1350Hz={busTele1350:F4}");

                // Mode 4 (Radio): warm broadcast compression + limiter bound
                float busRadio1k = RunBusToneRms(4, 1000.0, 0.15f, 10, 10);
                Assert("AudioEngine + MixerBus Mode 4 (Radio)",
                    engine.MixerBus.VoiceEffectMode == 4 && busRadio1k > 0.08f,
                    $"1kHzRms={busRadio1k:F4}");

                // Mode 5 (HallReverb): 20ms burst + silence -> audible reverb tail in silence blocks
                engine.VoiceEffectMode = 5;
                float[] busRevIn = new float[480 * 2];
                float[] busRevOut = new float[480 * 2];
                for (int b = 0; b < 3; b++)
                {
                    Array.Clear(busRevIn, 0, busRevIn.Length);
                    engine.MicrophoneCapture.OutputBuffer.Write(busRevIn);
                    engine.MixerBus.Read(busRevOut, 0, busRevOut.Length);
                    if (!AllFiniteBounded(busRevOut, 1.0f)) allModesFiniteAndBounded = false;
                }
                for (int b = 0; b < 2; b++) // 20ms burst
                {
                    FillSineStereo(busRevIn, 1000.0, 0.6f, b * 480);
                    engine.MicrophoneCapture.OutputBuffer.Write(busRevIn);
                    engine.MixerBus.Read(busRevOut, 0, busRevOut.Length);
                    if (!AllFiniteBounded(busRevOut, 1.0f)) allModesFiniteAndBounded = false;
                }
                double tailSumSq = 0;
                long tailSamples = 0;
                for (int b = 0; b < 12; b++) // 120ms silence tail
                {
                    Array.Clear(busRevIn, 0, busRevIn.Length);
                    engine.MicrophoneCapture.OutputBuffer.Write(busRevIn);
                    engine.MixerBus.Read(busRevOut, 0, busRevOut.Length);
                    if (!AllFiniteBounded(busRevOut, 1.0f)) allModesFiniteAndBounded = false;
                    if (b >= 3) // measure 30ms..120ms after burst (well after dry input ended)
                    {
                        for (int i = 0; i < busRevOut.Length; i++)
                        {
                            tailSumSq += busRevOut[i] * busRevOut[i];
                        }
                        tailSamples += busRevOut.Length;
                    }
                }
                float busRevTailRms = (float)Math.Sqrt(tailSumSq / tailSamples);
                Assert("AudioEngine + MixerBus Mode 5 (HallReverb Tail)",
                    engine.MixerBus.VoiceEffectMode == 5 && busRevTailRms > 0.005f,
                    $"TailRms={busRevTailRms:F4}");

                Assert("AudioEngine + MixerBus All Modes Finite & Limiter-Bounded", allModesFiniteAndBounded);
            }
            catch (Exception ex)
            {
                Assert("AudioEngine VoiceFX Integration Exception", false, ex.Message);
            }

            Console.WriteLine("=================================================");
            Console.WriteLine($"Tests Completed: {passed + failed} | Passed: {passed} | Failed: {failed} | Skipped: {skipped}");
            Console.WriteLine("=================================================");

            Environment.Exit(failed == 0 ? 0 : 1);
        }

        private static void FillSineStereo(Span<float> dest, double freqHz, float amplitude, int startFrame)
        {
            int frames = dest.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                float s = (float)Math.Sin(2.0 * Math.PI * freqHz * ((startFrame + i) / 48000.0)) * amplitude;
                dest[i * 2] = s;
                dest[i * 2 + 1] = s;
            }
        }

        private static float ComputeRms(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return 0f;
            double sumSq = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sumSq += samples[i] * samples[i];
            }
            return (float)Math.Sqrt(sumSq / samples.Length);
        }

        private static void ComputeChannelPeak(ReadOnlySpan<float> samples, int startFrame, int endFrame, out float peakL, out float peakR)
        {
            peakL = 0f;
            peakR = 0f;
            int maxFrame = Math.Min(endFrame, samples.Length / 2);
            for (int f = Math.Max(0, startFrame); f < maxFrame; f++)
            {
                float absL = Math.Abs(samples[f * 2]);
                float absR = Math.Abs(samples[f * 2 + 1]);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
            }
        }

        private static bool AllFiniteBounded(ReadOnlySpan<float> samples, float maxAbs)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float s = samples[i];
                if (!float.IsFinite(s) || Math.Abs(s) > maxAbs)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
