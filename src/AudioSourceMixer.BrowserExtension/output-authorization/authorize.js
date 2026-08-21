import { browserName } from '../shared/protocol.js';
import { createI18n } from '../shared/i18n.js';
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
let i18n;

i18n = await createI18n(async () => {
  renderPendingTargets(pendingRequest?.windowsEndpointId || '');
  elements.targetName.textContent = pendingRequest?.windowsEndpointName || i18n.t('authorizeNoPending');
  elements.instruction.textContent = pendingRequest
    ? i18n.t('pendingNext', pendingRequest.windowsEndpointName) : i18n.t('authorizeInstruction');
  if (candidate) {
    elements.candidateBrowser.textContent = candidate.browserLabel || i18n.t('browserNameUnavailable');
    elements.compatibility.textContent = i18n.t(candidate.compatibility.messageCode || 'compatUnknown');
    elements.confirmCandidate.textContent = i18n.t(mismatchConfirmationArmed ? 'authorizeConfirmMismatch' : 'authorizeConfirm');
  }
  await renderMappings();
  setStatus(i18n.t('authorizeReading'));
});

const authorizationController = createAuthorizationController({
  browser,
  loadMappingStore,
  localStorage: chrome.storage.local,
  sessionStorage: chrome.storage.session,
  sendMessage: (message) => chrome.runtime.sendMessage(message)
});

elements.chooseOutput.addEventListener('click', () => runUiTask('authorizeOpenDeviceList', chooseOutputFromUserGesture));
elements.unlockDevices.addEventListener('click', () => runUiTask('authorizeShowCompatible', unlockCompatibilityDevices));
elements.useFallback.addEventListener('click', () => runUiTask('authorizeUseCandidate', selectFallbackCandidate));
elements.pendingTargets.addEventListener('change', () => runUiTask('authorizePendingLabel',
  () => selectPendingRequest(elements.pendingTargets.value)));
elements.testCandidate.addEventListener('click', () => runUiTask('authorizePlayTest', testCandidate));
elements.confirmCandidate.addEventListener('click', () => runUiTask('authorizeConfirm', confirmCandidate));
elements.retryNotification.addEventListener('click', () => runUiTask('authorizeRetryNotification', retryNotification));
elements.reselectCandidate.addEventListener('click', () => runUiTask('authorizeReselect', resetCandidate));
elements.cancelCandidate.addEventListener('click', () => runUiTask('authorizeCancel', cancelCandidate));
elements.clearAll.addEventListener('click', () => runUiTask('mappingClearAll', clearAllMappings));
navigator.mediaDevices.addEventListener('devicechange', () => {
  if (authorizationController.confirmInFlight) { deviceRefreshRequested = true; return; }
  runUiTask('authorizeBrowserOutputs', () => refreshVisibleDevices(true));
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[PENDING_OUTPUT_AUTHORIZATION_KEY])
    runUiTask('authorizePendingLabel', requestPendingRefresh);
  if (area === 'local' && (changes[OUTPUT_MAPPINGS_KEY] || changes[LEGACY_OUTPUT_MAPPINGS_KEY]))
    runUiTask('mappingTitle', requestMappingRefresh);
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
  console.error(`[Audio Source Mixer] ${name} failed`, error);
  setStatus(i18n.t('uiTaskFailed', [i18n.t(name), localizedError(error, message)]));
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
    option.textContent = `${request.windowsEndpointName} (${i18n.t('tabsWaiting', Object.keys(request.waiters || {}).length)})`;
    elements.pendingTargets.append(option);
  }
  elements.pendingTargets.disabled = pendingRequests.length === 0;
  const selected = pendingRequests.find((request) => request.windowsEndpointId === preferredId) || pendingRequests[0] || null;
  if (selected) elements.pendingTargets.value = selected.windowsEndpointId;
}

function selectPendingRequest(endpointId) {
  if (authorizationController.confirmInFlight) return;
  pendingRequest = pendingRequests.find((request) => request.windowsEndpointId === endpointId) || null;
  elements.targetName.textContent = pendingRequest?.windowsEndpointName || i18n.t('authorizeNoPending');
  elements.instruction.textContent = pendingRequest
    ? i18n.t('pendingNext', pendingRequest.windowsEndpointName)
    : i18n.t('authorizeInstruction');
  elements.chooseOutput.disabled = !pendingRequest;
  elements.unlockDevices.disabled = !pendingRequest;
  resetCandidate();
}

