# MixerOnline — Architectural Specification

MixerOnline is a Windows desktop audio mixing console built with **.NET 8 WPF** paired with a **Chromium Manifest V3 Audio Bridge** extension. All internal audio processing runs at **48,000 Hz, 32-bit IEEE float, stereo**.

```text
+-------------------------------------------------------------------+
|               Chromium Browser (Chrome/Brave/Edge/Opera/Vivaldi)  |
|  [Selected Tab] -> [chrome.tabCapture] -> [AudioWorklet 48kHz]    |
|                          | 128-frame Float32 PCM (2.67 ms)        |
|                          v                                        |
|           [offscreen.js WebSocket ws://127.0.0.1:<port>/ws]       |
+--------------------------+----------------------------------------+
                           | Loopback TCP + Origin Validation
+--------------------------v----------------------------------------+
|  MixerOnline.exe         |                                        |
|  +-----------------------+-------+   +-------------------------+  |
|  | BrowserBridgeServer           |   | MicrophoneCapture       |  |
|  | - HttpListener (127.0.0.1)    |   | - WASAPI Shared Event   |  |
|  | - Origin guard + /status      |   | - 16/24/32 PCM & Float  |  |
|  +---------------+---------------+   | - Downward Expander     |  |
|                  |                   +------------+------------+  |
|                  v                                |               |
|  +-------------------------------+                v               |
|  | BrowserAudioReceiver          |   [AdaptiveJitterBuffer]       |
|  | - AdaptiveJitterBuffer        |                |               |
|  | - Cubic Hermite Resampler     |                v               |
|  +---------------+---------------+   [DelayLine (0-300 ms)]       |
|                  |                                |               |
|                  v                                |               |
|  [PitchShifter (-6..+6 semitones)]                |               |
|                  |                                |               |
|                  +---------------+----------------+               |
|                                  v                                |
|  +-------------------------------------------------------------+  |
|  | AudioMixerBus                                               |  |
|  | - Exponential gain smoothing (0.015f per frame)             |  |
|  | - AudioLimiter (0.94f tanh soft-knee, 0.999f ceiling)       |  |
|  | - Peak / RMS metering + Monitor AdaptiveJitterBuffer        |  |
|  +-----------------------+---------------------+---------------+  |
|                          |                     |                  |
|                          v                     v                  |
|                 [AudioOutputRouter]   [MonitorSampleProvider]     |
|                 WASAPI Shared Event   WASAPI Shared Event         |
+--------------------------+---------------------+------------------+
                           |                     |
                           v                     v
          CABLE Input (VB-Audio Cable)      Headphones
                           |
                           v
      Discord / OBS / Zoom / TikTok Live Studio
      (Input: CABLE Output - VB-Audio Virtual Cable)
```

---

## Subsystems & Module Responsibilities

### 1. Desktop UI & ViewModel (`MainWindow.xaml`, `MainViewModel.cs`)
- Frameless matte carbon WPF window (`960x640`) with three channel strips (`01 Browser Audio`, `02 Microphone`, `03 Master Output`), custom `ConsoleFader` sliders, and `DualStereoLedMeter` displays.
- `MainViewModel` updates Peak and RMS meter properties on a `33 ms` (~30 FPS) `DispatcherTimer` (`DispatcherPriority.Render`) and persists user adjustments through `SettingsManager`.
- Supports minimizing/closing to the Windows system tray via `NotifyIcon` (`MinimizeToTray`) with quick mute and exit actions.

### 2. Chromium Extension (`extension/`)
- **Manifest V3 (`manifest.json` v2.2.0):** Requests `tabCapture`, `tabs`, `offscreen`, and `storage` permissions plus loopback host permissions (`127.0.0.1`, `localhost`).
- **Service Worker (`background.js`):** Maintains the `offscreen.html` document, filters out internal browser schemes (`chrome://`, `brave://`, `edge://`, `opera://`, `vivaldi://`, `arc://`, `devtools://`, `about:`), acquires tab media stream IDs via `chrome.tabCapture.getMediaStreamId`, and broadcasts tab changes.
- **Audio Capture Host (`offscreen.js` & `pcm-processor.js`):**
  - Calls `navigator.mediaDevices.getUserMedia` with WebRTC APM flags explicitly disabled (`echoCancellation: false`, `googEchoCancellation: false`, `googAutoGainControl: false`, `googNoiseSuppression: false`, `googHighpassFilter: false`) to preserve full 20 Hz–20 kHz stereo fidelity.
  - Runs a `48000 Hz` `AudioContext` feeding `PcmProcessor` (`AudioWorkletProcessor`), dispatching 128-frame (`2.67 ms`) interleaved Float32 stereo packets over WebSocket (with a `ScriptProcessorNode` fallback).
  - Persists custom `bridgePort` (`1025–65534`, default `49876`) in `chrome.storage.local` and exposes `passThroughGainNode` for click-free local tab playback toggling.
