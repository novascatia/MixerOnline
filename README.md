<p align="center"><img src="docs/assets/banner.png" alt="MixerOnline" width="840" /></p>

<p align="center">
  Real-time 48 kHz 32-bit float Windows desktop audio mixer that blends an isolated Chromium browser tab with your microphone into a virtual microphone for Discord, OBS Studio, Zoom, and TikTok Live Studio.
</p>

<p align="center">
  <a href="#download--install-no-coding-required">Download &amp; Install</a> &middot;
  <a href="#features">Features</a> &middot;
  <a href="#calibrating-mic-sync-delay">Calibrating Mic Sync Delay</a> &middot;
  <a href="docs/SETUP_GUIDE.md">Setup Guide</a> &middot;
  <a href="docs/ARCHITECTURE.md">Architecture</a> &middot;
  <a href="#build--package-from-source">Build from Source</a> &middot;
  <a href="#license">License</a>
</p>

---

## Download & Install (No Coding Required)

MixerOnline ships as a portable, self-contained Windows x64 application paired with a lightweight Manifest V3 browser extension. You do not need to install the .NET SDK or Visual Studio.

### 1. Download the Release Archive
1. Open the **Releases** page on GitHub and download `MixerOnline-v1.0.0-win-x64.zip`.
2. Extract the archive into a permanent folder (referred to below as `<extracted-folder>`).

### 2. Install VB-Audio Virtual Cable (One-Time Setup)
Windows requires a virtual audio driver so applications like Discord or OBS can treat MixerOnline's output as a microphone.
1. Download the free driver from [VB-Audio Virtual Cable](https://vb-audio.com/Cable/).
2. Extract the driver archive, right-click `VBCABLE_Setup_x64.exe`, and choose **Run as administrator**.
3. Reboot Windows if prompted. You can verify the driver afterward by running `.\scripts\setup_vbcable.ps1` or opening **Setup Guide** inside MixerOnline.

### 3. Load the Chromium Browser Extension
Load `<extracted-folder>\extension` in **any supported Chromium browser**:

| Browser | Extensions Page |
|---|---|
| Google Chrome | `chrome://extensions` |
| Brave | `brave://extensions` |
| Microsoft Edge | `edge://extensions` |
| Opera / Opera GX | `opera://extensions` |
| Vivaldi | `vivaldi://extensions` |
| Arc | `arc://extensions` (or `chrome://extensions`) |

1. Open your browser's extensions page from the table above (or click **Browser Extensions** in MixerOnline's bottom toolbar).
2. Enable **Developer mode** in the top-right corner.
3. Click **Load unpacked** and select `<extracted-folder>\extension`.
4. Pin **MixerOnline Audio Bridge** to your toolbar for quick access.

> **Note:** Firefox is **not supported**. MixerOnline relies on Chromium's `chrome.tabCapture` and `chrome.offscreen` Manifest V3 APIs to isolate audio from an individual browser tab without capturing desktop notifications or other tabs.

### 4. Launch and Route Audio
1. Double-click `MixerOnline.exe` inside `<extracted-folder>`.
2. In **01 Browser Audio**, pick the browser tab you want to stream (for example YouTube, Spotify Web, or Twitch).
3. In **02 Microphone**, choose your physical microphone.
4. In **03 Master Output**, confirm the primary target is `CABLE Input (VB-Audio Virtual Cable)`.
5. In Discord, OBS Studio, Zoom, Google Meet, or TikTok Live Studio, set your **Input / Microphone Device** to:
   ```text
   CABLE Output (VB-Audio Virtual Cable)
   ```

---

## Features

