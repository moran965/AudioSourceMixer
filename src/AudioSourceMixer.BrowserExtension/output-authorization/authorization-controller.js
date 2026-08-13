import {
  OUTPUT_MAPPINGS_KEY,
  PENDING_OUTPUT_AUTHORIZATION_KEY,
  confirmOutputMapping,
  removeAuthorizationWaiters
} from './mappings.js';

export function createAuthorizationController({ browser, loadMappingStore, localStorage, sessionStorage, sendMessage }) {
  if (!['chrome', 'edge'].includes(browser)) throw new Error('Unsupported browser authorization controller.');
  if (typeof loadMappingStore !== 'function' || typeof sendMessage !== 'function')
    throw new Error('Authorization controller dependencies are incomplete.');

  let confirmInFlight = false;
  let lastNotification = null;

  return Object.freeze({
    get confirmInFlight() { return confirmInFlight; },
    confirm,
    retryNotification
  });

  async function confirm(candidate, pendingRequest, operationToken) {
    const snapshot = createConfirmationSnapshot(browser, candidate, pendingRequest, operationToken);
    if (confirmInFlight) return Object.freeze({ status: 'ignored', reason: 'confirmation-in-flight', snapshot });

    confirmInFlight = true;
    try {
      const store = await loadMappingStore();
      await localStorage.set({ [OUTPUT_MAPPINGS_KEY]: confirmOutputMapping(store, snapshot.candidate) });

      const storedQueue = await sessionStorage.get(PENDING_OUTPUT_AUTHORIZATION_KEY);
      const queue = removeAuthorizationWaiters(
        storedQueue[PENDING_OUTPUT_AUTHORIZATION_KEY], snapshot.browser,
        snapshot.endpointId, snapshot.waiterKeys);
      await sessionStorage.set({ [PENDING_OUTPUT_AUTHORIZATION_KEY]: queue });

      const notification = Object.freeze({
        type: 'authorization.mappingChanged',
        action: 'confirmed',
        browser: snapshot.browser,
        windowsEndpointId: snapshot.endpointId,
        waiterCount: snapshot.waiterCount,
        operationToken: snapshot.operationToken
      });
      lastNotification = notification;
      try {
        await sendMessage(notification);
        lastNotification = null;
        return Object.freeze({ status: 'completed', mappingSaved: true, notified: true, snapshot });
      } catch (error) {
        return Object.freeze({ status: 'partial', mappingSaved: true, notified: false,
          notificationError: normalizeError(error), snapshot });
      }
    } finally {
      confirmInFlight = false;
    }
  }

  async function retryNotification() {
    const notification = lastNotification;
    if (!notification) return Object.freeze({ status: 'nothing-to-retry', notified: true });
    try {
      await sendMessage(notification);
      if (lastNotification === notification) lastNotification = null;
      return Object.freeze({ status: 'completed', notified: true });
    } catch (error) {
      return Object.freeze({ status: 'partial', notified: false, notificationError: normalizeError(error) });
    }
  }
}

export function createConfirmationSnapshot(browser, candidate, pendingRequest, operationToken) {
  if (!candidate?.windowsEndpointId || !candidate.deviceId) throw new Error('没有可确认的候选设备。');
  if (!pendingRequest?.windowsEndpointId) throw new Error('当前授权请求已不存在，请重新选择。');
  if (candidate.windowsEndpointId !== pendingRequest.windowsEndpointId)
    throw new Error('候选设备与当前授权请求不一致，请重新选择。');
  if (!Number.isSafeInteger(operationToken) || operationToken <= 0) throw new Error('确认操作标识无效。');

  const waiterEntries = Object.entries(pendingRequest.waiters || {})
    .map(([key, value]) => [key, Object.freeze({ ...(value || {}) })]);
  const candidateSnapshot = Object.freeze({
    ...candidate,
    compatibility: Object.freeze({ ...(candidate.compatibility || {}) })
  });
  const requestSnapshot = Object.freeze({
    ...pendingRequest,
    waiters: Object.freeze(Object.fromEntries(waiterEntries))
  });
  return Object.freeze({
    browser,
    endpointId: requestSnapshot.windowsEndpointId,
    endpointName: candidateSnapshot.windowsEndpointName,
    browserLabel: candidateSnapshot.browserLabel || '',
    waiterKeys: Object.freeze(waiterEntries.map(([key]) => key)),
    waiterCount: waiterEntries.length,
    operationToken,
    candidate: candidateSnapshot,
    pendingRequest: requestSnapshot
  });
}

function normalizeError(error) {
  return error instanceof Error ? error.message : String(error);
}
