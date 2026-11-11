/**
 * MixerOnline Offscreen Document Audio Capture & WebSocket Streamer
 * Lossless 48kHz 32-Bit Float (3,072 kbps Uncompressed PCM) Pipeline
 */

const DEFAULT_BRIDGE_PORT = 49876;
const MIN_BRIDGE_PORT = 1025;
const MAX_BRIDGE_PORT = 65534;
const BRIDGE_PORT_STORAGE_KEY = 'bridgePort';
const EXTENSION_CLIENT_NAME = 'ChromiumExtension';
const EXTENSION_VERSION = '2.2.0';

let currentPort = DEFAULT_BRIDGE_PORT;
let ws = null;
let reconnectTimer = null;
let audioContext = null;
let mediaStream = null;
let sourceNode = null;
let passThroughGainNode = null;
let keepAliveGainNode = null;
let workletNode = null;
let scriptProcessorNode = null;
let currentTabId = null;
let currentTabTitle = '';
let passThroughEnabled = true;
let captureGen = 0;

console.log('[MixerOnline Offscreen] Initialized');

function parseValidPort(value) {
  if (typeof value === 'number') {
    return Number.isInteger(value) && value >= MIN_BRIDGE_PORT && value <= MAX_BRIDGE_PORT
      ? value
      : null;
  }

  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (!/^\d+$/.test(trimmed)) {
      return null;
    }
    const parsed = Number(trimmed);
    return Number.isInteger(parsed) && parsed >= MIN_BRIDGE_PORT && parsed <= MAX_BRIDGE_PORT
      ? parsed
      : null;
  }

  return null;
}

function getWebSocketUrl() {
  return `ws://127.0.0.1:${currentPort}/ws`;
}

function clearReconnectTimer() {
  if (reconnectTimer) {
    clearInterval(reconnectTimer);
    reconnectTimer = null;
  }
}

function closeActiveSocket() {
  if (!ws) {
    return;
  }

  const oldSocket = ws;
  ws = null;

  try {
    oldSocket.onopen = null;
    oldSocket.onmessage = null;
    oldSocket.onclose = null;
    oldSocket.onerror = null;
    if (oldSocket.readyState === WebSocket.OPEN || oldSocket.readyState === WebSocket.CONNECTING) {
      oldSocket.close();
    }
  } catch {}
}

function connectWebSocket() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
    return;
  }

  try {
    const socket = new WebSocket(getWebSocketUrl());
    ws = socket;
    socket.binaryType = 'arraybuffer';

    socket.onopen = () => {
      if (ws !== socket) {
        try {
          socket.close();
        } catch {}
        return;
      }

      clearReconnectTimer();

      sendJson({
        type: 'HELLO',
        client: EXTENSION_CLIENT_NAME,
        version: EXTENSION_VERSION
      });

      chrome.runtime.sendMessage({ action: 'REQUEST_TABS' }).catch(() => {});
      chrome.runtime.sendMessage({ action: 'STATUS_UPDATED', connected: true }).catch(() => {});

      if (mediaStream && currentTabId && audioContext) {
        sendJson({
          type: 'CAPTURE_STARTED',
          tabId: currentTabId,
          title: currentTabTitle,
          sampleRate: audioContext.sampleRate || 48000,
          channels: 2,
          active: true
        });
      }
    };

    socket.onmessage = (event) => {
      if (ws !== socket) {
        return;
      }

      if (typeof event.data === 'string') {
        try {
          const msg = JSON.parse(event.data);
          handleDesktopMessage(msg);
        } catch (err) {
          console.error('[MixerOnline Offscreen] Error parsing message:', err);
        }
      }
    };

    socket.onclose = () => {
      if (ws !== socket) {
        return;
      }

      ws = null;
      chrome.runtime.sendMessage({ action: 'STATUS_UPDATED', connected: false }).catch(() => {});
      scheduleReconnect();
    };

    socket.onerror = () => {};
  } catch {
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (!reconnectTimer) {
    reconnectTimer = setInterval(connectWebSocket, 1000);
  }
}

async function initAndConnect() {
  try {
    if (chrome?.storage?.local) {
      const stored = await chrome.storage.local.get(BRIDGE_PORT_STORAGE_KEY);
      const validPort = parseValidPort(stored?.[BRIDGE_PORT_STORAGE_KEY]);
      currentPort = validPort ?? DEFAULT_BRIDGE_PORT;
    }
  } catch (err) {
    console.warn('[MixerOnline Offscreen] Failed to load bridgePort from storage:', err);
    currentPort = DEFAULT_BRIDGE_PORT;
  }

  connectWebSocket();
}

