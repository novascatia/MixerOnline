/**
 * MixerOnline Extension Popup Logic
 */

const DEFAULT_BRIDGE_PORT = 49876;
const MIN_BRIDGE_PORT = 1025;
const MAX_BRIDGE_PORT = 65534;
const BRIDGE_PORT_STORAGE_KEY = 'bridgePort';

function parseValidPort(rawValue) {
  if (typeof rawValue === 'number') {
    return Number.isInteger(rawValue) && rawValue >= MIN_BRIDGE_PORT && rawValue <= MAX_BRIDGE_PORT
      ? rawValue
      : null;
  }

  const trimmed = String(rawValue ?? '').trim();
  if (!/^\d+$/.test(trimmed)) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isInteger(parsed) && parsed >= MIN_BRIDGE_PORT && parsed <= MAX_BRIDGE_PORT
    ? parsed
    : null;
}

function getTabHostLabel(rawUrl) {
  if (!rawUrl) {
    return '';
  }

  try {
    const parsed = new URL(rawUrl);
    if (parsed.hostname) {
      return parsed.hostname;
    }
    if (parsed.protocol === 'file:') {
      return 'local file';
    }
    return parsed.protocol.replace(':', '');
  } catch {
    return '';
  }
}

function sendRuntimeMessage(payload) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(payload, (response) => {
        if (chrome.runtime.lastError) {
          resolve(null);
          return;
        }
        resolve(response ?? null);
      });
    } catch {
      resolve(null);
    }
  });
}

