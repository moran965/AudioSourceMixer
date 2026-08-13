import {
  NATIVE_HOST_NAME,
  PROTOCOL_VERSION,
  browserName,
  sanitizeOrigin,
  sourceId,
  validateAudioCommand
} from '../shared/protocol.js';
import {
  LEGACY_OUTPUT_MAPPINGS_KEY,
  OUTPUT_MAPPINGS_KEY,
  PENDING_OUTPUT_AUTHORIZATION_KEY,
  findOutputMapping,
  markOutputMappingStale,
  migrateOutputMappingStore,
  pendingAuthorizationState,
  queueAuthorizationRequest,
  saveOutputMapping
} from '../output-authorization/mappings.js';
import { shouldConnectNativeOnRecovery } from './lifecycle-policy.js';
import { createEqualizerPreset } from '../shared/equalizer.js';

const OFFSCREEN_URL = 'offscreen/offscreen.html';
const AUTHORIZATION_URL = 'output-authorization/authorize.html';
const NATIVE_RETRY_DELAY_MS = 5000;
let nativePort = null;
let nativeReady = null;

chrome.action.onClicked.addListener((tab) => runEventTask('切换标签页增强', () => toggleTab(tab)));
chrome.tabs.onRemoved.addListener((tabId) => runEventTask('清理已关闭标签页', () => stopTab(browserName(), tabId, true)));
chrome.tabCapture.onStatusChanged.addListener((info) => {
  if (info.status === 'stopped' || info.status === 'error')
    runEventTask('同步标签页捕获状态', () => stopTab(browserName(), info.tabId, true));
});
chrome.runtime.onStartup.addListener(() => runEventTask('浏览器启动恢复', () => recoverRuntimeState('runtime-startup')));
chrome.runtime.onInstalled.addListener((details) => runEventTask('扩展安装或更新', () => handleInstalled(details)));

chrome.runtime.onMessage.addListener((message) => {
  if (message.type === 'offscreen.level') runEventTask('转发标签页电平', () => forwardLevel(message));
  else if (message.type === 'offscreen.tabEnded')
    runEventTask('清理已结束标签页', () => stopTab(message.browser || browserName(), message.tabId, true));
  else if (message.type === 'offscreen.outputChanged')
    runEventTask('同步浏览器输出状态', () => forwardOutputStatus(message));
  else if (message.type === 'authorization.mappingChanged')
    runEventTask('重新验证输出映射', () => revalidateActiveOutputs(message));
});

runEventTask('service worker 状态恢复', () => recoverRuntimeState('service-worker-evaluated'));

function runEventTask(name, action) {
  executeEventTask(name, action);
}

async function executeEventTask(name, action) {
  try { await action(); }
  catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error(`[Audio Source Mixer] ${name}失败`, error);
    try { await chrome.storage.session.set({ lastExtensionError: `${name}：${message}` }); }
    catch (storageError) { console.error('[Audio Source Mixer] 无法记录扩展错误', storageError); }
  }
}

async function handleInstalled() {
  await recoverRuntimeState('extension-installed');
}

async function toggleTab(tab) {
  if (!Number.isInteger(tab.id)) return;
  const browser = browserName();
  await withSourceLock(sourceId(browser, tab.id), async () => {
    const state = await getTabState(browser, tab.id);
    if (state?.state === 'starting' || state?.state === 'stopping') return;
    if (state?.state === 'active') await stopTabCore(browser, tab.id, false, state);
    else await startTabCore(browser, tab);
  });
}