async function applyBridgePort(rawPort) {
  const validPort = parseValidPort(rawPort);
  if (validPort === null) {
    return {
      success: false,
      error: `Port must be an integer between ${MIN_BRIDGE_PORT} and ${MAX_BRIDGE_PORT}.`,
      bridgePort: currentPort
    };
  }

  try {
    if (chrome?.storage?.local) {
      await chrome.storage.local.set({ [BRIDGE_PORT_STORAGE_KEY]: validPort });
    }
  } catch (err) {
    return {
      success: false,
      error: err?.message || 'Failed to save bridge port.',
      bridgePort: currentPort
    };
  }

  currentPort = validPort;
  clearReconnectTimer();
  closeActiveSocket();
  connectWebSocket();

  return {
    success: true,
    bridgePort: currentPort
  };
}

function sendJson(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(obj));
  }
}

function sendBinary(buffer) {
  if (ws && ws.readyState === WebSocket.OPEN && ws.bufferedAmount < 131072) {
    ws.send(buffer);
  }
}

function handleDesktopMessage(msg) {
  switch (msg.type) {
    case 'GET_TABS':
      chrome.runtime.sendMessage({ action: 'REQUEST_TABS' }).catch(() => {});
      break;

    case 'SELECT_TAB':
      if (msg.tabId && msg.tabId !== currentTabId) {
        chrome.runtime.sendMessage({ action: 'CAPTURE_TAB_REQUEST', tabId: msg.tabId }).catch(() => {});
      }
      break;

    case 'STOP_CAPTURE':
      stopAudioCapture();
      sendJson({ type: 'CAPTURE_STOPPED', tabId: currentTabId });
      chrome.runtime.sendMessage({ action: 'CAPTURE_STOPPED' }).catch(() => {});
      break;

    case 'SET_BROWSER_PASSTHROUGH':
      passThroughEnabled = Boolean(msg.enabled);
      updatePassThrough();
      break;
  }
}

function updatePassThrough() {
  if (!passThroughGainNode || !audioContext) return;
  try {
    const target = passThroughEnabled ? 1.0 : 0.0;
    passThroughGainNode.gain.setTargetAtTime(target, audioContext.currentTime, 0.01);
  } catch (err) {
    console.warn('[MixerOnline Offscreen] Error updating passthrough:', err);
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'START_CAPTURE_WITH_STREAM') {
    startAudioCapture(message.streamId, message.tabId, message.title)
      .then(() => sendResponse({ success: true }))
      .catch((err) => {
        console.error('[MixerOnline Offscreen] Capture error:', err);
        sendResponse({ success: false, error: err.message });
      });
    return true;
  }

  if (message.action === 'STOP_CAPTURE') {
    stopAudioCapture();
    sendJson({ type: 'CAPTURE_STOPPED', tabId: currentTabId });
    sendResponse({ success: true });
    return false;
  }

  if (message.action === 'SEND_TAB_LIST') {
    sendJson({
      type: 'TAB_LIST',
      tabs: message.tabs
    });
    sendResponse({ success: true });
    return false;
  }

  if (message.action === 'GET_STATE') {
    sendResponse({
      connected: Boolean(ws && ws.readyState === WebSocket.OPEN),
      capturing: Boolean(mediaStream),
      currentTabId,
      currentTabTitle,
      passThroughEnabled,
      bridgePort: currentPort
    });
    return false;
  }

  if (message.action === 'GET_BRIDGE_PORT') {
    sendResponse({
      success: true,
      bridgePort: currentPort
    });
    return false;
  }

  if (message.action === 'SET_BRIDGE_PORT') {
    applyBridgePort(message.port ?? message.bridgePort)
      .then((result) => sendResponse(result))
      .catch((err) => sendResponse({
        success: false,
        error: err?.message || 'Failed to update bridge port.',
        bridgePort: currentPort
      }));
    return true;
  }

  if (message.action === 'SET_PASSTHROUGH') {
    passThroughEnabled = Boolean(message.enabled);
    updatePassThrough();
    sendResponse({ success: true, passThroughEnabled });
    return false;
  }
});

