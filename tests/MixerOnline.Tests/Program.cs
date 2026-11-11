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

                        // Verify WebSocket Origin Guard blocks external web pages
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

            Console.WriteLine("=================================================");
            Console.WriteLine($"Tests Completed: {passed + failed} | Passed: {passed} | Failed: {failed} | Skipped: {skipped}");
            Console.WriteLine("=================================================");

            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