async function startTabCore(browser, tab) {
  const tabId = tab.id;
  const previous = await getTabState(browser, tabId);
  const generation = (previous?.generation || 0) + 1;
  await setTabState(browser, tabId, { ...previous, browser, tabId, state: 'starting', generation });
  await updateBadge();
  try {
    if (!await ensureNativeReady()) {
      await chrome.storage.session.set({ nativeStatus: '请先打开 Audio Source Mixer' });
      throw new Error('请先打开 Audio Source Mixer');
    }
    await ensureOffscreenDocument();
    const existing = await listOffscreenGraphs();
    const existingGraph = existing.find((graph) => graph.browser === browser && graph.tabId === tabId);
    if (existingGraph) {
      await setTabState(browser, tabId, { ...existingGraph, browser, tabId, state: 'active', generation: existingGraph.generation || generation });
      await registerStateWithNative({ ...existingGraph, browser, tabId, state: 'active' });
      return;
    }
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
    const metadata = {
      state: 'active', browser, tabId, generation,
      title: tab.title || '未命名标签页', origin: sanitizeOrigin(tab.url),
      volume: 1, balance: 0, muted: false,
      equalizer: createEqualizerPreset('off'),
      outputDeviceId: '', outputDeviceName: '', outputStatus: '系统默认',
      correlationId: '', commandGeneration: 0
    };
    const response = await chrome.runtime.sendMessage({ type: 'audio.start', streamId, ...metadata });
    if (!response?.ok) throw new Error(response?.error || 'Offscreen audio graph did not start.');
    Object.assign(metadata, response);
    delete metadata.ok;
    await setTabState(browser, tabId, metadata);
    await registerStateWithNative(metadata);
  } catch (error) {
    await removeTabState(browser, tabId);
    console.error('Could not enable tab audio control:', error);
  } finally {
    await updateBadge();
  }
}

async function stopTab(browser, tabId, streamAlreadyEnded) {
  if (!Number.isInteger(tabId)) return;
  await withSourceLock(sourceId(browser, tabId), async () => {
    const state = await getTabState(browser, tabId);
    if (!state || state.state === 'stopping') return;
    await stopTabCore(browser, tabId, streamAlreadyEnded, state);
  });
}

async function stopTabCore(browser, tabId, streamAlreadyEnded, state) {
  await setTabState(browser, tabId, { ...state, state: 'stopping' });
  try {
    if (!streamAlreadyEnded) await chrome.runtime.sendMessage({ type: 'audio.stop', browser, tabId });
  } catch (error) {
    console.warn('Offscreen graph was already unavailable:', error.message);
  } finally {
    await postNative({
      protocolVersion: PROTOCOL_VERSION, type: 'tab.unregister', browser, tabId,
      sourceId: sourceId(browser, tabId)
    });
    await removeTabState(browser, tabId);
    await updateBadge();
    await closeOffscreenIfIdle();
  }
}

async function closeOffscreenIfIdle() {
  const states = await getStates();
  if (Object.values(states).some((state) => ['starting', 'active', 'stopping'].includes(state.state))) return;
  const contexts = await getOffscreenContexts();
  if (contexts.length > 0) await chrome.offscreen.closeDocument();
}

async function forwardLevel(message) {
  const browser = message.browser || browserName();
  const state = await getTabState(browser, message.tabId);
  if (!state || state.state !== 'active') return;
  await postNative({
    protocolVersion: PROTOCOL_VERSION, type: 'tab.update', browser, tabId: message.tabId,
    captureState: 'active', sourceId: sourceId(browser, message.tabId),
    volume: state.volume, balance: state.balance, muted: state.muted, peak: message.peak,
    equalizer: state.equalizer || createEqualizerPreset('off'),
    outputDeviceId: state.outputDeviceId || '', outputDeviceName: state.outputDeviceName || '',
    outputStatus: state.outputStatus || '系统默认'
  });
}

async function forwardOutputStatus(message) {
  const browser = message.browser || browserName();
  const key = sourceId(browser, message.tabId);
  await withSourceLock(key, async () => {
    const state = await getTabState(browser, message.tabId);
    if (!state || state.state !== 'active') return;
    const updated = { ...state, ...message, browser, tabId: message.tabId };
    delete updated.type;
    await reconcileMappingResult(updated);
    await setTabState(browser, message.tabId, updated);
    await publishOutputState(message.tabId, updated, 'devicechange');
  });
}

