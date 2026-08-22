import test from 'node:test';
import assert from 'node:assert/strict';
import {
  OUTPUT_MAPPINGS_KEY,
  PENDING_OUTPUT_AUTHORIZATION_KEY,
  authorizationRequestKey
} from '../../src/AudioSourceMixer.BrowserExtension/output-authorization/mappings.js';
import {
  createAuthorizationController,
  createConfirmationSnapshot
} from '../../src/AudioSourceMixer.BrowserExtension/output-authorization/authorization-controller.js';

function candidate(endpointId = 'endpoint-a', testStatus = 'verified') {
  const selected = {
    browser: 'edge', windowsEndpointId: endpointId, windowsEndpointName: '扬声器 A',
    browserLabel: 'Speakers A', browserGroupId: 'group-a', deviceId: 'device-a',
    authorizedAt: '2026-08-14T00:00:00.000Z', compatibility: { level: 'likely', messageCode: 'compatLikely' },
    candidateGeneration: 3, deviceListGeneration: 7
  };
  return { ...selected, testVerification: {
    status: testStatus, browser: selected.browser, windowsEndpointId: selected.windowsEndpointId,
    deviceId: selected.deviceId, effectiveSinkId: testStatus === 'verified' ? selected.deviceId : '',
    candidateGeneration: selected.candidateGeneration, deviceListGeneration: selected.deviceListGeneration,
    verifiedAt: testStatus === 'verified' ? '2026-08-14T00:00:01.000Z' : null
  } };
}

function request(endpointId = 'endpoint-a', waiterIds = ['edge:1', 'edge:2']) {
  return {
    browser: 'edge', windowsEndpointId: endpointId, windowsEndpointName: '扬声器 A',
    waiters: Object.fromEntries(waiterIds.map((key, index) => [key, {
      browser: 'edge', tabId: index + 1, generation: index + 4, correlationId: `c-${index}`
    }]))
  };
}

function mappingStore() { return { schemaVersion: 3, mappings: {} }; }

function harness(options = {}) {
  const state = {
    local: {},
    session: { [PENDING_OUTPUT_AUTHORIZATION_KEY]: options.queue || {
      [authorizationRequestKey('edge', 'endpoint-a')]: request()
    } },
    localWrites: 0,
    sessionWrites: 0,
    notifications: 0
  };
  const localStorage = {
    async set(value) {
      state.localWrites++;
      Object.assign(state.local, value);
      options.onLocalSet?.(value, state);
    }
  };
  const sessionStorage = {
    async get(key) { return { [key]: state.session[key] }; },
    async set(value) {
      state.sessionWrites++;
      Object.assign(state.session, value);
      options.onSessionSet?.(value, state);
    }
  };
  const controller = createAuthorizationController({
    browser: 'edge',
    loadMappingStore: options.loadMappingStore || (async () => mappingStore()),
    localStorage,
    sessionStorage,
    sendMessage: async (message) => {
      state.notifications++;
      state.lastNotification = message;
      if (options.sendError && state.notifications <= (options.failNotifications ?? 1)) throw options.sendError;
      return { ok: true };
    }
  });
  return { controller, state };
}

test('confirmation snapshots candidate, request, browser, waiters, and token before any await', async () => {
  const selected = candidate();
  const pending = request();
  let visibleCandidate = selected;
  let visibleRequest = pending;
  const { controller, state } = harness({
    onLocalSet() { visibleCandidate = null; visibleRequest = null; },
    onSessionSet() { visibleCandidate = null; }
  });

  const confirmation = controller.confirm(visibleCandidate, visibleRequest, 17);
  visibleCandidate = null;
  visibleRequest = null;
  const result = await confirmation;

  assert.equal(result.status, 'completed');
  assert.equal(result.snapshot.endpointName, '扬声器 A');
  assert.equal(result.snapshot.browserLabel, 'Speakers A');
  assert.equal(result.snapshot.waiterCount, 2);
  assert.equal(result.snapshot.operationToken, 17);
  assert.equal(state.lastNotification.windowsEndpointId, 'endpoint-a');
  assert.equal(state.lastNotification.waiterCount, 2);
  assert.ok(state.local[OUTPUT_MAPPINGS_KEY].mappings['edge:endpoint-a']);
});

test('synchronous and post-write storage refreshes may clear UI state without corrupting confirmation', async () => {
  for (const timing of ['synchronous', 'after-promise']) {
    let visibleCandidate = candidate();
    let refreshes = 0;
    const { controller } = harness({
      onSessionSet() {
        if (timing === 'synchronous') { refreshes++; visibleCandidate = null; }
        else queueMicrotask(() => { refreshes++; visibleCandidate = null; });
      },
      onLocalSet() { refreshes++; visibleCandidate = null; }
    });
    const result = await controller.confirm(visibleCandidate, request(), timing === 'synchronous' ? 1 : 2);
    await new Promise((resolve) => queueMicrotask(resolve));
    assert.equal(result.status, 'completed', timing);
    assert.equal(result.snapshot.endpointName, '扬声器 A', timing);
    assert.equal(visibleCandidate, null, timing);
    assert.ok(refreshes >= 2, timing);
  }
});

test('rapid double confirmation persists, removes, and notifies exactly once', async () => {
  let releaseLoad;
  const loadGate = new Promise((resolve) => { releaseLoad = resolve; });
  const { controller, state } = harness({
    loadMappingStore: async () => { await loadGate; return mappingStore(); }
  });

  const first = controller.confirm(candidate(), request(), 1);
  const second = await controller.confirm(candidate(), request(), 2);
  assert.equal(second.status, 'ignored');
  releaseLoad();
  assert.equal((await first).status, 'completed');
  assert.equal(state.localWrites, 1);
  assert.equal(state.sessionWrites, 1);
  assert.equal(state.notifications, 1);
});

