import { browserName } from '../shared/protocol.js';
import { createAuthorizationController } from './authorization-controller.js';
import { compareDeviceNames, createOutputMappingCandidate, playOutputTestTone } from './authorization-workflow.js';
import {
  LEGACY_OUTPUT_MAPPINGS_KEY,
  OUTPUT_MAPPINGS_KEY,
  PENDING_OUTPUT_AUTHORIZATION_KEY,
  clearBrowserOutputMappings,
  mappingDisplayState,
  migrateOutputMappingStore,
  outputMappings,
  pendingAuthorizationRequests,
  physicalOutputDevices,
  removeOutputMapping
} from './mappings.js';

const browser = browserName();
const elements = Object.fromEntries([...document.querySelectorAll('[id]')].map((element) => [element.id, element]));
let pendingRequest = null;
let pendingRequests = [];
let visibleOutputs = [];
let candidate = null;
let mismatchConfirmationArmed = false;
let operationGeneration = 0;
let pendingRefreshRequested = false;
let pendingRefreshPromise = null;
let mappingRefreshRequested = false;
let mappingRefreshPromise = null;
let deviceRefreshRequested = false;

const authorizationController = createAuthorizationController({
  browser,
  loadMappingStore,
  localStorage: chrome.storage.local,
  sessionStorage: chrome.storage.session,
  sendMessage: (message) => chrome.runtime.sendMessage(message)
});

elements.chooseOutput.addEventListener('click', () => runUiTask('选择输出设备', chooseOutputFromUserGesture));
elements.unlockDevices.addEventListener('click', () => runUiTask('读取兼容设备', unlockCompatibilityDevices));
elements.useFallback.addEventListener('click', () => runUiTask('选择兼容设备', selectFallbackCandidate));
elements.pendingTargets.addEventListener('change', () => runUiTask('切换授权请求',
  () => selectPendingRequest(elements.pendingTargets.value)));
elements.testCandidate.addEventListener('click', () => runUiTask('播放候选测试声音', testCandidate));
elements.confirmCandidate.addEventListener('click', () => runUiTask('确认输出映射', confirmCandidate));
elements.retryNotification.addEventListener('click', () => runUiTask('重新通知增强标签页', retryNotification));
elements.reselectCandidate.addEventListener('click', () => runUiTask('重新选择候选设备', resetCandidate));
elements.cancelCandidate.addEventListener('click', () => runUiTask('取消候选设备', cancelCandidate));
elements.clearAll.addEventListener('click', () => runUiTask('清除全部映射', clearAllMappings));
navigator.mediaDevices.addEventListener('devicechange', () => {
  if (authorizationController.confirmInFlight) { deviceRefreshRequested = true; return; }
  runUiTask('刷新输出设备', () => refreshVisibleDevices(true));
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[PENDING_OUTPUT_AUTHORIZATION_KEY])
    runUiTask('同步待授权请求', requestPendingRefresh);
  if (area === 'local' && (changes[OUTPUT_MAPPINGS_KEY] || changes[LEGACY_OUTPUT_MAPPINGS_KEY]))
    runUiTask('同步设备映射', requestMappingRefresh);
});

function runUiTask(name, action) {
  executeUiTask(name, action);
}

async function executeUiTask(name, action) {
  try { await action(); }
  catch (error) { reportUiError(name, error); }
}

function reportUiError(name, error) {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`[Audio Source Mixer] ${name}失败`, error);
  setStatus(`${name}失败：${message}。请重试；如果问题持续，请重新打开此页面。`);
}

async function loadMappingStore() {
  const stored = await chrome.storage.local.get([OUTPUT_MAPPINGS_KEY, LEGACY_OUTPUT_MAPPINGS_KEY]);
  const store = migrateOutputMappingStore(stored[OUTPUT_MAPPINGS_KEY], stored[LEGACY_OUTPUT_MAPPINGS_KEY]);
  if (!stored[OUTPUT_MAPPINGS_KEY]) {
    await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: store });
  }
  return store;
}

async function loadPendingRequests() {
  const stored = await chrome.storage.session.get(PENDING_OUTPUT_AUTHORIZATION_KEY);
  pendingRequests = pendingAuthorizationRequests(stored[PENDING_OUTPUT_AUTHORIZATION_KEY], browser);
  const previousId = pendingRequest?.windowsEndpointId;
  renderPendingTargets(previousId);
  const selected = pendingRequests.find((request) => request.windowsEndpointId === previousId) || pendingRequests[0] || null;
  if (selected) elements.pendingTargets.value = selected.windowsEndpointId;
  selectPendingRequest(selected?.windowsEndpointId || '');
}