async function startAudioCapture(streamId, tabId, title) {
  stopAudioCapture();
  const myGen = ++captureGen;

  currentTabId = tabId;
  currentTabTitle = title || `Tab #${tabId}`;

  try {
    let stream = null;
    try {
      // Force Chromium to bypass WebRTC APM (Echo Cancellation, Noise Suppression, Highpass, AGC)
      // inside the mandatory block so music retains 100% uncompressed 20Hz-20kHz studio response.
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          mandatory: {
            chromeMediaSource: 'tab',
            chromeMediaSourceId: streamId,
            echoCancellation: false,
            googEchoCancellation: false,
            googAutoGainControl: false,
            googNoiseSuppression: false,
            googHighpassFilter: false
          }
        },
        video: false
      });
    } catch {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          mandatory: {
            chromeMediaSource: 'tab',
            chromeMediaSourceId: streamId
          }
        },
        video: false
      });
    }

    if (myGen !== captureGen) {
      stream.getTracks().forEach((track) => track.stop());
      return;
    }

    mediaStream = stream;

    const ctx = new (window.AudioContext || window.webkitAudioContext)({
      sampleRate: 48000,
      latencyHint: 'playback'
    });

    if (ctx.state === 'suspended') {
      await ctx.resume();
    }

    if (myGen !== captureGen) {
      ctx.close().catch(() => {});
      stream.getTracks().forEach((track) => track.stop());
      return;
    }

    audioContext = ctx;
    const activeSampleRate = audioContext.sampleRate || 48000;

    sourceNode = audioContext.createMediaStreamSource(mediaStream);

    passThroughGainNode = audioContext.createGain();
    passThroughGainNode.gain.value = passThroughEnabled ? 1.0 : 0.0;
    sourceNode.connect(passThroughGainNode);
    passThroughGainNode.connect(audioContext.destination);

    keepAliveGainNode = audioContext.createGain();
    keepAliveGainNode.gain.value = 0.0;
    keepAliveGainNode.connect(audioContext.destination);

    let workletSuccess = false;
    try {
      await audioContext.audioWorklet.addModule('pcm-processor.js');
      if (myGen !== captureGen) return;

      workletNode = new AudioWorkletNode(audioContext, 'pcm-processor', {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        outputChannelCount: [2],
        channelCount: 2,
        channelCountMode: 'explicit'
      });

      workletNode.port.onmessage = (event) => {
        if (myGen === captureGen) {
          sendBinary(event.data);
        }
      };

      sourceNode.connect(workletNode);
      workletNode.connect(keepAliveGainNode);
      workletSuccess = true;
    } catch (workletErr) {
      console.warn('[MixerOnline Offscreen] AudioWorklet fallback:', workletErr);
    }

    if (!workletSuccess && myGen === captureGen) {
      const bufferSize = 512;
      scriptProcessorNode = audioContext.createScriptProcessor(bufferSize, 2, 2);

      scriptProcessorNode.onaudioprocess = (event) => {
        if (myGen !== captureGen) return;
        const inputL = event.inputBuffer.getChannelData(0);
        const inputR = event.inputBuffer.numberOfChannels > 1 ? event.inputBuffer.getChannelData(1) : inputL;
        const length = inputL.length;

        const interleaved = new Float32Array(length * 2);
        for (let i = 0; i < length; i++) {
          interleaved[i * 2] = inputL[i];
          interleaved[i * 2 + 1] = inputR[i];
        }

        sendBinary(interleaved.buffer);
      };

      sourceNode.connect(scriptProcessorNode);
      scriptProcessorNode.connect(keepAliveGainNode);
    }

    mediaStream.getAudioTracks().forEach((track) => {
      track.onended = () => {
        if (myGen === captureGen) {
          stopAudioCapture();
          sendJson({ type: 'TAB_CLOSED', tabId });
          chrome.runtime.sendMessage({ action: 'CAPTURE_STOPPED' }).catch(() => {});
        }
      };
    });

    sendJson({
      type: 'CAPTURE_STARTED',
      tabId: currentTabId,
      title: currentTabTitle,
      sampleRate: activeSampleRate,
      channels: 2,
      active: true
    });

    chrome.runtime.sendMessage({
      action: 'CAPTURE_STARTED',
      tabId: currentTabId,
      title: currentTabTitle
    }).catch(() => {});
  } catch (err) {
    stopAudioCapture();
    sendJson({
      type: 'ERROR',
      message: `Failed to capture tab audio: ${err.message}`
    });
    throw err;
  }
}

function stopAudioCapture() {
  captureGen++;
  if (scriptProcessorNode) {
    try { scriptProcessorNode.disconnect(); } catch {}
    scriptProcessorNode = null;
  }
  if (workletNode) {
    try { workletNode.disconnect(); } catch {}
    workletNode = null;
  }
  if (passThroughGainNode) {
    try { passThroughGainNode.disconnect(); } catch {}
    passThroughGainNode = null;
  }
  if (keepAliveGainNode) {
    try { keepAliveGainNode.disconnect(); } catch {}
    keepAliveGainNode = null;
  }
  if (sourceNode) {
    try { sourceNode.disconnect(); } catch {}
    sourceNode = null;
  }
  if (mediaStream) {
    try {
      mediaStream.getTracks().forEach((track) => track.stop());
    } catch {}
    mediaStream = null;
  }
  if (audioContext) {
    try { audioContext.close(); } catch {}
    audioContext = null;
  }

  currentTabId = null;
  currentTabTitle = '';
}

initAndConnect();
