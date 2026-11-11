/**
 * MixerOnline Audio Bridge - Background Service Worker
 * Supports Chromium Manifest V3 browsers (Chrome, Brave, Edge, Opera, Vivaldi, Arc).
 */

const INTERNAL_URL_PREFIXES = [
  'brave://',
  'chrome://',
  'chrome-extension://',
  'extension://',
  'devtools://',
  'edge://',
  'opera://',
  'vivaldi://',
  'arc://',
  'about:'
];

let activeCapturingTabId = null;
let creatingOffscreenPromise = null;

function isCapturableTab(tab) {
  if (!tab || !tab.url) {
    return false;
  }

  const normalizedUrl = tab.url.toLowerCase();
  return !INTERNAL_URL_PREFIXES.some((prefix) => normalizedUrl.startsWith(prefix));
}

async function hasOffscreenDocument() {
  if (typeof chrome.runtime.getContexts === 'function') {
    const existingContexts = await chrome.runtime.getContexts({
      contextTypes: ['OFFSCREEN_DOCUMENT']
    });
    return existingContexts.length > 0;
  }

  if (typeof self.clients !== 'undefined') {
    const matchedClients = await self.clients.matchAll();
    const offscreenUrl = chrome.runtime.getURL('offscreen.html');
    return matchedClients.some((client) => client.url === offscreenUrl);
  }

  return false;
}

async function ensureOffscreenDocument() {
  if (creatingOffscreenPromise) {
    return creatingOffscreenPromise;
  }

  creatingOffscreenPromise = (async () => {
    if (await hasOffscreenDocument()) {
      return;
    }

    await chrome.offscreen.createDocument({
      url: 'offscreen.html',
      reasons: ['AUDIO_PLAYBACK', 'USER_MEDIA'],
      justification: 'MixerOnline tab audio capture and streaming'
    });
  })();

  try {
    await creatingOffscreenPromise;
  } finally {
    creatingOffscreenPromise = null;
  }
}

chrome.runtime.onStartup.addListener(async () => {
  await ensureOffscreenDocument();
});

chrome.runtime.onInstalled.addListener(async () => {
  await ensureOffscreenDocument();
});

ensureOffscreenDocument().catch(console.error);

async function queryTabs() {
  const rawTabs = await chrome.tabs.query({});
  return rawTabs
    .filter(isCapturableTab)
    .map((tab) => ({
      id: tab.id,
      title: tab.title || 'Untitled Tab',
      url: tab.url,
      audible: Boolean(tab.audible) || tab.id === activeCapturingTabId,
      favIconUrl: tab.favIconUrl || ''
    }));
}

async function sendTabList() {
  try {
    await ensureOffscreenDocument();
    const tabs = await queryTabs();
    chrome.runtime.sendMessage({
      action: 'SEND_TAB_LIST',
      tabs
    }).catch(() => {});
  } catch (err) {
    console.error('[MixerOnline BG] Error sending tab list:', err);
  }
}

chrome.tabs.onCreated.addListener(() => {
  sendTabList();
});

chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === activeCapturingTabId) {
    activeCapturingTabId = null;
    chrome.runtime.sendMessage({ action: 'STOP_CAPTURE' }).catch(() => {});
  }
  sendTabList();
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === 'complete' || changeInfo.title || changeInfo.audible !== undefined) {
    sendTabList();
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'REQUEST_TABS') {
    sendTabList().then(() => sendResponse({ success: true }));
    return true;
  }

  if (message.action === 'GET_TABS_FOR_POPUP') {
    ensureOffscreenDocument()
      .catch(() => {})
      .then(() => queryTabs())
      .then((tabs) => sendResponse({ tabs }));
    return true;
  }

  if (message.action === 'CAPTURE_TAB_REQUEST') {
    const targetTabId = message.tabId;
    (async () => {
      try {
        await ensureOffscreenDocument();

        const state = await chrome.runtime.sendMessage({ action: 'GET_STATE' }).catch(() => null);
        if (state && state.capturing && state.currentTabId === targetTabId) {
          sendResponse({ success: true, alreadyCapturing: true });
          return;
        }

        if (state && state.capturing) {
          await chrome.runtime.sendMessage({ action: 'STOP_CAPTURE' }).catch(() => {});
          await new Promise((resolve) => setTimeout(resolve, 80));
        }

        const tab = await chrome.tabs.get(targetTabId);

        chrome.tabCapture.getMediaStreamId({ targetTabId }, (streamId) => {
          if (chrome.runtime.lastError || !streamId) {
            console.error('[MixerOnline BG] tabCapture error:', chrome.runtime.lastError?.message);
            sendResponse({ success: false, error: chrome.runtime.lastError?.message });
            return;
          }

          activeCapturingTabId = targetTabId;

          chrome.runtime.sendMessage({
            action: 'START_CAPTURE_WITH_STREAM',
            streamId,
            tabId: targetTabId,
            title: tab.title || `Tab #${targetTabId}`
          }, (resp) => {
            if (chrome.runtime.lastError) {
              activeCapturingTabId = null;
              sendResponse({ success: false, error: chrome.runtime.lastError.message });
              return;
            }
            activeCapturingTabId = resp && resp.success ? targetTabId : null;
            sendResponse(resp || { success: false, error: 'Offscreen capture failed' });
          });
        });
      } catch (err) {
        console.error('[MixerOnline BG] Failed to initiate capture:', err);
        sendResponse({ success: false, error: err.message });
      }
    })();
    return true;
  }

  if (message.action === 'CAPTURE_STARTED') {
    activeCapturingTabId = message.tabId;
    return false;
  }

  if (message.action === 'CAPTURE_STOPPED') {
    activeCapturingTabId = null;
    return false;
  }
});