document.addEventListener('DOMContentLoaded', async () => {
  const statusBadge = document.getElementById('statusBadge');
  const statusText = document.getElementById('statusText');
  const activeBox = document.getElementById('activeBox');
  const activeTitle = document.getElementById('activeTitle');
  const btnStopCapture = document.getElementById('btnStopCapture');
  const tabSelect = document.getElementById('tabSelect');
  const tabCount = document.getElementById('tabCount');
  const btnCaptureSelected = document.getElementById('btnCaptureSelected');
  const passThroughToggle = document.getElementById('passThroughToggle');
  const bridgePortInput = document.getElementById('bridgePortInput');
  const btnApplyPort = document.getElementById('btnApplyPort');
  const portFeedback = document.getElementById('portFeedback');

  let isPortDirty = false;
  let portFeedbackTimer = null;

  function clearPortFeedback() {
    if (portFeedbackTimer) {
      clearTimeout(portFeedbackTimer);
      portFeedbackTimer = null;
    }
    portFeedback.textContent = '';
    portFeedback.className = 'port-feedback';
  }

  function showPortFeedback(text, kind) {
    clearPortFeedback();
    portFeedback.textContent = text;
    portFeedback.className = `port-feedback ${kind}`;
    portFeedbackTimer = setTimeout(() => {
      portFeedback.textContent = '';
      portFeedback.className = 'port-feedback';
      portFeedbackTimer = null;
    }, 2200);
  }

  async function loadStoredBridgePort() {
    try {
      if (chrome?.storage?.local) {
        const stored = await chrome.storage.local.get(BRIDGE_PORT_STORAGE_KEY);
        const validPort = parseValidPort(stored?.[BRIDGE_PORT_STORAGE_KEY]);
        bridgePortInput.value = String(validPort ?? DEFAULT_BRIDGE_PORT);
      }
    } catch (err) {
      console.warn('Failed to read stored bridgePort:', err);
    }
  }

  async function refreshState() {
    try {
      const response = await sendRuntimeMessage({ action: 'GET_STATE' });
      if (!response) {
        return;
      }

      if (response.connected) {
        statusBadge.classList.add('connected');
        statusText.textContent = 'Mixer Connected';
      } else {
        statusBadge.classList.remove('connected');
        statusText.textContent = 'Mixer Offline';
      }

      if (response.capturing && response.currentTabId) {
        activeBox.classList.add('visible');
        activeTitle.textContent = response.currentTabTitle || `Tab #${response.currentTabId}`;
      } else {
        activeBox.classList.remove('visible');
      }

      passThroughToggle.checked = Boolean(response.passThroughEnabled);

      const statePort = parseValidPort(response.bridgePort);
      if (statePort !== null && !isPortDirty && document.activeElement !== bridgePortInput) {
        bridgePortInput.value = String(statePort);
      }
    } catch (err) {
      console.warn('Failed to get state:', err);
    }
  }

  async function loadTabs() {
    try {
      const response = await sendRuntimeMessage({ action: 'GET_TABS_FOR_POPUP' });
      if (!response || !Array.isArray(response.tabs)) {
        return;
      }

      const { tabs } = response;
      tabCount.textContent = String(tabs.length);

      const prevValue = tabSelect.value;
      const defaultOpt = document.createElement('option');
      defaultOpt.value = '';
      defaultOpt.textContent = 'Select a tab to stream...';

      const options = [defaultOpt];
      tabs.forEach((tab) => {
        const opt = document.createElement('option');
        opt.value = String(tab.id);
        const prefix = tab.audible ? '[Audio] ' : '';
        const title = tab.title || 'Untitled Tab';
        const host = getTabHostLabel(tab.url);
        opt.textContent = host ? `${prefix}${title} (${host})` : `${prefix}${title}`;
        options.push(opt);
      });

      tabSelect.replaceChildren(...options);

      if (prevValue && tabs.some((tab) => String(tab.id) === prevValue)) {
        tabSelect.value = prevValue;
      }
    } catch (err) {
      console.warn('Failed to load tabs:', err);
    }
  }

  async function applyBridgePort() {
    const validPort = parseValidPort(bridgePortInput.value);
    if (validPort === null) {
      showPortFeedback('1025-65534', 'error');
      bridgePortInput.focus();
      return;
    }

    btnApplyPort.disabled = true;
    try {
      const response = await sendRuntimeMessage({
        action: 'SET_BRIDGE_PORT',
        port: validPort
      });

      if (!response) {
        if (chrome?.storage?.local) {
          await chrome.storage.local.set({ [BRIDGE_PORT_STORAGE_KEY]: validPort });
        }
        await sendRuntimeMessage({ action: 'REQUEST_TABS' });
        isPortDirty = false;
        bridgePortInput.value = String(validPort);
        showPortFeedback('Saved', 'saved');
      } else if (response.success) {
        isPortDirty = false;
        bridgePortInput.value = String(response.bridgePort ?? validPort);
        showPortFeedback('Saved', 'saved');
      } else {
        showPortFeedback('Invalid', 'error');
      }

      setTimeout(refreshState, 250);
    } catch (err) {
      console.warn('Failed to update bridge port:', err);
      showPortFeedback('Failed', 'error');
    } finally {
      btnApplyPort.disabled = false;
    }
  }

  btnCaptureSelected.addEventListener('click', async () => {
    const selectedTabId = Number.parseInt(tabSelect.value, 10);
    if (!selectedTabId) {
      return;
    }

    await sendRuntimeMessage({
      action: 'CAPTURE_TAB_REQUEST',
      tabId: selectedTabId
    });
    setTimeout(refreshState, 300);
  });

  btnStopCapture.addEventListener('click', async () => {
    await sendRuntimeMessage({ action: 'STOP_CAPTURE' });
    await refreshState();
  });

  passThroughToggle.addEventListener('change', async () => {
    await sendRuntimeMessage({
      action: 'SET_PASSTHROUGH',
      enabled: passThroughToggle.checked
    });
  });

  bridgePortInput.addEventListener('input', () => {
    isPortDirty = true;
    clearPortFeedback();
  });

  bridgePortInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      applyBridgePort();
    }
  });

  btnApplyPort.addEventListener('click', () => {
    applyBridgePort();
  });

  await loadStoredBridgePort();
  await loadTabs();
  await refreshState();

  const pollTimer = setInterval(refreshState, 1500);
  window.addEventListener('unload', () => clearInterval(pollTimer));
});