function requestPendingRefresh() {
  pendingRefreshRequested = true;
  if (authorizationController.confirmInFlight) return Promise.resolve();
  if (pendingRefreshPromise) return pendingRefreshPromise;
  pendingRefreshPromise = (async () => {
    do {
      pendingRefreshRequested = false;
      await loadPendingRequests();
    } while (pendingRefreshRequested && !authorizationController.confirmInFlight);
  })().finally(() => { pendingRefreshPromise = null; });
  return pendingRefreshPromise;
}

function requestMappingRefresh() {
  mappingRefreshRequested = true;
  if (authorizationController.confirmInFlight) return Promise.resolve();
  if (mappingRefreshPromise) return mappingRefreshPromise;
  mappingRefreshPromise = (async () => {
    do {
      mappingRefreshRequested = false;
      await renderMappings();
    } while (mappingRefreshRequested && !authorizationController.confirmInFlight);
  })().finally(() => { mappingRefreshPromise = null; });
  return mappingRefreshPromise;
}

function renderPendingTargets(preferredId = '') {
  elements.pendingTargets.replaceChildren();
  for (const request of pendingRequests) {
    const option = document.createElement('option');
    option.value = request.windowsEndpointId;
    option.textContent = `${request.windowsEndpointName}（${Object.keys(request.waiters || {}).length} 个标签页等待）`;
    elements.pendingTargets.append(option);
  }
  elements.pendingTargets.disabled = pendingRequests.length === 0;
  const selected = pendingRequests.find((request) => request.windowsEndpointId === preferredId) || pendingRequests[0] || null;
  if (selected) elements.pendingTargets.value = selected.windowsEndpointId;
}

function selectPendingRequest(endpointId) {
  if (authorizationController.confirmInFlight) return;
  pendingRequest = pendingRequests.find((request) => request.windowsEndpointId === endpointId) || null;
  elements.targetName.textContent = pendingRequest?.windowsEndpointName || '当前没有等待授权的设备';
  elements.instruction.textContent = pendingRequest
    ? `接下来请选择与“${pendingRequest.windowsEndpointName}”相同的物理设备。确认后，等待此设备的全部标签页会分别重试。`
    : '从桌面程序为增强标签页选择非默认输出后，设备会出现在这里。';
  elements.chooseOutput.disabled = !pendingRequest;
  elements.unlockDevices.disabled = !pendingRequest;
  resetCandidate();
}

async function chooseOutputFromUserGesture() {
  try {
    requirePendingRequest();
    if (typeof navigator.mediaDevices.selectAudioOutput !== 'function') {
      elements.fallbackArea.classList.remove('hidden');
      setStatus('当前浏览器使用兼容流程。请点击“显示兼容设备列表”。');
      return;
    }
    const selected = await navigator.mediaDevices.selectAudioOutput();
    showCandidate(selected);
  } catch (error) {
    setStatus(`没有完成设备选择：${error.message}`);
  }
}

async function unlockCompatibilityDevices() {
  try {
    requirePendingRequest();
    let permissionStream;
    try { permissionStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false }); }
    finally { permissionStream?.getTracks().forEach((track) => track.stop()); }
    await refreshVisibleDevices(false);
    elements.fallbackArea.classList.remove('hidden');
    setStatus('设备访问已完成，临时媒体 track 已立即停止。请从列表中选择同一台物理设备。');
  } catch (error) { setStatus(`无法显示完整设备列表：${error.message}`); }
}

function selectFallbackCandidate() {
  if (authorizationController.confirmInFlight) return;
  const selected = visibleOutputs.find((device) => device.deviceId === elements.fallbackDevices.value);
  if (!selected) { setStatus('请先选择一个浏览器输出设备。'); return; }
  showCandidate(selected);
}

function showCandidate(selected) {
  if (authorizationController.confirmInFlight) return;
  requirePendingRequest();
  candidate = createOutputMappingCandidate(pendingRequest, selected, browser);
  mismatchConfirmationArmed = false;
  elements.candidateWindows.textContent = candidate.windowsEndpointName;
  elements.candidateBrowser.textContent = candidate.browserLabel || '浏览器未提供设备名称';
  elements.compatibility.textContent = candidate.compatibility.message;
  elements.compatibility.classList.toggle('warning', candidate.compatibility.level === 'warning');
  elements.confirmCandidate.textContent = '声音来自正确设备，确认保存';
  elements.candidatePanel.classList.remove('hidden');
  setStatus('候选映射尚未保存。请先播放测试声音。');
}

