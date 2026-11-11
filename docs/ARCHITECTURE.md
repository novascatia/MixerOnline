# MixerOnline Beta — Architectural Specification

MixerOnline Beta (`v1.1.0-beta`) extends the **48,000 Hz, 32-bit IEEE float, stereo** WPF mixing engine and **Chromium Manifest V3 Audio Bridge** with a **zero-latency-dry Voice FX** DSP chain, a **configurable drift-trimmed Cue Loopback** monitor bus, and **shared/beta settings coexistence**.

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
|  MixerOnline-beta.exe    |                                        |
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
|  +---------------+---------------+   [VoiceEffectProcessor]       |
|                  |                   (0-sample dry lookahead,     |
|                  v                    6 presets + 10ms crossfade) |
|  [PitchShifter (-6..+6 semitones)]                |               |
|                  |                                v               |
|                  |                   [DelayLine (0-300 ms)]       |
|                  +---------------+----------------+               |
|                                  v                                |
|  +-------------------------------------------------------------+  |
|  | AudioMixerBus                                               |  |
|  | - Master mix -> AudioLimiter -> Primary Out (VB-Cable)      |  |
|  | - MonitorSourceMode (Music | Mic | Both) -> _monitorLimiter |  |
|  |   -> Monitor AdaptiveJitterBuffer (240 preroll / 1440 max)  |  |
|  +-----------------------+---------------------+---------------+  |
|                          |                     |                  |
|                          v                     v                  |
|                 [AudioOutputRouter]   [MonitorSampleProvider]     |
|                 WASAPI Shared Event   - Math.Min(Latency, 15) ms  |
|                                       - Drift trim >1920 -> 480   |
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
- Frameless matte carbon WPF window (`1020x720`) marked with a `BETA` badge, hosting `01 Browser Audio`, `02 Microphone` (with 6 Voice FX preset chips and `FX Mix` slider), and `03 Master Output` (with `Listen To: Music | Mic | Both` selector).
- `MainViewModel` drives 30 FPS (`33 ms` `DispatcherTimer`) LED VU meter ballistics and coordinates `UpdateBrowserPassthrough()` so browser tab local playback remains active when monitoring is off or set to `Mic` (`MonitorSourceMode == 1`), and mutes automatically when monitoring `Music` (`0`) or `Both` (`2`) to avoid slapback echo.
- Includes `NotifyIcon` tray minimization (`MinimizeToTray`) with quick mute and exit controls.

### 2. Chromium Extension (`extension/`)
- **Manifest V3 (`manifest.json` v2.2.0):** Uses `tabCapture`, `tabs`, `offscreen`, and `storage` permissions with loopback host access (`127.0.0.1`, `localhost`).
- **Service Worker (`background.js`):** Manages the `offscreen.html` host, excludes internal browser URLs (`chrome://`, `brave://`, `edge://`, `opera://`, `vivaldi://`, `arc://`, `devtools://`, `about:`), captures tabs via `chrome.tabCapture.getMediaStreamId`, and pushes tab list updates.
- **Audio Capture Host (`offscreen.js` & `pcm-processor.js`):**
  - Disables WebRTC APM flags (`echoCancellation: false`, `googEchoCancellation: false`, `googAutoGainControl: false`, `googNoiseSuppression: false`, `googHighpassFilter: false`) for full-bandwidth 20 Hz–20 kHz audio.
  - Streams 128-frame (`2.67 ms`) 48 kHz interleaved Float32 stereo packets from `AudioWorkletProcessor` (with `ScriptProcessorNode` fallback) and persists custom `bridgePort` (`1025–65534`, default `49876`) in `chrome.storage.local`.
- **Toolbar Popup (`popup.html` & `popup.js`):** Exposes connection status, tab switching, local tab playback toggle, and inline **Bridge Port** editing.

### 3. Chromium Browser Launcher (`Core/Utils/BrowserLauncher.cs`)
- `FindExtensionFolder()` locates `<extracted-folder>\extension` across release and development directory layouts.
- `OpenExtensionsPage()` probes running Chromium windows, Windows default `https`/`http` browser ProgIds, background Chromium processes, and standard install locations across **Brave** (`brave://extensions`), **Google Chrome** (`chrome://extensions`), **Microsoft Edge** (`edge://extensions`), **Vivaldi** (`vivaldi://extensions`), and **Opera / Opera GX** (`opera://extensions`).

