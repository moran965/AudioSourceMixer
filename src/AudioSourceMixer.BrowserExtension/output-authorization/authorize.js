import { browserName } from '../shared/protocol.js';
import { compareDeviceNames, createOutputMappingCandidate, playOutputTestTone } from './authorization-workflow.js';
import {
  LEGACY_OUTPUT_MAPPINGS_KEY,
  OUTPUT_MAPPINGS_KEY,
  PENDING_OUTPUT_AUTHORIZATION_KEY,
  clearBrowserOutputMappings,
  confirmOutputMapping,
  mappingDisplayState,
  migrateOutputMappingStore,
  outputMappings,
  pendingAuthorizationRequests,
  physicalOutputDevices,
  removeAuthorizationRequest,
  removeOutputMapping
} from './mappings.js';

const browser = browserName();
const elements = Object.fromEntries([...document.querySelectorAll('[id]')].map((element) => [element.id, element]));
let pendingRequest = null;
let pendingRequests = [];
let visibleOutputs = [];
let candidate = null;
let mismatchConfirmationArmed = false;

elements.chooseOutput.addEventListener('click', () => { void chooseOutputFromUserGesture(); });
elements.unlockDevices.addEventListener('click', () => { void unlockCompatibilityDevices(); });
elements.useFallback.addEventListener('click', () => { selectFallbackCandidate(); });
elements.pendingTargets.addEventListener('change', () => { selectPendingRequest(elements.pendingTargets.value); });
elements.testCandidate.addEventListener('click', () => { void testCandidate(); });
elements.confirmCandidate.addEventListener('click', () => { void confirmCandidate(); });
elements.reselectCandidate.addEventListener('click', resetCandidate);
elements.cancelCandidate.addEventListener('click', cancelCandidate);
elements.clearAll.addEventListener('click', () => { void clearAllMappings(); });
navigator.mediaDevices.addEventListener('devicechange', () => { void refreshVisibleDevices(true); });
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[PENDING_OUTPUT_AUTHORIZATION_KEY]) void loadPendingRequests();
  if (area === 'local' && (changes[OUTPUT_MAPPINGS_KEY] || changes[LEGACY_OUTPUT_MAPPINGS_KEY])) void renderMappings();
});

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
  const selected = visibleOutputs.find((device) => device.deviceId === elements.fallbackDevices.value);
  if (!selected) { setStatus('请先选择一个浏览器输出设备。'); return; }
  showCandidate(selected);
}

function showCandidate(selected) {
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
  if (!candidate) return;
  if (candidate.compatibility.level === 'warning' && !mismatchConfirmationArmed) {
    mismatchConfirmationArmed = true;
    elements.confirmCandidate.textContent = '名称不一致，仍确认保存';
    setStatus('名称看起来不一致。请再次点击确认，表示你已通过试听核对物理设备。');
    return;
  }
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: confirmOutputMapping(store, candidate) });
  const completed = pendingRequest;
  const storedQueue = await chrome.storage.session.get(PENDING_OUTPUT_AUTHORIZATION_KEY);
  const queue = removeAuthorizationRequest(storedQueue[PENDING_OUTPUT_AUTHORIZATION_KEY], browser, completed.windowsEndpointId);
  await chrome.storage.session.set({ [PENDING_OUTPUT_AUTHORIZATION_KEY]: queue });
  await chrome.runtime.sendMessage({
    type: 'authorization.mappingChanged', action: 'confirmed', browser,
    windowsEndpointId: completed.windowsEndpointId,
    waiterCount: Object.keys(completed.waiters || {}).length
  });
  setStatus(`已验证并保存：${candidate.windowsEndpointName} → ${candidate.browserLabel || '所选浏览器设备'}`);
  candidate = null;
  elements.candidatePanel.classList.add('hidden');
  await loadPendingRequests();
  await renderMappings();
}

function resetCandidate() {
  candidate = null;
  mismatchConfirmationArmed = false;
  elements.candidatePanel.classList.add('hidden');
}

function cancelCandidate() {
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
    actionButton('测试', () => { void testExistingMapping(mapping); }),
    actionButton('修改/重新授权', () => editMapping(mapping), 'secondary'),
    actionButton('删除/忘记', () => { void deleteMapping(mapping); }, 'danger')
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

function actionButton(text, handler, className = '') {
  const button = document.createElement('button');
  button.textContent = text;
  button.className = className;
  button.addEventListener('click', handler);
  return button;
}

async function testExistingMapping(mapping) {
  try {
    await playOutputTestTone(mapping.deviceId);
    setStatus(`“${mapping.windowsEndpointName}”测试声音播放完毕，临时音频上下文已关闭。`);
  } catch (error) { setStatus(`测试失败：${error.message}`); }
}

function editMapping(mapping) {
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
  if (!window.confirm(`忘记“${mapping.windowsEndpointName}”的浏览器设备映射？`)) return;
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: removeOutputMapping(store, browser, mapping.windowsEndpointId) });
  await chrome.runtime.sendMessage({ type: 'authorization.mappingChanged', action: 'deleted', browser,
    windowsEndpointId: mapping.windowsEndpointId });
  setStatus('映射已删除；使用该设备的活动标签页会回到等待授权状态。');
  await renderMappings();
}

async function clearAllMappings() {
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

await loadMappingStore();
await loadPendingRequests();
await refreshVisibleDevices(false);