- **Toolbar Popup (`popup.html` & `popup.js`):** Shows live mixer connection status, active capture state, capturable tabs, local playback toggle, and inline **Bridge Port** configuration.

### 3. Chromium Browser Launcher (`Core/Utils/BrowserLauncher.cs`)
- `FindExtensionFolder()` locates the unpacked `extension` folder relative to the executable in both published `dist/` layouts and multi-level `bin/` development paths.
- `OpenExtensionsPage()` launches the appropriate Chromium extensions URL (`brave://extensions`, `chrome://extensions`, `edge://extensions`, `vivaldi://extensions`, or `opera://extensions`) using a 4-stage resolution strategy:
  1. Running Chromium processes with a visible window (`Brave`, `Chrome`, `Edge`, `Vivaldi`, `Opera`).
  2. Current Windows default `https`/`http` browser ProgId from `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations`.
  3. Any background running Chromium process.
  4. Standard `Program Files`, `Program Files (x86)`, and `LocalApplicationData` installation paths.

### 4. Browser Communication Bridge (`Core/Network/BrowserBridgeServer.cs`, `ProtocolMessages.cs`)
- Binds an `HttpListener` strictly to `http://127.0.0.1:{_port}/` (default `49876`).
- **Health Endpoint:** `GET /status` returns JSON (`app`, `status`, `port`, `connected`) with `Cache-Control: no-store`.
- **Origin Guard (`IsAllowedWebSocketOrigin`):** Inspects the HTTP `Origin` header on WebSocket upgrades and rejects unauthorized origins (such as arbitrary websites attempting Cross-Site WebSocket Hijacking) with `403 Forbidden`. Only browser extension origins (`chrome-extension://`, `extension://`, `moz-extension://`) and native/test loopback clients that omit `Origin` are accepted.
- Uses `ArrayPool<byte>.Shared` for low-allocation reception of binary Float32 stereo frames and JSON RPC messages (`HELLO`, `GET_TABS`, `TAB_LIST`, `SELECT_TAB`, `CAPTURE_STARTED`, `CAPTURE_STOPPED`, `STOP_CAPTURE`, `SET_BROWSER_PASSTHROUGH`, `TAB_CLOSED`, `TAB_UPDATED`, `ERROR`).

### 5. Browser Audio Receiver & Jitter Buffer (`BrowserAudioReceiver.cs`, `AdaptiveJitterBuffer.cs`)
- `AdaptiveJitterBuffer` implements a lock-protected circular stereo buffer (`48,000` frames default capacity) with configurable preroll (`Math.Max(960, latencyMs * 48 * 2)` frames) and maximum backlog (`Math.Max(4800, preroll * 3)` frames). On underrun, it applies a 32-frame micro-fade and cleanly re-enters preroll instead of looping stale audio.
- `BrowserAudioReceiver` passes `48,000 Hz` streams through 1:1 without resampling, and uses a stateful 4-point Cubic Hermite interpolator whenever input rate differs (for example `44.1 kHz -> 48.0 kHz`, selectable via `SampleRateMode`).

### 6. Microphone Capture (`MicrophoneCapture.cs`)
- Captures audio via `WasapiCapture` in shared event-driven mode (`16 ms` period).
- Normalizes 16-bit PCM, 24-bit PCM, 32-bit PCM, and 32-bit IEEE float across mono, stereo, and multichannel inputs into `48,000 Hz` stereo float (using Cubic Hermite resampling when hardware rate is not `48 kHz`).
- Applies a gentle studio downward expander (`GateThreshold = 0.0015f`, ~`-56 dBFS`) before writing to its `AdaptiveJitterBuffer`.