| Capability | Details |
|---|---|
| **Exclusive Tab Capture** | Isolates audio from one selected tab across Chrome, Brave, Edge, Opera, Vivaldi, or Arc via Web Audio `AudioWorklet` (`48 kHz` stereo Float32 PCM) with WebRTC echo/noise/AGC processing bypassed for full 20 Hz–20 kHz fidelity. |
| **48 kHz 32-Bit Float Mixing Bus** | Real-time stereo summing bus (`AudioMixerBus.cs`) with exponential gain smoothing (`0.015f` per frame) to eliminate slider zipper noise and clicks. |
| **Key / Tone Transposer (`-6` to `+6` semitones)** | Dual-grain synchronous overlap-add pitch shifter (`PitchShifter.cs`) using 50 ms grains and a 4096-sample Hann window to shift music key without changing tempo, with bit-exact bypass at `0` semitones. |
| **Mic Sync Delay (`0–200ms`)** | Sample-accurate circular delay line (`DelayLine.cs`) with smooth slew control and quick `0ms`, `45ms`, and `80ms` presets (default `+45ms`) to align live microphone speech with browser playback latency. |
| **Headphone Cue Monitor** | Toggleable local headphone monitor output (`MonitorSampleProvider.cs`) that automatically mutes direct browser tab playback while monitoring is active to prevent double-audio slapback echo. |
| **Soft-Knee Limiter** | Mastering peak limiter (`AudioLimiter.cs`) passing signals 1:1 below `-0.5 dBFS` (`0.94f`) and smoothly limiting transients below `0.999f` (`-0.001 dBFS`) with 0.8 ms attack and 80 ms release. |
| **Custom Bridge Port** | Loopback WebSocket port (default `49876`, valid range `1025–65534`) configurable in both desktop **Settings** (`Local Bridge Port`) and the extension popup (**Bridge Port** -> **Apply**). |
| **Console UI & Device Resilience** | Dark 3-channel workspace with 30 FPS Peak & RMS LED VU meters, system tray minimization, persistent JSON settings (`%APPDATA%\MixerOnline\settings.json`), and automatic WASAPI hot-plug recovery. |

---

## Calibrating Mic Sync Delay

Because browser tab audio passes through Chromium's `AudioWorklet`, local loopback WebSocket transport, and MixerOnline's `AdaptiveJitterBuffer` preroll cushion, browser playback arrives slightly later than an unprocessed hardware microphone. Without delay compensation, your voice can sound slightly early compared to a song or backing track.

Use this quick calibration check (typically `+11ms` to `+45ms`):

1. Leave **Headphone Monitor** OFF so you hear the browser tab directly, or open a short local recording test in OBS Studio / Audacity capturing `CABLE Output (VB-Audio Virtual Cable)`.
2. Play a steady metronome, drum groove, or karaoke track in your selected browser tab.
3. Clap your hands or speak crisp syllables (`"tah, tah, tah"`) in time with the beat.
4. Listen back to the recorded mix (or ask a friend on Discord):
   - **If your voice/clap lands before the beat (early):** increase **Mic Sync Delay**. Try the `45ms` preset first, then nudge upward or downward in `5–10 ms` steps.
   - **If your voice/clap lands after the beat (late):** reduce **Mic Sync Delay** toward `+11ms` or `0ms` (common when **Audio Buffer Size** in Settings is set to `15 ms` or `30 ms`).
   - **If you raise Audio Buffer Size to `50 ms` or `100 ms`:** try the `80ms` preset or slide between `+45ms` and `+100ms` until claps lock tightly onto the beat.

---

## Audio Engine Specifications

| Parameter | Specification |
|---|---|
| **Internal Mixing Format** | 48,000 Hz, 32-bit IEEE Float, Stereo |
| **Capture & Output API** | Windows WASAPI (Shared Event-Driven) |
| **Browser Transport** | Loopback WebSocket (`ws://127.0.0.1:<port>/ws`, default `49876`, Origin-validated) |
| **Audio Buffer Presets** | `15 ms`, `30 ms` (default), `50 ms`, `100 ms` |
| **Key Transposer** | `-6` to `+6` semitones (1-semitone steps, tempo-preserving) |
| **Mic Sync Delay** | `0–200 ms` UI slider (`0–300 ms` DSP engine capacity, default `+45 ms`) |
| **Volume Smoothing** | Exponential per-frame interpolation (`0.015f`) |
| **Soft-Knee Limiter** | `0.94f` (`-0.5 dBFS`) knee threshold, `0.999f` (`-0.001 dBFS`) peak ceiling |
| **Metering Refresh** | 30 FPS Peak & RMS ballistics |