async function chooseOutputFromUserGesture() {
  try {
    requirePendingRequest();
    if (typeof navigator.mediaDevices.selectAudioOutput !== 'function') {
      elements.fallbackArea.classList.remove('hidden');
      setStatus(i18n.t('compatibilityMode'));
      return;
    }
    const selected = await navigator.mediaDevices.selectAudioOutput();
    showCandidate(selected);
  } catch (error) {
    setStatus(i18n.t('selectionIncomplete', localizedError(error)));
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
    setStatus(i18n.t('deviceAccessComplete'));
  } catch (error) { setStatus(i18n.t('deviceListFailed', localizedError(error))); }
}

function selectFallbackCandidate() {
  if (authorizationController.confirmInFlight) return;
  const selected = visibleOutputs.find((device) => device.deviceId === elements.fallbackDevices.value);
  if (!selected) { setStatus(i18n.t('chooseBrowserDevice')); return; }
  showCandidate(selected);
}

function showCandidate(selected) {
  if (authorizationController.confirmInFlight) return;
  requirePendingRequest();
  candidate = createOutputMappingCandidate(pendingRequest, selected, browser);
  mismatchConfirmationArmed = false;
  elements.candidateWindows.textContent = candidate.windowsEndpointName;
  elements.candidateBrowser.textContent = candidate.browserLabel || i18n.t('browserNameUnavailable');
  elements.compatibility.textContent = i18n.t(candidate.compatibility.messageCode || 'compatUnknown');
  elements.compatibility.classList.toggle('warning', candidate.compatibility.level === 'warning');
  elements.confirmCandidate.textContent = i18n.t('authorizeConfirm');
  elements.candidatePanel.classList.remove('hidden');
  setStatus(i18n.t('candidateUnsaved'));
}

async function testCandidate() {
  if (!candidate) return;
  elements.testCandidate.disabled = true;
  try {
    await playOutputTestTone(candidate.deviceId);
    setStatus(i18n.t('testComplete'));
  } catch (error) { setStatus(i18n.t('testFailed', localizedError(error))); }
  finally { elements.testCandidate.disabled = false; }
}