### 4. Browser Communication Bridge (`Core/Network/BrowserBridgeServer.cs`, `ProtocolMessages.cs`)
- Binds `HttpListener` strictly to `http://127.0.0.1:{_port}/` (default `49876`).
- **Health Endpoint:** `GET /status` returns JSON (`app`, `status`, `port`, `connected`) with `Cache-Control: no-store`.
- **Origin Guard (`IsAllowedWebSocketOrigin`):** Validates the HTTP `Origin` header during WebSocket upgrade; allows extension origins (`chrome-extension://`, `extension://`, `moz-extension://`) and native/test loopback clients without an `Origin`, and rejects disallowed origins with HTTP `403 Forbidden`.
- Pools receive buffers via `ArrayPool<byte>.Shared` and handles binary Float32 PCM frames alongside JSON control messages (`HELLO`, `GET_TABS`, `TAB_LIST`, `SELECT_TAB`, `CAPTURE_STARTED`, `CAPTURE_STOPPED`, `STOP_CAPTURE`, `SET_BROWSER_PASSTHROUGH`, `TAB_CLOSED`, `TAB_UPDATED`, `ERROR`).

### 5. Zero-Latency Voice FX Processor (`Core/Audio/VoiceEffectProcessor.cs`)
- Runs sample-by-sample directly ahead of `DelayLine` on the 48 kHz stereo microphone buffer with **0 samples of dry lookahead delay**, ensuring vocal sync never drifts against music.
- Smoothly crossfades mode switches and enable/disable transitions over ~10 ms (`480` frames, `BlendStep = 1.0f / 480.0f`), smooths intensity changes (`IntensitySmoothCoeff = 0.004f`), flushes denormals (`< 1e-18f`), sanitizes non-finite inputs, and guards peaks via `SoftProtect` (`0.95f` knee, `1.25f` ceiling).
- **Presets (`VoiceEffectMode`):**
  - `0 — Off (Clean)`: Bit-exact passthrough bypass once settled.
  - `1 — RoomIsolate`: 4th-order `110 Hz` Butterworth HPF cascade, `290 Hz` room boxiness notch, fast downward expander/noise gate (`-48` to `-34 dBFS` threshold scaled by intensity), `3.5 kHz` presence peak, `14 kHz` air LPF, and gentle `2.5:1` compressor (`-16 dBFS`, `+1.5 dB` makeup).
  - `2 — Echo`: `32,768`-frame circular delay line with `220 ms` Left (`10,560` frames) and `310 ms` Right (`14,880` frames) stereo ping-pong cross-feedback, in-loop `240 Hz` HPF + `3.0 kHz` LPF damping, and `tanh` feedback stabilization.
  - `3 — Telephone`: 4th-order Butterworth bandpass (`340 Hz` HPF + `3.2 kHz` LPF), `1.35 kHz` nasal peak, asymmetric `tanh` soft-clipping, `20 Hz` DC blocker, and soft speech leveler.
  - `4 — Radio`: Broadcast EQ (`75 Hz` HPF, `145 Hz` warmth peak, `380 Hz` de-mud cut, `4.5 kHz` clarity lift), `3:1` optical-style compressor (`-18 dBFS` threshold, `4 dB` soft knee, `+2.5 dB` makeup), and `SoftRadioLimit` (`0.78f`..`0.95f`).
  - `5 — HallReverb`: `160 Hz` pre-send HPF feeding 4 parallel damped stereo comb filters (`1429/1601/1811/2053` L, `+73` stereo spread R) and 2 series all-pass diffusers per channel.