async function ensureOffscreenDocument() {
  if ((await getOffscreenContexts()).length > 0) return;
  await chrome.offscreen.createDocument({
    url: OFFSCREEN_URL,
    reasons: ['USER_MEDIA'],
    justification: 'Keep user-enabled tab audio graphs active while the service worker sleeps.'
  });
}

async function getOffscreenContexts() {
  return chrome.runtime.getContexts({
    contextTypes: ['OFFSCREEN_DOCUMENT'],
    documentUrls: [chrome.runtime.getURL(OFFSCREEN_URL)]
  });
}

async function listOffscreenGraphs() {
  if ((await getOffscreenContexts()).length === 0) return [];
  try { return (await chrome.runtime.sendMessage({ type: 'audio.list' }))?.graphs || []; }
  catch { return []; }
}

async function ensureNativePort() {
  if (nativePort) return nativePort;
  const { nativeRetryAfter = 0 } = await chrome.storage.session.get('nativeRetryAfter');
  if (Date.now() < nativeRetryAfter) return null;
  try {
    const port = chrome.runtime.connectNative(NATIVE_HOST_NAME);
    nativePort = port;
    nativeReady = createDeferred();
    port.postMessage({ protocolVersion: PROTOCOL_VERSION, type: 'bridge.hello', browser: browserName(),
      extensionVersion: chrome.runtime.getManifest().version });
    port.onMessage.addListener((message) => {
      runEventTask('处理桌面命令', async () => {
        try { await handleNativeMessage(message); }
        catch (error) {
          await chrome.storage.session.set({ nativeStatus: `Native 命令失败：${error.message}` });
          await updateBadge();
          throw error;
        }
      });
    });
    port.onDisconnect.addListener(() => {
      runEventTask('处理桌面连接断开', async () => {
        nativeReady?.resolve(false);
        nativeReady = null;
        nativePort = null;
        await chrome.storage.session.set({ nativeRetryAfter: Date.now() + NATIVE_RETRY_DELAY_MS, nativeStatus: '请先打开 Audio Source Mixer' });
        if (chrome.runtime.lastError) console.warn('Native host disconnected:', chrome.runtime.lastError.message);
        await updateBadge();
      });
    });
    runEventTask('注册活动增强标签页', () => registerAllActiveTabs(port));
    return port;
  } catch (error) {
    await chrome.storage.session.set({ nativeRetryAfter: Date.now() + NATIVE_RETRY_DELAY_MS, nativeStatus: `Native Host 不可用：${error.message}` });
    console.warn('Native host unavailable:', error.message);
    return null;
  }
}

async function ensureNativeReady() {
  const port = await ensureNativePort();
  if (!port) return false;
  if (!nativeReady) return true;
  return Promise.race([
    nativeReady.promise,
    new Promise((resolve) => setTimeout(() => resolve(false), 1500))
  ]);
}

function createDeferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}

async function registerAllActiveTabs(port) {
  const states = await getStates();
  for (const state of Object.values(states))
    if (state.state === 'active') port.postMessage(createRegisterMessage(state));
}

async function registerStateWithNative(state) {
  await postNative(createRegisterMessage(state));
}

function createRegisterMessage(state) {
  return {
    protocolVersion: PROTOCOL_VERSION, type: 'tab.register', browser: state.browser, tabId: state.tabId,
    title: state.title, origin: state.origin, captureState: 'active', sourceId: sourceId(state.browser, state.tabId),
    volume: state.volume ?? 1, balance: state.balance ?? 0, muted: Boolean(state.muted), peak: 0,
    equalizer: state.equalizer || createEqualizerPreset('off'),
    outputDeviceId: state.outputDeviceId || '', outputDeviceName: state.outputDeviceName || '',
    outputStatus: state.outputStatus || '系统默认', effectiveSinkId: state.effectiveSinkId || '',
    effectiveSinkLabel: state.effectiveSinkLabel || '', routingState: state.routingState || 'Default',
    generation: state.commandGeneration || state.generation || 0, correlationId: state.correlationId || ''
  };
}