async function testCandidate() {
  if (!candidate) return;
  elements.testCandidate.disabled = true;
  try {
    await playOutputTestTone(candidate.deviceId);
    setStatus('测试声音播放完毕，临时音频上下文已关闭。请根据实际听到的位置确认。');
  } catch (error) { setStatus(`无法播放测试声音：${error.message}`); }
  finally { elements.testCandidate.disabled = false; }
}

async function confirmCandidate() {
  const candidateAtStart = candidate;
  const requestAtStart = pendingRequest;
  const operationToken = ++operationGeneration;
  if (!candidateAtStart || !requestAtStart) return;
  if (authorizationController.confirmInFlight) {
    setStatus('正在保存这项映射，请稍候。');
    return;
  }
  if (candidateAtStart.compatibility.level === 'warning' && !mismatchConfirmationArmed) {
    mismatchConfirmationArmed = true;
    elements.confirmCandidate.textContent = '名称不一致，仍确认保存';
    setStatus('名称看起来不一致。请再次点击确认，表示你已通过试听核对物理设备。');
    return;
  }
  setConfirmationBusy(true);
  elements.retryNotification.classList.add('hidden');
  try {
    const result = await authorizationController.confirm(candidateAtStart, requestAtStart, operationToken);
    if (result.status === 'ignored') {
      setStatus('正在保存这项映射，请稍候。');
      return;
    }
    const snapshot = result.snapshot;
    resetCandidate(true);
    if (result.notified) {
      setStatus(`已验证并保存：${snapshot.endpointName} → ${snapshot.browserLabel || '所选浏览器设备'}`);
    } else {
      elements.retryNotification.classList.remove('hidden');
      setStatus(`映射已保存，但通知增强标签页失败：${result.notificationError}。可以点击“重新通知”。`);
    }
  } finally {
    setConfirmationBusy(false);
    await requestPendingRefresh();
    await requestMappingRefresh();
    if (deviceRefreshRequested) {
      deviceRefreshRequested = false;
      await refreshVisibleDevices(true);
    }
  }
}

async function retryNotification() {
  elements.retryNotification.disabled = true;
  try {
    const result = await authorizationController.retryNotification();
    if (result.notified) {
      elements.retryNotification.classList.add('hidden');
      setStatus('已重新通知增强标签页。已保存的映射现在可以重试。');
    } else {
      setStatus(`映射仍已安全保存，但通知失败：${result.notificationError}。请确认桌面程序正在运行后重试。`);
    }
  } finally {
    elements.retryNotification.disabled = false;
  }
}

function setConfirmationBusy(busy) {
  for (const element of [elements.chooseOutput, elements.unlockDevices, elements.useFallback,
    elements.pendingTargets, elements.testCandidate, elements.confirmCandidate,
    elements.reselectCandidate, elements.cancelCandidate, elements.clearAll]) element.disabled = busy;
  for (const button of elements.mappings.querySelectorAll('button')) button.disabled = busy;
  if (!busy) {
    elements.pendingTargets.disabled = pendingRequests.length === 0;
    elements.chooseOutput.disabled = !pendingRequest;
    elements.unlockDevices.disabled = !pendingRequest;
    elements.useFallback.disabled = !pendingRequest || visibleOutputs.length === 0;
  }
}

function resetCandidate(force = false) {
  if (authorizationController.confirmInFlight && !force) return;
  candidate = null;
  mismatchConfirmationArmed = false;
  elements.candidatePanel.classList.add('hidden');
}

function cancelCandidate() {
  if (authorizationController.confirmInFlight) return;
  resetCandidate();
  setStatus('已取消；候选设备没有写入，原映射保持不变。');
}

async function refreshVisibleDevices(deviceChanged) {
  visibleOutputs = physicalOutputDevices(await navigator.mediaDevices.enumerateDevices());
  elements.fallbackDevices.replaceChildren();
  for (const device of visibleOutputs) {
    const option = document.createElement('option');
    option.value = device.deviceId;
    option.textContent = device.label || '未命名输出设备';
    elements.fallbackDevices.append(option);
  }
  elements.fallbackDevices.disabled = visibleOutputs.length === 0;
  elements.useFallback.disabled = !pendingRequest || visibleOutputs.length === 0;
  await renderMappings();
  if (deviceChanged) setStatus('设备列表已变化。不可见或有歧义的设备会要求重新授权。');
}

async function renderMappings() {
  const store = await loadMappingStore();
  const mappings = Object.values(outputMappings(store)).filter((mapping) => mapping.browser === browser);
  elements.mappings.replaceChildren();
  if (mappings.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '当前没有设备映射。';
    elements.mappings.append(empty);
    return;
  }
  for (const mapping of mappings) elements.mappings.append(createMappingCard(mapping));
}

