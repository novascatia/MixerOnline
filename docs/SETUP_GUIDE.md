# MixerOnline — Setup & Configuration Guide

MixerOnline mixes real-time 48 kHz audio from a single Chromium browser tab with your microphone and routes the combined signal to a virtual microphone for Discord, OBS Studio, Zoom, Google Meet, and TikTok Live Studio.

---

## Supported Browsers & System Requirements

- **Operating System:** Windows 10 or Windows 11 (x64)
- **Supported Browsers:** Google Chrome, Brave, Microsoft Edge, Opera / Opera GX, Vivaldi, and Arc (Chromium Manifest V3)
- **Unsupported Browsers:** Mozilla Firefox is **not supported** because it does not implement Chromium's `chrome.tabCapture` and `chrome.offscreen` APIs required for isolated tab PCM streaming.

---

## Step 1: Install VB-Audio Virtual Cable (One-Time Setup)

Windows requires a virtual audio driver to expose MixerOnline's output as a selectable microphone in other applications.

1. Download **VB-Audio Virtual Cable** (freeware for personal use) from [vb-audio.com/Cable](https://vb-audio.com/Cable/).
2. Extract the downloaded ZIP archive, right-click `VBCABLE_Setup_x64.exe`, and select **Run as administrator**.
3. Follow the installer prompts and reboot Windows if requested.
4. Verify detection:
   - Launch `MixerOnline.exe` and check the top status bar for `VB-Cable Ready`, or open **Setup Guide** inside the app.
   - Alternatively, run `.\scripts\setup_vbcable.ps1` from PowerShell.
5. Under **03 Master Output**, confirm the output device is set to:
   ```text
   CABLE Input (VB-Audio Virtual Cable)
   ```

---

## Step 2: Load the Chromium Browser Extension

1. Launch `MixerOnline.exe` (optional for this step, or use its bottom bar shortcuts).
2. Open your Chromium browser's extensions page:
   - Click **Browser Extensions** in MixerOnline's bottom toolbar to let `BrowserLauncher` detect your running or default Chromium browser automatically, **or** open the URL manually:

   | Browser | Extensions Page |
   |---|---|
   | Google Chrome | `chrome://extensions` |
   | Brave | `brave://extensions` |
   | Microsoft Edge | `edge://extensions` |
   | Opera / Opera GX | `opera://extensions` |
   | Vivaldi | `vivaldi://extensions` |
   | Arc | `arc://extensions` (or `chrome://extensions`) |

3. Toggle **Developer mode** to **ON** (top-right corner on most Chromium browsers).
4. Click **Load unpacked**.
5. Select `<extracted-folder>\extension` (you can click **Extension Folder** in MixerOnline's bottom bar to open this directory directly in File Explorer).
6. Pin **MixerOnline Audio Bridge** to your browser toolbar.
7. Confirm MixerOnline's top status indicator switches from `Extension Offline` to `Browser Connected`.

---

## Step 3: Configure Discord, OBS Studio, Zoom, & TikTok Live Studio

### Discord
1. Open **User Settings** -> **Voice & Video**.
2. Set **Input Device** to:
   ```text
   CABLE Output (VB-Audio Virtual Cable)
   ```
3. Set **Output Device** to your physical headphones or speakers (never `CABLE Input`, which would create a feedback loop).
4. If streaming music or backing tracks through MixerOnline, consider disabling **Krisp** noise suppression / echo cancellation in Discord so music frequencies are not gated out.

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

## Step 4: Daily Mixing Workflow

1. Launch `MixerOnline.exe`.
2. **01 Browser Audio:**
   - Choose your target tab (YouTube, Spotify Web, Twitch, etc.) from the dropdown and click **Refresh** if a newly opened tab is not listed yet. You can also start or stop capture from the **MixerOnline Audio Bridge** toolbar popup.
   - Use **Key / Tone** (`-6` to `+6` semitones) to transpose songs down (`♭ Down`) or up (`♯ Up`) without changing playback speed, or click **Reset (0)** to return to the original key.
   - Click the sample-rate badge in the top-right of panel `01` to cycle sample-rate conversion (`Auto`, `44.1 kHz -> 48.0 kHz`, or `48.0 kHz Direct`) if needed.
3. **02 Microphone:**
   - Select your microphone (click **Scan** after plugging in a USB mic).
   - Balance the fader so normal speech peaks around healthy levels on the dual LED meter without hitting full red.
4. **03 Master Output & Headphone Monitor:**
   - Keep **Headphone Monitor** (`Cue`) **OFF** during normal sessions when `Play captured tab audio in browser when Monitor is OFF` is checked in **Settings**. You will hear the browser tab directly with 0 ms browser latency and no microphone self-echo.
   - Toggle **Headphone Monitor** **ON** and pick your headphones if you want to audition the mixed `CABLE Input` feed locally. MixerOnline automatically mutes direct browser tab passthrough while monitoring is ON to prevent double-audio slapback.

---

## Calibrating Mic Sync Delay

Live microphone audio enters via WASAPI directly, whereas browser tab audio travels through Web Audio `AudioWorklet`, local WebSocket framing, and the jitter buffer cushion. Without compensation, your voice can arrive slightly earlier than the music.

1. Open OBS Studio, Audacity, or a Discord voice check and record `CABLE Output (VB-Audio Virtual Cable)` while playing a rhythmic track or metronome in your captured tab.
2. Clap or speak short syllables (`"tah, tah, tah"`) on each beat.
3. Play back the recording and adjust **Mic Sync Delay** (`0–200 ms`, default `+45 ms`):
   - **Voice/clap lands before the beat:** increase **Mic Sync Delay** (`+11 ms` -> `+45 ms`, or use the `45ms` / `80ms` quick presets).
   - **Voice/clap lands after the beat:** decrease **Mic Sync Delay** toward `0ms` or `+11ms` (typical when **Audio Buffer Size** in **Settings** is `15 ms` or `30 ms`).

---

## Customizing the Local Bridge Port

Both MixerOnline and the browser extension communicate over loopback `127.0.0.1:49876` by default. If another tool already uses port `49876` (status bar shows `Failed to start browser bridge on port 49876`):

1. In MixerOnline desktop, click **Settings**, enter a new **Local Bridge Port** (`1025–65534`), click **Save Changes**, and restart `MixerOnline.exe` so the bridge binds the new port.
2. Click the **MixerOnline Audio Bridge** icon in your browser toolbar, enter the exact same number under **Bridge Port**, and click **Apply**.
3. Wait for the popup badge to change from `Mixer Offline` to `Mixer Connected`.

---

## Troubleshooting

- **`Extension Offline` in MixerOnline:**
  Confirm the extension is enabled in `chrome://extensions` (or `brave://extensions` / `edge://extensions`), verify the **Bridge Port** in the extension popup matches **Local Bridge Port** in MixerOnline **Settings**, and test `http://127.0.0.1:49876/status` (substituting your port) in a browser tab — it should return JSON with `"status":"ready"`.
- **Tab shows no audio or disappears from list:**
  Internal browser pages (`chrome://`, `brave://`, `edge://`, `devtools://`, extension pages) are intentionally filtered out. Make sure the target page is a standard `http://` or `https://` tab and is actively playing audio.
- **Echo or doubled music in headphones:**
  Make sure you are not listening to both browser passthrough and **Headphone Monitor** simultaneously, and verify your communication app's **Output Device** is your physical headphones rather than `CABLE Input`.
- **Crackling or high delay on Headphone Monitor:**
  Select your direct hardware headphones or speakers as the **Headphone Monitor** device rather than third-party virtual audio enhancers (such as FxSound or Steam Streaming Speakers). If needed, raise **Audio Buffer Size** in **Settings** from `15 ms` to `30 ms` or `50 ms`.