async function postNative(message) {
  try { (await ensureNativePort())?.postMessage(message); }
  catch (error) {
    nativePort = null;
    await chrome.storage.session.set({ nativeRetryAfter: Date.now() + NATIVE_RETRY_DELAY_MS, nativeStatus: `Native Host 不可用：${error.message}` });
    console.warn('Native host unavailable:', error.message);
  }
}

async function handleNativeMessage(message) {
  if (message.type === 'bridge.status') {
    nativeReady?.resolve(!message.error);
    if (!message.error) nativeReady = null;
    await chrome.storage.session.set({ nativeStatus: message.error || `Native Host 协议 ${PROTOCOL_VERSION} 已连接` });
    await updateBadge();
    return;
  }
  if (message.type === 'bridge.openOptions') {
    await openAuthorizationPageOnce();
    return;
  }
  if (message.type === 'bridge.clearMappings') {
    const stored = await loadOutputMappingStore();
    const mappings = Object.fromEntries(Object.entries(stored.mappings)
      .filter(([, mapping]) => mapping.browser !== browserName()));
    await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: { ...stored, mappings } });
    await revalidateActiveOutputs({ browser: browserName() });
    return;
  }
  if (message.type === 'tab.stop') {
    await stopTab(message.browser || browserName(), message.tabId, false);
    return;
  }
  if (message.type !== 'tab.setAudio') return;

  const audio = validateAudioCommand(message);
  const browser = message.browser || browserName();
  const key = sourceId(browser, message.tabId);
  await withSourceLock(key, async () => {
    const state = await getTabState(browser, message.tabId);
    if (!state || state.state !== 'active') throw new Error(`Tab ${message.tabId} is not actively captured.`);
    if (audio.generation < (state.commandGeneration || 0)) {
      await publishOutputState(message.tabId, state, 'stale-command');
      return;
    }
    let mapping = audio.forceAuthorization ? null : await resolveOutputMapping(browser, audio.outputDeviceId, audio.outputDeviceName);
    const desired = { ...state, ...audio, browser, tabId: message.tabId, commandGeneration: audio.generation };
    await setTabState(browser, message.tabId, desired);
    if (audio.outputDeviceId && !mapping) {
      await requestOutputAuthorization(browser, message.tabId, audio);
      const pending = pendingAuthorizationState(desired);
      await setTabState(browser, message.tabId, pending);
      await publishOutputState(message.tabId, pending, 'none');
      if (shouldOpenAuthorization(audio)) openAuthorizationPageInBackground();
    }
    const response = await chrome.runtime.sendMessage({
      type: 'audio.update', browser, tabId: message.tabId, ...audio,
      browserOutputDeviceId: mapping?.deviceId || '',
      browserOutputDeviceLabel: mapping?.browserLabel || '',
      browserGroupId: mapping?.browserGroupId || ''
    });
    if (!response) throw new Error('Offscreen audio update returned no result.');
    if (response.staleIgnored) return;
    const updated = { ...desired, ...response, commandGeneration: audio.generation };
    delete updated.ok;
    await reconcileMappingResult(updated);
    if (updated.mappingStale) await requestOutputAuthorization(browser, message.tabId, audio);
    await setTabState(browser, message.tabId, updated);
    await publishOutputState(message.tabId, updated, mapping?.matchKind || 'none');
    if (updated.mappingStale && shouldOpenAuthorization(audio)) openAuthorizationPageInBackground();
  });
}

function shouldOpenAuthorization(audio) {
  return audio.forceAuthorization || audio.requestSource === 'User';
}

async function resolveOutputMapping(browser, windowsEndpointId, windowsEndpointName) {
  if (!windowsEndpointId) return null;
  return findOutputMapping(await loadOutputMappingStore(), browser, windowsEndpointId, windowsEndpointName);
}

async function loadOutputMappingStore() {
  const stored = await chrome.storage.local.get([OUTPUT_MAPPINGS_KEY, LEGACY_OUTPUT_MAPPINGS_KEY]);
  const migrated = migrateOutputMappingStore(stored[OUTPUT_MAPPINGS_KEY], stored[LEGACY_OUTPUT_MAPPINGS_KEY]);
  if (!stored[OUTPUT_MAPPINGS_KEY]) await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: migrated });
  return migrated;
}