### 6. Mixing Bus, Delay, Pitch Shift, & Limiter (`AudioMixerBus.cs`, `PitchShifter.cs`, `DelayLine.cs`, `AudioLimiter.cs`)
- **Signal Order:** `MicrophoneCapture` (WASAPI shared event, `16 ms`, `-56 dBFS` expander) -> `VoiceEffectProcessor` -> `DelayLine` (`0–300 ms` clamped, `0–200 ms` UI slider, Beta default `11 ms`, `0.25`-frame slew) -> summed with `PitchShifter`-processed `BrowserAudioReceiver` (`-6..+6` semitones dual-grain SOLA with 4096-sample Hann window, `2400`-frame grains, and zero-overhead `0`-semitone bypass).
- **Primary Output Limiter:** `AudioLimiter` applies `0.8 ms` attack / `80 ms` release peak envelope tracking, 1:1 transparency below `0.94f` (`-0.5 dBFS`), and `tanh` soft-knee limiting under `0.999f`.
- **Monitor Feed Routing:** `MonitorSourceMode` selects `0 = Music` (browser scaled by browser & master gain through dedicated `_monitorLimiter`), `1 = Mic` (mic scaled by mic & master gain through `_monitorLimiter`), or `2 = Both` (full limited master mix) into `_monitorBuffer` (`AdaptiveJitterBuffer` configured with `240`-frame initial preroll and `1440`-frame initial cap).

### 7. Drift-Trimmed Headphone Monitor (`AudioOutputRouter.cs`, `MonitorSampleProvider.cs`, `AdaptiveJitterBuffer.cs`)
- `AudioOutputRouter.CreateAndInitMonitorOut` opens the monitor `WasapiOut` at `Math.Min(_latencyMs, 15)` ms (with fallback to `_latencyMs` if the device rejects 15 ms) and invokes `_monitorProvider.RequestLowLatencySync()`.
- **Continuous Drift Trimming (`MonitorSampleProvider.Read`):**
  - On initial sync (`_trimOnNextRead`), trims `_monitorBuffer` via `TrimToMaxBuffered(Math.Max(240, count / 2))`.
  - During ongoing playback, whenever `_monitorBuffer.BufferedFrames > 1920` (40 ms at 48 kHz), trims back to `480` frames (10 ms) keeping the newest audio without re-entering preroll. This prevents hardware clock mismatch between `CABLE Input` and the local headphone DAC from slowly increasing monitoring delay over long sessions.

### 8. Shared & Beta Persistence (`AppSettings.cs`, `SettingsManager.cs`)
- **Core Settings (`%APPDATA%\MixerOnline\settings.json`):** `SettingsManager.Load()` reads Stable's settings file first (falling back to legacy `%APPDATA%\MixerOnline-beta\settings.json` if Stable's file does not exist) and `Save()` atomically writes `CoreSettingsDto` back to `%APPDATA%\MixerOnline\settings.json` (Beta default `MicDelayMs = 11`).
- **Beta Extras (`%APPDATA%\MixerOnline-beta\beta-settings.json`):** Stores `MonitorSourceMode`, `VoiceEffectMode`, and `VoiceEffectIntensity` independently via atomic temp-file writes (`WriteJsonAtomically`), with range sanitization (`LatencyMs` clamped `15–200`, `VoiceEffectMode` `0–5`, `MonitorSourceMode` `0–2`, `IsMonitoringEnabled` reset to `false` on launch).

---

## Automated Verification Suite (`tests/MixerOnline.Tests/Program.cs`)

Running `.\scripts\build.ps1 -Configuration Release` builds the Beta application and runs the automated DSP and integration test suite covering:
- `RingBuffer`, `AudioLimiter`, `DelayLine`, `PitchShifter`, `DeviceManager`, `BrowserBridgeServer` HTTP `/status` + WebSocket streaming, bit-exact 48 kHz continuity, and `AudioEngine` lifecycle
- `AudioMixerBus.MonitorSourceMode` routing (`Music` default vs `Mic` vs `Both`)
- `VoiceEffectProcessor` bit-exact `Off` bypass, zero dry lookahead latency across all 5 active effects, multi-preset sweep stability, and per-mode DSP behavior (`RoomIsolate`, `Echo`, `Telephone`, `Radio`, `HallReverb`)
- `AdaptiveJitterBuffer.TrimToMaxBuffered` and `MonitorSampleProvider` low-latency sync + automatic drift trimming above `1920` frames
- `SettingsManager` shared core + Beta extras round-trip, range sanitization, and byte-exact `%APPDATA%` restoration
- End-to-end `AudioEngine` + `AudioMixerBus` integration across all Voice FX modes