test('confirmation tolerates a request removed or selection changed by another flow', async () => {
  const { controller, state } = harness({ queue: {} });
  const selected = candidate();
  const pending = request();
  const resultPromise = controller.confirm(selected, pending, 4);
  selected.windowsEndpointName = '已切换的页面候选';
  pending.windowsEndpointName = '已切换的请求';
  pending.waiters = {};
  const result = await resultPromise;
  assert.equal(result.status, 'completed');
  assert.equal(result.snapshot.endpointName, '扬声器 A');
  assert.equal(result.snapshot.waiterCount, 2);
  assert.deepEqual(state.session[PENDING_OUTPUT_AUTHORIZATION_KEY], {});
});

test('only snapshotted waiters are removed while waiters arriving during confirmation survive', async () => {
  const newWaiter = { browser: 'edge', tabId: 9, generation: 9 };
  const { controller, state } = harness({
    onLocalSet() {
      state.session[PENDING_OUTPUT_AUTHORIZATION_KEY]['edge:endpoint-a'].waiters['edge:9'] = newWaiter;
    }
  });
  const result = await controller.confirm(candidate(), request(), 5);
  assert.equal(result.status, 'completed');
  assert.deepEqual(state.session[PENDING_OUTPUT_AUTHORIZATION_KEY]['edge:endpoint-a'].waiters,
    { 'edge:9': newWaiter });
});

test('device changes and page reselection cannot mutate the immutable in-flight snapshot', async () => {
  const selected = candidate();
  const pending = request();
  let releaseLoad;
  const loadGate = new Promise((resolve) => { releaseLoad = resolve; });
  const { controller } = harness({ loadMappingStore: async () => { await loadGate; return mappingStore(); } });
  const resultPromise = controller.confirm(selected, pending, 6);
  selected.browserLabel = 'devicechange replacement';
  pending.waiters['edge:3'] = { browser: 'edge', tabId: 3 };
  releaseLoad();
  const result = await resultPromise;
  assert.equal(result.snapshot.browserLabel, 'Speakers A');
  assert.equal(result.snapshot.waiterCount, 2);
  assert.ok(Object.isFrozen(result.snapshot));
  assert.ok(Object.isFrozen(result.snapshot.candidate));
  assert.ok(Object.isFrozen(result.snapshot.pendingRequest));
});

test('notification rejection keeps the mapping and supports an idempotent retry', async () => {
  const { controller, state } = harness({ sendError: new Error('service worker unavailable'), failNotifications: 1 });
  const result = await controller.confirm(candidate(), request(), 7);
  assert.equal(result.status, 'partial');
  assert.equal(result.mappingSaved, true);
  assert.equal(result.notified, false);
  assert.match(result.notificationError, /service worker unavailable/);
  assert.ok(state.local[OUTPUT_MAPPINGS_KEY].mappings['edge:endpoint-a']);

  const retried = await controller.retryNotification();
  assert.equal(retried.status, 'completed');
  assert.equal(retried.notified, true);
  assert.equal(state.localWrites, 1);
  assert.equal(state.notifications, 2);
  assert.equal((await controller.retryNotification()).status, 'nothing-to-retry');
});

test('invalid or mismatched transient state is rejected before storage mutation', async () => {
  assert.throws(() => createConfirmationSnapshot('edge', null, request(), 1),
    (error) => error.uiMessageKey === 'candidateMissing');
  assert.throws(() => createConfirmationSnapshot('edge', candidate('a'), request('b'), 1),
    (error) => error.uiMessageKey === 'candidateMismatch');
  assert.throws(() => createConfirmationSnapshot('edge', candidate(), request(), 0),
    (error) => error.uiMessageKey === 'operationInvalid');
  const { controller, state } = harness();
  await assert.rejects(controller.confirm(candidate('a'), request('b'), 8),
    (error) => error.uiMessageKey === 'candidateMismatch');
  assert.equal(state.localWrites, 0);
  assert.equal(state.sessionWrites, 0);
  assert.equal(state.notifications, 0);
});

test('confirmation rejects untested, failed, stale-generation, and wrong-device verification proofs', async () => {
  for (const selected of [
    candidate('endpoint-a', 'untested'),
    candidate('endpoint-a', 'failed'),
    { ...candidate(), deviceListGeneration: 8 },
    { ...candidate(), testVerification: { ...candidate().testVerification, effectiveSinkId: 'default' } }
  ]) {
    const { controller, state } = harness();
    await assert.rejects(controller.confirm(selected, request(), 10),
      (error) => error.uiMessageKey === 'testRequiredBeforeSave');
    assert.equal(state.localWrites, 0);
    assert.equal(state.sessionWrites, 0);
    assert.equal(state.notifications, 0);
  }
});

test('handled notification failures do not emit unhandledRejection', async () => {
  const unhandled = [];
  const listener = (reason) => unhandled.push(reason);
  process.on('unhandledRejection', listener);
  try {
    const { controller } = harness({ sendError: new Error('expected rejection'), failNotifications: 2 });
    assert.equal((await controller.confirm(candidate(), request(), 9)).status, 'partial');
    assert.equal((await controller.retryNotification()).status, 'partial');
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(unhandled, []);
  } finally {
    process.off('unhandledRejection', listener);
  }
});