async function reconcileMappingResult(state) {
  if (!state.outputDeviceId) return;
  let mappings = await loadOutputMappingStore();
  if (state.mappingRebound) {
    mappings = saveOutputMapping(mappings, {
      ...state.mappingRebound,
      browser: state.browser,
      windowsEndpointId: state.outputDeviceId,
      windowsEndpointName: state.outputDeviceName || state.outputDeviceId,
      updatedAt: new Date().toISOString()
    });
    await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: mappings });
  } else if (state.mappingStale) {
    mappings = markOutputMappingStale(mappings, state.browser, state.outputDeviceId);
    await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: mappings });
  }
}

async function requestOutputAuthorization(browser, tabId, audio) {
  const request = {
    browser, tabId, correlationId: audio.correlationId, generation: audio.generation,
    windowsEndpointId: audio.outputDeviceId,
    windowsEndpointName: audio.outputDeviceName || audio.outputDeviceId,
    outputDevices: audio.outputDevices,
    requestedAt: new Date().toISOString()
  };
  await withStorageLock('authorization-queue', async () => {
    const stored = await chrome.storage.session.get(PENDING_OUTPUT_AUTHORIZATION_KEY);
    const previous = stored[PENDING_OUTPUT_AUTHORIZATION_KEY] || {};
    const queue = queueAuthorizationRequest(previous, request);
    await chrome.storage.session.set({ [PENDING_OUTPUT_AUTHORIZATION_KEY]: queue });
  });
}

function openAuthorizationPageInBackground() {
  runEventTask('打开输出授权页', openAuthorizationPageOnce);
}

async function openAuthorizationPageOnce() {
  const url = chrome.runtime.getURL(AUTHORIZATION_URL);
  const tabs = await chrome.tabs.query({});
  if (tabs.some((tab) => tab.url === url)) return;
  await chrome.runtime.openOptionsPage();
}

async function revalidateActiveOutputs(message = {}) {
  const states = await getStates();
  for (const state of Object.values(states)) {
    if (state.state !== 'active') continue;
    if (message.browser && state.browser !== message.browser) continue;
    if (message.windowsEndpointId && state.outputDeviceId !== message.windowsEndpointId) continue;
    try {
      await withSourceLock(sourceId(state.browser, state.tabId), async () => {
        const latest = await getTabState(state.browser, state.tabId);
        if (!latest || latest.state !== 'active') return;
        const mapping = await resolveOutputMapping(latest.browser, latest.outputDeviceId, latest.outputDeviceName);
        const response = await chrome.runtime.sendMessage({
          type: 'audio.update', browser: latest.browser, tabId: latest.tabId,
          volume: latest.volume, balance: latest.balance, muted: latest.muted,
          equalizer: latest.equalizer || createEqualizerPreset('off'),
          outputDeviceId: latest.outputDeviceId || '', outputDeviceName: latest.outputDeviceName || '',
          correlationId: latest.correlationId || crypto.randomUUID(), generation: latest.commandGeneration || 0,
          browserOutputDeviceId: mapping?.deviceId || '', browserOutputDeviceLabel: mapping?.browserLabel || '',
          browserGroupId: mapping?.browserGroupId || ''
        });
        if (!response || response.staleIgnored) return;
        const updated = { ...latest, ...response };
        delete updated.ok;
        await reconcileMappingResult(updated);
        await setTabState(latest.browser, latest.tabId, updated);
        await publishOutputState(latest.tabId, updated, mapping?.matchKind || 'none');
      });
    } catch (error) { console.error('Could not revalidate output mapping:', error); }
  }
}