async function confirmCandidate() {
  const candidateAtStart = candidate;
  const requestAtStart = pendingRequest;
  const operationToken = ++operationGeneration;
  if (!candidateAtStart || !requestAtStart) return;
  if (authorizationController.confirmInFlight) {
    setStatus(i18n.t('savingMapping'));
    return;
  }
  if (candidateAtStart.compatibility.level === 'warning' && !mismatchConfirmationArmed) {
    mismatchConfirmationArmed = true;
    elements.confirmCandidate.textContent = i18n.t('authorizeConfirmMismatch');
    setStatus(i18n.t('mismatchSecondConfirm'));
    return;
  }
  setConfirmationBusy(true);
  elements.retryNotification.classList.add('hidden');
  try {
    const result = await authorizationController.confirm(candidateAtStart, requestAtStart, operationToken);
    if (result.status === 'ignored') {
      setStatus(i18n.t('savingMapping'));
      return;
    }
    const snapshot = result.snapshot;
    resetCandidate(true);
    if (result.notified) {
      setStatus(i18n.t('mappingSaved', [snapshot.endpointName, snapshot.browserLabel || i18n.t('mappingSelectedDevice')]));
    } else {
      elements.retryNotification.classList.remove('hidden');
      setStatus(i18n.t('mappingSavedNotifyFailed', result.notificationError));
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
      setStatus(i18n.t('notificationRetried'));
    } else {
      setStatus(i18n.t('notificationStillFailed', result.notificationError));
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
  setStatus(i18n.t('candidateCanceled'));
}

async function refreshVisibleDevices(deviceChanged) {
  visibleOutputs = physicalOutputDevices(await navigator.mediaDevices.enumerateDevices());
  elements.fallbackDevices.replaceChildren();
  for (const device of visibleOutputs) {
    const option = document.createElement('option');
    option.value = device.deviceId;
    option.textContent = device.label || i18n.t('unnamedOutput');
    elements.fallbackDevices.append(option);
  }
  elements.fallbackDevices.disabled = visibleOutputs.length === 0;
  elements.useFallback.disabled = !pendingRequest || visibleOutputs.length === 0;
  await renderMappings();
  if (deviceChanged) setStatus(i18n.t('deviceListChanged'));
}

async function renderMappings() {
  const store = await loadMappingStore();
  const mappings = Object.values(outputMappings(store)).filter((mapping) => mapping.browser === browser);
  elements.mappings.replaceChildren();
  if (mappings.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = i18n.t('mappingEmpty');
    elements.mappings.append(empty);
    return;
  }
  for (const mapping of mappings) elements.mappings.append(createMappingCard(mapping));
}

function createMappingCard(mapping) {
  const card = document.createElement('article');
  card.className = 'mapping';
  const state = mappingDisplayState(mapping, visibleOutputs);
  const confirmed = mapping.verifiedAt ? new Date(mapping.verifiedAt).toLocaleString(i18n.language) : i18n.t('notListeningConfirmed');
  card.innerHTML = '';
  const head = document.createElement('div');
  head.className = 'mapping-head';
  const names = document.createElement('div');
  const title = document.createElement('h3');
  title.textContent = mapping.windowsEndpointName;
  const browserDevice = document.createElement('p');
  browserDevice.textContent = i18n.t('mappingBrowserDevice', mapping.browserLabel || i18n.t('mappingNameUnavailable'));
  const verified = document.createElement('p');
  verified.textContent = i18n.t('mappingLastConfirmed', confirmed);
  names.append(title, browserDevice, verified);
  const badge = document.createElement('span');
  badge.className = `state ${state === 'verified' ? 'verified' : 'warning'}`;
  badge.textContent = i18n.t({ verified: 'mappingStateVerified', 'needs-reauthorization': 'mappingStateReauthorize',
    unavailable: 'mappingStateUnavailable', unverified: 'mappingStateUnverified' }[state] || 'mappingStateUnverified');
  head.append(names, badge);

  const actions = document.createElement('div');
  actions.className = 'actions';
  actions.append(
    actionButton('mappingTest', () => testExistingMapping(mapping)),
    actionButton('mappingEdit', () => editMapping(mapping), 'secondary'),
    actionButton('mappingDelete', () => deleteMapping(mapping), 'danger')
  );
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  summary.textContent = i18n.t('mappingDetails');
  const technical = document.createElement('pre');
  technical.textContent = i18n.t('mappingTechnical', [mapping.browser, mapping.windowsEndpointId, mapping.deviceId,
    mapping.browserGroupId || i18n.t('none'), mapping.staleReason || i18n.t('none')]);
  details.append(summary, technical);
  card.append(head, actions, details);
  return card;
}

function actionButton(key, action, className = '') {
  const button = document.createElement('button');
  button.textContent = i18n.t(key);
  button.className = className;
  button.disabled = authorizationController.confirmInFlight;
  button.addEventListener('click', () => runUiTask(key, action));
  return button;
}

async function testExistingMapping(mapping) {
  try {
    await playOutputTestTone(mapping.deviceId);
    setStatus(i18n.t('existingTestComplete', mapping.windowsEndpointName));
  } catch (error) { setStatus(i18n.t('existingTestFailed', localizedError(error))); }
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
  setStatus(i18n.t('editInstruction'));
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

async function deleteMapping(mapping) {
  if (authorizationController.confirmInFlight) return;
  if (!window.confirm(i18n.t('deleteConfirm', mapping.windowsEndpointName))) return;
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: removeOutputMapping(store, browser, mapping.windowsEndpointId) });
  await chrome.runtime.sendMessage({ type: 'authorization.mappingChanged', action: 'deleted', browser,
    windowsEndpointId: mapping.windowsEndpointId });
  setStatus(i18n.t('mappingDeleted'));
  await renderMappings();
}

async function clearAllMappings() {
  if (authorizationController.confirmInFlight) return;
  if (!window.confirm(i18n.t('clearConfirm'))) return;
  const store = await loadMappingStore();
  await chrome.storage.local.set({ [OUTPUT_MAPPINGS_KEY]: clearBrowserOutputMappings(store, browser) });
  await chrome.runtime.sendMessage({ type: 'authorization.mappingChanged', action: 'cleared', browser });
  setStatus(i18n.t('mappingsCleared'));
  await renderMappings();
}

function requirePendingRequest() {
  if (!pendingRequest?.windowsEndpointId) throw uiError('noPendingError', 'No pending Windows output device.');
}

function setStatus(message) { elements.status.textContent = message; }

function uiError(code, message) {
  const error = new Error(message);
  error.uiMessageKey = code;
  return error;
}

function localizedError(error, fallback = null) {
  if (error?.uiMessageKey) return i18n.t(error.uiMessageKey);
  return fallback || (error instanceof Error ? error.message : String(error));
}

async function initialize() {
  await loadMappingStore();
  await loadPendingRequests();
  await refreshVisibleDevices(false);
}

initialize().catch((error) => reportUiError('authorizePageTitle', error));