### 7. DSP Processing Chain (`AudioMixerBus.cs`, `PitchShifter.cs`, `DelayLine.cs`, `AudioLimiter.cs`)
Inside `AudioMixerBus.Read`:
1. **Mic Delay (`DelayLine.cs`):** Reads mic audio and applies sample-accurate delay (`0–300 ms` clamped, `0–200 ms` UI slider, `32,768`-frame circular buffer = ~682.6 ms). Uses zero-overhead passthrough when `0 ms` and slews delay changes by at most `0.25` frame per frame to prevent zipper artifacts.
2. **Browser Key Transpose (`PitchShifter.cs`):** Processes browser audio through a dual-grain synchronous overlap-add pitch shifter (`-6` to `+6` semitones) with a `16,384`-frame circular buffer, `2,400`-frame (`50 ms`) grains, `4,096`-sample precomputed Hann window lookup table, linear tap interpolation, and `0.002f` per-frame pitch-ratio slewing. Bypasses with zero processing overhead when `Semitones == 0`.
3. **Gain Smoothing & Summing:** Interpolates Mic, Browser, and Master target gains exponentially per frame (`SmoothingCoeff = 0.015f`) and sums the channels.
4. **Soft-Knee Limiter (`AudioLimiter.cs`):** Tracks stereo peak envelope (`0.8 ms` attack / `80 ms` release), passes signals untouched 1:1 below `0.94f` (`-0.5 dBFS`), and smoothly saturates peaks via `tanh` below `0.999f` (`-0.001 dBFS`).
5. **Monitor Feed:** Writes the limited master mix into `MonitorBuffer` (`AdaptiveJitterBuffer`, `576`-frame initial preroll, `7,200`-frame cap).

### 8. Output Routing & Device Management (`AudioOutputRouter.cs`, `MonitorSampleProvider.cs`, `DeviceManager.cs`)
- `AudioOutputRouter` drives the primary `WasapiOut` (`AudioClientShareMode.Shared`, `useEventSync: true`, `10–200 ms` clamped latency) targeting `CABLE Input (VB-Audio Virtual Cable)` and an optional secondary `WasapiOut` fed by `MonitorSampleProvider` for headphones.
- `DeviceManager` registers an `IMMNotificationClient` via `MMDeviceEnumerator` to detect device hot-plugs, default changes, and VB-Cable availability (`VB-Audio` / `CABLE` / `Virtual Cable`). `AudioEngine` automatically reconnects previously selected capture or render devices when they return active.

### 9. Configuration & Persistence (`AppSettings.cs`, `SettingsManager.cs`)
- Persists devices, volumes, mute states, `MicDelayMs` (default `45`), `BrowserKeySemitones`, `LatencyMs` (default `30`), `BridgePort` (default `49876`), `MinimizeToTray`, `AutoReconnect`, and `HearTabInBrowser` to `%APPDATA%\MixerOnline\settings.json`.
- On startup, sanitizes legacy gain ranges and initializes `IsMonitoringEnabled = false` to prevent unexpected slapback echo.

---

## Automated Verification Suite (`tests/MixerOnline.Tests/Program.cs`)

Running `.\scripts\build.ps1 -Configuration Release` builds the desktop app and runs the automated DSP and integration test suite covering:
- `RingBuffer` FIFO ordering and overflow boundary protection
- `AudioLimiter` 1:1 linear region and `<= 0.999f` positive/negative ceiling enforcement
- `DelayLine` 0 ms passthrough and exact sample-accurate impulse delay
- `PitchShifter` 0-semitone bypass, positive/negative semitone clamping, and numerical stability
- `DeviceManager` WASAPI capture/render enumeration and VB-Audio Virtual Cable detection
- `BrowserBridgeServer` HTTP `/status` health endpoint, WebSocket handshake, JSON control frames, and binary Float32 metering
- Bit-exact 48 kHz continuity verification across interleaved `128`-frame browser packets and `1440`-frame WASAPI reads
- End-to-end `AudioEngine` initialization, bus format validation, and clean disposal