function createMappingCard(mapping) {
  const card = document.createElement('article');
  card.className = 'mapping';
  const state = mappingDisplayState(mapping, visibleOutputs);
  const confirmed = mapping.verifiedAt ? new Date(mapping.verifiedAt).toLocaleString() : '尚未通过试听确认';
  card.innerHTML = '';
  const head = document.createElement('div');
  head.className = 'mapping-head';
  const names = document.createElement('div');
  const title = document.createElement('h3');
  title.textContent = mapping.windowsEndpointName;
  const browserDevice = document.createElement('p');
  browserDevice.textContent = `浏览器设备：${mapping.browserLabel || '名称不可用'}`;
  const verified = document.createElement('p');
  verified.textContent = `最后确认：${confirmed}`;
  names.append(title, browserDevice, verified);
  const badge = document.createElement('span');
  badge.className = `state ${state === '已验证' ? 'verified' : 'warning'}`;
  badge.textContent = state;
  head.append(names, badge);

  const actions = document.createElement('div');
  actions.className = 'actions';
  actions.append(
    actionButton('测试', '测试已保存映射', () => testExistingMapping(mapping)),
    actionButton('修改/重新授权', '修改输出映射', () => editMapping(mapping), 'secondary'),
    actionButton('删除/忘记', '删除输出映射', () => deleteMapping(mapping), 'danger')
  );
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  summary.textContent = '查看技术详情';
  const technical = document.createElement('pre');
  technical.textContent = `浏览器：${mapping.browser}\nWindows endpoint：${mapping.windowsEndpointId}\nBrowser deviceId：${mapping.deviceId}\nGroupId：${mapping.browserGroupId || '(无)'}\n状态原因：${mapping.staleReason || '(无)'}`;
  details.append(summary, technical);
  card.append(head, actions, details);
  return card;
}

function actionButton(text, operationName, action, className = '') {
  const button = document.createElement('button');
  button.textContent = text;
  button.className = className;
  button.disabled = authorizationController.confirmInFlight;
  button.addEventListener('click', () => runUiTask(operationName, action));
  return button;
}

async function testExistingMapping(mapping) {
  try {
    await playOutputTestTone(mapping.deviceId);
    setStatus(`“${mapping.windowsEndpointName}”测试声音播放完毕，临时音频上下文已关闭。`);
  } catch (error) { setStatus(`测试失败：${error.message}`); }
}

function editMapping(mapping) {
  if (authorizationController.confirmInFlight) return;
  const existing = pendingRequests.find((request) => request.windowsEndpointId === mapping.windowsEndpointId);
  if (!existing) {
    pendingRequests.push({ browser, windowsEndpointId: mapping.windowsEndpointId,
      windowsEndpointName: mapping.windowsEndpointName, waiters: {}, managementOnly: true });
  }
  renderPendingTargets(mapping.windowsEndpointId);
  selectPendingRequest(mapping.windowsEndpointId);
  elements.pendingTargets.disabled = false;
  setStatus('请选择新的浏览器设备。原映射会保留到你明确确认新候选为止。');
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

async function deleteMapping(mapping) {
  if (authorizationController.confirmInFlight) return;
  if (!window.confirm(`忘记“${mapping.windowsEndpointName}”的浏览器设备映射？`)) return;
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: removeOutputMapping(store, browser, mapping.windowsEndpointId) });
  await chrome.runtime.sendMessage({ type: 'authorization.mappingChanged', action: 'deleted', browser,
    windowsEndpointId: mapping.windowsEndpointId });
  setStatus('映射已删除；使用该设备的活动标签页会回到等待授权状态。');
  await renderMappings();
}

async function clearAllMappings() {
  if (authorizationController.confirmInFlight) return;
  if (!window.confirm('清除当前浏览器配置中的全部 Audio Source Mixer 输出设备映射？')) return;
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: clearBrowserOutputMappings(store, browser) });
  await chrome.runtime.sendMessage({ type: 'authorization.mappingChanged', action: 'cleared', browser });
  setStatus('本浏览器配置中的全部输出设备映射已清除。其他浏览器和配置文件不受影响。');
  await renderMappings();
}

function requirePendingRequest() {
  if (!pendingRequest?.windowsEndpointId) throw new Error('当前没有待设置的 Windows 输出设备。');
}

function setStatus(message) { elements.status.textContent = message; }

async function initialize() {
  await loadMappingStore();
  await loadPendingRequests();
  await refreshVisibleDevices(false);
}

initialize().catch((error) => reportUiError('初始化授权页面', error));