---

## Project Structure

```text
MixerOnline/
├── .github/workflows/release.yml   # GitHub Actions build, test, and release workflow
├── docs/
│   ├── assets/                     # Brand banner and logos (PNG / SVG)
│   ├── ARCHITECTURE.md             # Technical architecture & DSP specification
│   └── SETUP_GUIDE.md              # Step-by-step setup, calibration, and troubleshooting
├── extension/                      # Chromium Manifest V3 Audio Bridge extension
│   ├── manifest.json               # Permissions & MV3 configuration
│   ├── background.js               # Tab enumeration, filtering, and stream ID setup
│   ├── offscreen.html / .js        # Web Audio API host & WebSocket client
│   ├── pcm-processor.js            # 48 kHz Float32 AudioWorklet packetizer
│   ├── popup.html / .js            # Toolbar popup UI & Bridge Port editor
│   └── README.md                   # Extension installation & port reference
├── scripts/
│   ├── build.ps1                   # Build application & run automated verification suite
│   ├── package.ps1                 # Publish self-contained x64 EXE & release ZIP in dist/
│   └── setup_vbcable.ps1           # Diagnostic check for VB-Audio Virtual Cable
├── src/MixerOnline.App/            # .NET 8 WPF Mixing Console Application
│   ├── Controls/                   # Custom console fader & dual stereo LED VU meters
│   ├── Core/Audio/                 # AudioEngine, MixerBus, PitchShifter, DelayLine, Limiter
│   ├── Core/Config/                # AppSettings & SettingsManager (%APPDATA% persistence)
│   ├── Core/Network/               # BrowserBridgeServer (Origin-guarded WS / /status)
│   ├── Core/Utils/                 # BrowserLauncher (Chromium detection & extension URL)
│   ├── ViewModels/                 # MainViewModel & MVVM commands
│   └── Views/                      # SettingsDialog & SetupGuideDialog
├── tests/MixerOnline.Tests/        # Automated DSP and integration test suite
├── .gitignore
├── LICENSE                         # MIT License
└── README.md
```

---

## Build & Package from Source

### Prerequisites
- Windows 10 or Windows 11 (x64)
- [.NET 8 SDK (`8.0.x`)](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build and Run Automated Verification Suite
```powershell
.\scripts\build.ps1 -Configuration Release
```
This compiles `MixerOnline.App` and executes the automated DSP and integration test suite in `tests\MixerOnline.Tests\Program.cs`, covering `RingBuffer`, `AudioLimiter`, `DelayLine`, `PitchShifter`, `DeviceManager` WASAPI & VB-Cable detection, `BrowserBridgeServer` HTTP `/status` & WebSocket binary streaming, bit-exact 48 kHz audio continuity, and `AudioEngine` lifecycle.

### Create Portable EXE & GitHub Release Archive
```powershell
.\scripts\package.ps1
```
All release outputs are created cleanly inside `dist\`:
- `dist\MixerOnline.exe` — Self-contained single-file Windows x64 executable
- `dist\MixerOnline-v1.0.0-win-x64.zip` — Portable archive containing `MixerOnline.exe`, `extension\`, `README.md`, `LICENSE`, and `docs\`

---

## License

Licensed under the **MIT License** (`Copyright (c) 2026 MixerOnline Contributors`). See [LICENSE](LICENSE) for full terms.

VB-Audio Virtual Cable is a third-party driver provided by VB-Audio Software as freeware for personal use.