async function publishOutputState(tabId, state, mappingMatchKind) {
  await postNative({
    protocolVersion: PROTOCOL_VERSION, type: 'tab.update', browser: state.browser, tabId,
    sourceId: sourceId(state.browser, tabId),
    equalizer: state.equalizer || createEqualizerPreset('off'),
    outputDeviceId: state.outputDeviceId || '', outputDeviceName: state.outputDeviceName || '',
    outputStatus: state.outputStatus || '', correlationId: state.correlationId || '',
    generation: state.commandGeneration || state.generation || 0,
    browserDeviceId: state.browserDeviceId || '', browserDeviceLabel: state.browserDeviceLabel || '',
    browserGroupId: state.browserGroupId || '', effectiveSinkId: state.effectiveSinkId || '',
    effectiveSinkLabel: state.effectiveSinkLabel || '', routingState: state.routingState || 'Failed',
    setSinkDurationMs: state.setSinkDurationMs ?? 0, setSinkIdSupported: Boolean(state.setSinkIdSupported),
    error: state.error || (mappingMatchKind === 'none' && state.outputDeviceId ? 'Browser authorization is required.' : null)
  });
}

async function recoverRuntimeState(reason) {
  try {
    const browser = browserName();
    const states = await getStates();
    const graphs = await listOffscreenGraphs();
    const captured = typeof chrome.tabCapture.getCapturedTabs === 'function'
      ? await chrome.tabCapture.getCapturedTabs() : [];
    const graphKeys = new Set();
    for (const graph of graphs) {
      const key = sourceId(graph.browser || browser, graph.tabId);
      graphKeys.add(key);
      const previous = states[key] || {};
      states[key] = { ...previous, ...graph, browser: graph.browser || browser, tabId: graph.tabId, state: 'active' };
    }
    for (const [key, state] of Object.entries(states)) {
      if (state.state === 'starting' || state.state === 'active') {
        const captureExists = captured.some((capture) => capture.tabId === state.tabId && capture.status === 'active');
        if (!graphKeys.has(key) && !captureExists) delete states[key];
        else if (!graphKeys.has(key)) delete states[key];
      } else if (state.state === 'stopping') delete states[key];
    }
    await chrome.storage.session.set({ tabStates: states, recoveryStatus: `${reason}:${new Date().toISOString()}` });
    if (shouldConnectNativeOnRecovery(graphs)) await ensureNativeReady();
    await updateBadge();
  } catch (error) { console.warn('Runtime state recovery failed:', error.message); }
}

async function getStates() {
  const { tabStates = {} } = await chrome.storage.session.get('tabStates');
  return tabStates;
}

async function getTabState(browser, tabId) {
  return (await getStates())[sourceId(browser, tabId)];
}

async function setTabState(browser, tabId, value) {
  await mutateStates((states) => { states[sourceId(browser, tabId)] = value; });
}

async function removeTabState(browser, tabId) {
  await mutateStates((states) => { delete states[sourceId(browser, tabId)]; });
}

async function mutateStates(change) {
  await withStorageLock('tab-states', async () => {
    const states = await getStates();
    change(states);
    await chrome.storage.session.set({ tabStates: states });
  });
}

async function withSourceLock(key, action) {
  return withStorageLock(`source:${key}`, action);
}

async function withStorageLock(key, action) {
  if (globalThis.navigator?.locks?.request)
    return navigator.locks.request(`audio-source-mixer:${key}`, { mode: 'exclusive' }, action);
  return action();
}

async function updateBadge() {
  const states = await getStates();
  const { nativeStatus = '' } = await chrome.storage.session.get('nativeStatus');
  const count = Object.values(states).filter((state) => state.state === 'active').length;
  const hasNativeError = nativeStatus && !nativeStatus.includes('已连接');
  await chrome.action.setBadgeBackgroundColor({ color: hasNativeError ? '#B45309' : '#2563EB' });
  await chrome.action.setBadgeText({ text: hasNativeError ? '!' : count > 0 ? String(count) : '' });
  await chrome.action.setTitle({ title: hasNativeError ? nativeStatus : count > 0 ? `已增强 ${count} 个标签页；点击切换当前标签页` : '启用当前标签页的独立音频控制' });
}
