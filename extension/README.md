# MixerOnline Audio Bridge — Chromium Extension

This Chromium Manifest V3 extension captures audio exclusively from a single selected browser tab (such as YouTube, Spotify Web, Twitch, or web players) and streams uncompressed 48 kHz 32-bit float stereo PCM audio to the **MixerOnline** desktop mixer over local loopback WebSocket.

Compatible with **Google Chrome**, **Brave**, **Microsoft Edge**, **Opera**, **Vivaldi**, and **Arc**.

---

## Quick Installation (Load Unpacked)

1. Open your Chromium-based browser and enter the corresponding extensions page in the address bar:

   | Browser | Extensions URL |
   |---|---|
   | Google Chrome | `chrome://extensions` |
   | Brave | `brave://extensions` |
   | Microsoft Edge | `edge://extensions` |
   | Opera | `opera://extensions` |
   | Vivaldi | `vivaldi://extensions` |
   | Arc | `arc://extensions` (or `chrome://extensions`) |

2. Enable the **Developer mode** toggle (typically in the top-right corner of the extensions page).
3. Click **Load unpacked**.
4. Browse to your MixerOnline folder and select its `extension` directory (the folder containing `manifest.json`).
5. Pin **MixerOnline Audio Bridge** to your browser toolbar for one-click access.

---

## Changing the Bridge Port

By default, both the desktop app and the extension connect over `127.0.0.1:49876` (`ws://127.0.0.1:49876/ws`).

If you change **Local Bridge Port** inside MixerOnline desktop **Settings**:

1. Click the **MixerOnline Audio Bridge** icon in your browser toolbar.
2. Enter the same port number (`1025`–`65534`) in **Bridge Port**.
3. Click **Apply**. The popup displays **Saved** and immediately reconnects to the desktop mixer on the new port.

---

## Features

- **Exclusive Tab Audio Capture**: Captures only the selected tab without mixing in system sounds, desktop notifications, or other tabs.
- **Uncompressed Studio Pipeline**: Uses Web Audio API (`AudioWorklet`) to stream 48 kHz 32-bit float stereo audio over loopback WebSocket.
- **Dynamic Tab Switching**: Switch captured tabs at any time from either the desktop mixer UI or the extension popup.
- **Local Tab Playback Toggle**: Keep listening through the browser tab or mute browser playback to monitor exclusively through MixerOnline.
- **Automatic Recovery**: Handles tab closures, page navigations, and automatic reconnection if the desktop app restarts.
