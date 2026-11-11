# MixerOnline Beta — Setup & Configuration Guide

MixerOnline Beta (`v1.1.0-beta`) blends real-time 48 kHz audio from a single Chromium browser tab with your microphone (including zero-latency Voice FX and configurable headphone Cue Loopback) and outputs the combined mix to a virtual microphone for Discord, OBS Studio, Zoom, Google Meet, and TikTok Live Studio.

---

## Supported Browsers & System Requirements

- **Operating System:** Windows 10 or Windows 11 (x64)
- **Supported Browsers:** Google Chrome, Brave, Microsoft Edge, Opera / Opera GX, Vivaldi, and Arc (Chromium Manifest V3)
- **Unsupported Browsers:** Mozilla Firefox is **not supported** because it does not implement Chromium's `chrome.tabCapture` and `chrome.offscreen` APIs required for isolated tab PCM streaming.

---

## Step 1: Install VB-Audio Virtual Cable (One-Time Setup)

Windows requires a virtual audio driver to expose MixerOnline Beta's output as a microphone source to other applications.

1. Download **VB-Audio Virtual Cable** (freeware for personal use) from [vb-audio.com/Cable](https://vb-audio.com/Cable/).
2. Extract the downloaded ZIP archive, right-click `VBCABLE_Setup_x64.exe`, and select **Run as administrator**.
3. Follow the installer prompts and reboot Windows if requested.
4. Verify detection:
   - Launch `MixerOnline-beta.exe` and check the top status bar for `VB-Cable Ready`, or open **Setup Guide** inside the app.
   - Alternatively, run `.\scripts\setup_vbcable.ps1` from PowerShell.
5. Under **03 Master Output**, confirm the primary output target is set to:
   ```text
   CABLE Input (VB-Audio Virtual Cable)
   ```

---

## Step 2: Load the Chromium Browser Extension

1. Launch `MixerOnline-beta.exe`.
2. Open your Chromium browser's extensions page:
   - Click **Browser Extensions** in MixerOnline Beta's bottom toolbar to let `BrowserLauncher` detect your running or default Chromium browser automatically, **or** open the URL manually:

   | Browser | Extensions Page |
   |---|---|
   | Google Chrome | `chrome://extensions` |
   | Brave | `brave://extensions` |
   | Microsoft Edge | `edge://extensions` |
   | Opera / Opera GX | `opera://extensions` |
   | Vivaldi | `vivaldi://extensions` |
   | Arc | `arc://extensions` (or `chrome://extensions`) |

3. Toggle **Developer mode** to **ON** (top-right corner).
4. Click **Load unpacked**.
5. Select `<extracted-folder>\extension` (you can click **Extension Folder** in MixerOnline Beta's bottom bar to open this directory directly in File Explorer).
6. Pin **MixerOnline Audio Bridge** to your browser toolbar.
7. Confirm MixerOnline Beta's top status indicator switches from `Extension Offline` to `Browser Connected`.

---

## Step 3: Configure Discord, OBS Studio, Zoom, & TikTok Live Studio

### Discord
1. Open **User Settings** -> **Voice & Video**.
2. Set **Input Device** to:
   ```text
   CABLE Output (VB-Audio Virtual Cable)
   ```
3. Set **Output Device** to your physical headphones or speakers (never `CABLE Input`, which would create a feedback loop).
4. If streaming music or backing tracks through MixerOnline Beta, consider disabling **Krisp** noise suppression / echo cancellation in Discord so music frequencies are not attenuated.

### OBS Studio / Streamlabs
1. Open **Settings** -> **Audio**.
2. Under **Global Audio Devices**, set **Mic/Auxiliary Audio** to:
   ```text
   CABLE Output (VB-Audio Virtual Cable)
   ```

### Zoom / Google Meet / TikTok Live Studio
1. Open Audio Settings inside the application.
2. Select **CABLE Output (VB-Audio Virtual Cable)** as your microphone input.
3. Keep your speaker/headphone output set to your normal physical headphones.

---

## Step 4: Daily Mixing Workflow, Voice FX, & Cue Loopback

1. Launch `MixerOnline-beta.exe`.
2. **01 Browser Audio:**
   - Choose your target tab (YouTube, Spotify Web, Twitch, etc.) from the dropdown and click **Refresh** if needed, or select the tab from the **MixerOnline Audio Bridge** popup.
   - Use **Key / Tone** (`-6` to `+6` semitones) to transpose songs down (`♭ Down`) or up (`♯ Up`) without changing playback tempo, or click **Reset (0)** to restore original pitch.
3. **02 Microphone & Voice FX:**
   - Select your microphone (click **Scan** after plugging in a USB mic).
   - Pick a **Voice FX** preset (`Clean`, `Room Isolate`, `Echo`, `Telephone`, `Radio`, or `Hall`) and dial in the **FX Mix** slider (`0–100%`, default `75%`):
     - `Clean`: Bit-exact unprocessed vocal signal.
     - `Room Isolate`: Tightens untreated rooms by cutting sub-110 Hz rumble, reducing `290 Hz` boxiness, and gating background noise between words.
     - `Echo`: Stereo `220 ms / 310 ms` ping-pong vocal delay.
     - `Telephone`: Vintage `340 Hz–3.2 kHz` band-limited voice with warm saturation.
     - `Radio`: Broadcast-style EQ and `3:1` optical compression for rich speaking presence.
     - `Hall`: Spacious stereo concert reverb tail with low-end mud filtering.
   - All Voice FX presets run with **zero dry lookahead delay**, so switching presets mid-song never shifts your vocal timing.
4. **03 Master Output & Headphone Monitor (`Listen To`):**
   - Enable **Headphone Monitor** (`Cue`) and pick your wired headphones, then choose what you want to hear under **Listen To**:
     - **Music (Default):** Sends only the browser music feed (including Key / Tone shifts) to your headphones while automatically muting direct browser playback to avoid double-audio echo. Your audience still hears Music + Voice FX + Mic on `CABLE Output`.
     - **Mic:** Sends only your processed microphone signal to your headphones while keeping your browser tab's local playback preference intact.
     - **Both:** Sends the full limited master mix (`Music + Mic`) to your headphones while muting direct browser playback.
   - Keep **Headphone Monitor** **OFF** if you prefer listening straight from your browser with `Play captured tab audio in browser when Monitor is OFF` enabled in **Settings**.

---

## Calibrating Mic Sync Delay

Live microphone audio enters via WASAPI directly, whereas browser tab audio travels through Web Audio `AudioWorklet`, local WebSocket framing, and the jitter buffer cushion. Beta defaults **Mic Sync Delay** to `+11 ms` (slider range `0–200 ms`, quick presets `0ms`, `45ms`, `80ms`).

1. Record a short test clip in OBS Studio or Audacity capturing `CABLE Output (VB-Audio Virtual Cable)` while playing a rhythmic beat in your captured tab.
2. Clap your hands or sing crisp syllables (`"tah, tah, tah"`) in time with the beat.
3. Listen back to the recorded mix:
   - **If your voice/clap lands before the beat (early):** raise **Mic Sync Delay** from `+11 ms` toward `+25 ms`–`+45 ms` (or click `45ms` and fine-tune).
   - **If your voice/clap lands after the beat (late):** reduce **Mic Sync Delay** toward `0ms`.
   - **If you raise Audio Buffer Size to `50 ms` or `100 ms` in Settings:** test `+45 ms` to `+80 ms`+ until claps align cleanly with the beat.

---

## Shared Settings & Beta Profiles

MixerOnline Beta automatically stays compatible with Stable MixerOnline:
- Core mixer settings (devices, gains, `MicDelayMs`, `LatencyMs`, `BridgePort`) are loaded from and saved to `%APPDATA%\MixerOnline\settings.json` (with fallback migration from legacy `%APPDATA%\MixerOnline-beta\settings.json`).
- Beta-only controls (`VoiceEffectMode`, `VoiceEffectIntensity`, and `MonitorSourceMode`) are stored separately in `%APPDATA%\MixerOnline-beta\beta-settings.json` so Stable and Beta can coexist cleanly without overwriting each other's format.

---

## Customizing the Local Bridge Port

Both MixerOnline Beta and the browser extension communicate over loopback `127.0.0.1:49876` by default. If port `49876` is already in use:

1. In MixerOnline Beta, click **Settings**, enter a new **Local Bridge Port** (`1025–65534`), click **Save Changes**, and restart `MixerOnline-beta.exe` so the bridge binds the new port.
2. Click the **MixerOnline Audio Bridge** icon in your browser toolbar, enter the exact same number under **Bridge Port**, and click **Apply**.
3. Wait for the popup badge to switch from `Mixer Offline` to `Mixer Connected`.

---

## Troubleshooting

- **`Extension Offline` or `Port in use`:**
  Make sure only one MixerOnline instance is listening on the configured port, verify **Bridge Port** in the extension popup matches **Local Bridge Port** in **Settings**, and test `http://127.0.0.1:49876/status` (using your chosen port) to verify `"status":"ready"`.
- **Headphone Monitor latency creeps upward over long sessions:**
  MixerOnline Beta trims clock drift in `MonitorSampleProvider` whenever queued frames exceed `1920` (40 ms). Ensure your monitor target is a direct wired headphone/speaker device rather than a high-latency virtual enhancer (FxSound or Steam Streaming Speakers).
- **Double audio or echo when monitoring:**
  When `Listen To` is set to `Music` or `Both`, MixerOnline Beta automatically mutes direct browser playback. If you still hear echo, verify your voice chat app's **Output Device** is your physical headphones and not `CABLE Input`.
