import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { browserName, clamp, matchOutputDevice, normalizeDeviceLabel, sanitizeOrigin, sourceId, validateAudioCommand } from '../../src/AudioSourceMixer.BrowserExtension/shared/protocol.js';
import { createEqualizerPreset } from '../../src/AudioSourceMixer.BrowserExtension/shared/equalizer.js';
import {
  clearBrowserOutputMappings, confirmOutputMapping, findOutputMapping, mappingIsVisible, migrateOutputMappingStore,
  outputMappingKey, outputMappings, removeOutputMapping, saveOutputMapping,
  queueAuthorizationRequest, pendingAuthorizationRequests, pendingAuthorizationState, removeAuthorizationRequest,
  rebindOutputMapping, markOutputMappingStale, physicalOutputDevices
} from '../../src/AudioSourceMixer.BrowserExtension/output-authorization/mappings.js';
import { compareDeviceNames, createOutputMappingCandidate, playOutputTestTone }
  from '../../src/AudioSourceMixer.BrowserExtension/output-authorization/authorization-workflow.js';
import { shouldConnectNativeOnRecovery }
  from '../../src/AudioSourceMixer.BrowserExtension/service-worker/lifecycle-policy.js';

test('sourceId distinguishes browser and tab', () => {
  assert.equal(sourceId('chrome', 12), 'chrome:12');
  assert.equal(sourceId('edge', 12), 'edge:12');
});

test('idle service-worker recovery never requests native messaging', () => {
  assert.equal(shouldConnectNativeOnRecovery([]), false);
  assert.equal(shouldConnectNativeOnRecovery(null), false);
  assert.equal(shouldConnectNativeOnRecovery([{ tabId: 12, browser: 'edge' }]), true);
});

test('audio values are clamped', () => {
  assert.equal(clamp(2, 0, 1), 1);
  assert.equal(clamp(-2, -1, 1), -1);
});

test('origin discards path and query', () => {
  assert.equal(sanitizeOrigin('https://example.com/private?q=secret'), 'https://example.com');
});

test('Edge user agent is detected', () => {
  assert.equal(browserName('Mozilla/5.0 Edg/151.0'), 'edge');
  assert.equal(browserName('Mozilla/5.0 Chrome/150.0'), 'chrome');
});

test('native audio command carries endpoint catalog and correlation ID', () => {
  const equalizer = createEqualizerPreset('bass');
  assert.deepEqual(validateAudioCommand({ protocolVersion: 3, type: 'tab.setAudio', volume: 1.5, balance: -1, muted: true,
    outputDeviceId: 'windows-endpoint', outputDeviceName: 'USB DAC', correlationId: 'corr-1',
    outputDevices: [{ endpointId: 'windows-endpoint', friendlyName: 'USB DAC' }], equalizer }),
  { volume: 1.5, balance: -1, muted: true, outputDeviceId: 'windows-endpoint', outputDeviceName: 'USB DAC',
    followSystemDefault: false, resolvedOutputDeviceId: '', resolvedOutputDeviceName: '',
    outputDevices: [{ endpointId: 'windows-endpoint', friendlyName: 'USB DAC' }], correlationId: 'corr-1',
    generation: 0, requestSource: 'ProfileRestore', forceAuthorization: false, equalizer });
  assert.throws(() => validateAudioCommand({ protocolVersion: 2, type: 'tab.setAudio' }));
});

test('system default command retains semantic selection and carries resolved Windows endpoint', () => {
  const command = validateAudioCommand({
    protocolVersion: 3, type: 'tab.setAudio', outputDeviceId: '', followSystemDefault: true,
    resolvedOutputDeviceId: 'windows-headphones', resolvedOutputDeviceName: 'Bluetooth Headphones'
  });
  assert.equal(command.outputDeviceId, '');
  assert.equal(command.followSystemDefault, true);
  assert.equal(command.resolvedOutputDeviceId, 'windows-headphones');
  assert.equal(command.resolvedOutputDeviceName, 'Bluetooth Headphones');
});

test('name matching remains a controlled compatibility helper only', () => {
  const devices = [
    { kind: 'audiooutput', deviceId: 'default', label: 'Default - Speakers (Realtek Audio)' },
    { kind: 'audiooutput', deviceId: 'chromium-usb-id', label: 'USB DAC' }
  ];
  assert.equal(normalizeDeviceLabel('Default - Speakers (Realtek Audio)'), 'speakers (realtek audio)');
  assert.equal(matchOutputDevice(devices, 'USB DAC').deviceId, 'chromium-usb-id');
  assert.equal(matchOutputDevice(devices, 'Missing'), null);
});

test('authorized output mapping is keyed by Windows endpoint ID and revalidated by browser deviceId', () => {
  const mappings = confirmOutputMapping({}, {
    browser: 'edge', windowsEndpointId: '{wh-endpoint-id}', windowsEndpointName: '耳机 (WH-1000XM5)',
    browserLabel: 'WH-1000XM5', browserGroupId: 'group-wh', deviceId: 'edge-wh-id', updatedAt: '2026-08-10T00:00:00Z'
  });
  assert.equal(outputMappingKey('edge', '{wh-endpoint-id}'), 'edge:{wh-endpoint-id}');
  assert.equal(findOutputMapping(mappings, 'edge', '{wh-endpoint-id}', 'renamed device').deviceId, 'edge-wh-id');
  assert.equal(findOutputMapping(mappings, 'edge', '{other-endpoint-id}', '耳机 (WH-1000XM5)'), null);
  assert.equal(findOutputMapping(mappings, 'chrome', '{wh-endpoint-id}', '耳机 (WH-1000XM5)'), null);
  const mapping = Object.values(outputMappings(mappings))[0];
  assert.equal(mapping.browserGroupId, 'group-wh');
  assert.equal(mappingIsVisible(mapping, [
    { kind: 'audiooutput', deviceId: 'edge-wh-id', label: 'WH-1000XM5' }
  ]), true);
  assert.equal(mappingIsVisible(mapping, []), false);
  assert.ok(mapping.authorizedAt);
});

test('authorization excludes virtual default devices and stores only a concrete physical output', () => {
  const devices = [
    { kind: 'audiooutput', deviceId: 'default', groupId: 'group-wh', label: 'Default - WH-1000XM5' },
    { kind: 'audiooutput', deviceId: 'communications', groupId: 'group-wh', label: 'Communications - WH-1000XM5' },
    { kind: 'audiooutput', deviceId: 'physical-wh', groupId: 'group-wh', label: 'WH-1000XM5' },
    { kind: 'audioinput', deviceId: 'microphone', label: 'Microphone' }
  ];
  assert.deepEqual(physicalOutputDevices(devices).map((device) => device.deviceId), ['physical-wh']);
  assert.throws(() => saveOutputMapping({}, {
    browser: 'chrome', windowsEndpointId: 'wh', windowsEndpointName: 'WH-1000XM5',
    browserLabel: 'Default - WH-1000XM5', browserGroupId: 'group-wh', deviceId: 'default'
  }), /concrete browser output device/);
  const rebound = rebindOutputMapping({
    browser: 'chrome', windowsEndpointId: 'wh', windowsEndpointName: 'WH-1000XM5',
    browserLabel: 'Default - WH-1000XM5', browserGroupId: 'group-wh', deviceId: 'default'
  }, devices);
  assert.equal(rebound.deviceId, 'physical-wh');
  assert.equal(rebound.matchKind, 'groupId+label');
});

test('pending authorization queue keeps independent endpoints and every waiting tab', () => {
  let queue = queueAuthorizationRequest({}, {
    browser: 'edge', tabId: 11, correlationId: 'corr-11', generation: 101,
    windowsEndpointId: 'realtek', windowsEndpointName: 'Realtek Speakers'
  });
  queue = queueAuthorizationRequest(queue, {
    browser: 'edge', tabId: 12, correlationId: 'corr-12', generation: 102,
    windowsEndpointId: 'realtek', windowsEndpointName: 'Realtek Speakers'
  });
  queue = queueAuthorizationRequest(queue, {
    browser: 'edge', tabId: 13, correlationId: 'corr-13', generation: 103,
    windowsEndpointId: 'bluetooth', windowsEndpointName: 'WH-1000XM5'
  });
  const requests = pendingAuthorizationRequests(queue, 'edge');
  assert.equal(requests.length, 2);
  assert.deepEqual(Object.keys(requests.find((request) => request.windowsEndpointId === 'realtek').waiters).sort(),
    ['edge:11', 'edge:12']);
  queue = removeAuthorizationRequest(queue, 'edge', 'realtek');
  assert.deepEqual(pendingAuthorizationRequests(queue, 'edge').map((request) => request.windowsEndpointId), ['bluetooth']);
});

test('pending authorization acknowledgement retains the desktop correlation and generation', () => {
  const pending = pendingAuthorizationState({
    browser: 'chrome', tabId: 17, correlationId: 'desktop-correlation', commandGeneration: 91,
    outputDeviceId: 'windows-realtek', outputDeviceName: 'Realtek'
  });
  assert.equal(pending.routingState, 'PendingAuthorization');
  assert.equal(pending.correlationId, 'desktop-correlation');
  assert.equal(pending.commandGeneration, 91);
  assert.equal(pending.outputStatus, 'authorizationRequired');
  assert.equal(pending.outputStatusDetail, 'Realtek');
  assert.equal(pending.error, 'authorization-required');
});

test('stale browser deviceId safely rebinds only on a unique group and label or unique label', () => {
  const mapping = {
    browser: 'edge', windowsEndpointId: 'realtek', windowsEndpointName: 'Realtek Speakers',
    deviceId: 'stale-id', browserGroupId: 'group-realtek', browserLabel: 'Speakers (Realtek Audio)'
  };
  const groupMatch = rebindOutputMapping(mapping, [
    { kind: 'audiooutput', deviceId: 'new-id', groupId: 'group-realtek', label: 'Speakers (Realtek Audio)' },
    { kind: 'audiooutput', deviceId: 'other', groupId: 'other', label: 'Speakers (Realtek Audio)' }
  ]);
  assert.equal(groupMatch.deviceId, 'new-id');
  assert.equal(groupMatch.matchKind, 'groupId+label');
  assert.equal(rebindOutputMapping({ ...mapping, browserGroupId: '' }, [
    { kind: 'audiooutput', deviceId: 'one', label: 'Speakers (Realtek Audio)' },
    { kind: 'audiooutput', deviceId: 'two', label: 'Speakers (Realtek Audio)' }
  ]), null);
  const stale = markOutputMappingStale({ 'edge:realtek': mapping }, 'edge', 'realtek',
    'device-id-not-visible', '2026-08-11T00:00:00Z');
  assert.equal(stale.mappings['edge:realtek'].lastSeenAt, '2026-08-11T00:00:00Z');
  assert.equal(stale.mappings['edge:realtek'].verificationState, 'needs-reauthorization');
});

test('manifest remains MV3 with scoped capture, authorization, and storage capabilities', async () => {
  const manifest = JSON.parse(await readFile(new URL('../../src/AudioSourceMixer.BrowserExtension/manifest.json', import.meta.url), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.ok(manifest.action);
  assert.equal(manifest.action.default_popup, undefined);
  assert.equal(manifest.options_ui.page, 'output-authorization/authorize.html');
  assert.ok(manifest.permissions.includes('tabs'));
  assert.ok(manifest.permissions.includes('tabCapture'));
  assert.ok(!manifest.permissions.includes('audioCapture'));
  assert.equal(manifest.host_permissions, undefined);
  assert.ok(!manifest.permissions.includes('<all_urls>'));
});

test('visible authorization page consumes requests through a guarded transaction and stops fallback microphone tracks', async () => {
  const code = await readFile(new URL('../../src/AudioSourceMixer.BrowserExtension/output-authorization/authorize.js', import.meta.url), 'utf8');
  assert.match(code, /elements\.chooseOutput\.addEventListener\('click'/);
  assert.match(code, /navigator\.mediaDevices\.selectAudioOutput\(\)/);
  assert.match(code, /getUserMedia\(\{ audio: true, video: false \}\)/);
  assert.match(code, /getTracks\(\)\.forEach\(\(track\) => track\.stop\(\)\)/);
  assert.match(code, /pendingRequest\?\.windowsEndpointId/);
  assert.match(code, /pendingAuthorizationRequests/);
  assert.match(code, /chrome\.storage\.local/);
  assert.match(code, /createAuthorizationController/);
  assert.match(code, /candidateAtStart = candidate/);
  assert.match(code, /requestAtStart = pendingRequest/);
  assert.match(code, /authorizationController\.confirm\(candidateAtStart, requestAtStart, operationToken\)/);
  assert.match(code, /initialize\(\)\.catch/);
  assert.doesNotMatch(code, /\bvoid\s+[A-Za-z_$]/u);
  assert.doesNotMatch(code, /windowsNameInput/);
  assert.ok(code.indexOf('candidateAtStart = candidate') < code.indexOf('await authorizationController.confirm'));
});

test('legacy mappings migrate to schema 3 as unverified and are not trusted before confirmation', () => {
  const migrated = migrateOutputMappingStore(null, { 'edge:legacy': {
    browser: 'edge', windowsEndpointId: 'legacy', windowsEndpointName: 'USB DAC',
    deviceId: 'device-old', browserLabel: 'USB DAC', browserGroupId: 'g'
  } });
  assert.equal(migrated.schemaVersion, 3);
  assert.equal(migrated.mappings['edge:legacy'].verificationState, 'unverified');
  assert.equal(findOutputMapping(migrated, 'edge', 'legacy', 'USB DAC'), null);
});

test('candidate stays transient until confirmation; mappings can be replaced, removed, and browser-scoped', () => {
  const oldStore = confirmOutputMapping({}, { browser: 'edge', windowsEndpointId: 'speaker',
    windowsEndpointName: 'Realtek Speakers', deviceId: 'old-device', browserLabel: 'Speakers', browserGroupId: 'old-group' },
    '2026-08-01T00:00:00Z');
  const candidate = createOutputMappingCandidate({ windowsEndpointId: 'speaker', windowsEndpointName: 'Realtek Speakers' },
    { kind: 'audiooutput', deviceId: 'new-device', label: 'Speakers (Realtek)', groupId: 'new-group' }, 'edge');
  assert.equal(findOutputMapping(oldStore, 'edge', 'speaker').deviceId, 'old-device');
  const confirmed = confirmOutputMapping(oldStore, candidate, '2026-08-13T00:00:00Z');
  assert.equal(findOutputMapping(confirmed, 'edge', 'speaker').deviceId, 'new-device');
  assert.equal(findOutputMapping(removeOutputMapping(confirmed, 'edge', 'speaker'), 'edge', 'speaker'), null);
  const mixed = confirmOutputMapping(confirmed, { ...candidate, browser: 'chrome', windowsEndpointId: 'other' });
  const cleared = clearBrowserOutputMappings(mixed, 'edge');
  assert.equal(findOutputMapping(cleared, 'edge', 'speaker'), null);
  assert.equal(findOutputMapping(cleared, 'chrome', 'other').deviceId, 'new-device');
});

test('name mismatch warns and test tone closes its temporary audio context', async () => {
  assert.equal(compareDeviceNames('WH-1000XM5 Bluetooth Headphones', 'Speakers (Realtek Audio)').level, 'warning');
  const calls = [];
  const context = {
    currentTime: 1, destination: {}, setSinkId: async (id) => calls.push(['sink', id]),
    createOscillator: () => ({ frequency: { value: 0 }, connect: () => {}, start: () => calls.push(['start']),
      stop: () => calls.push(['stop']), disconnect: () => calls.push(['osc-disconnect']) }),
    createGain: () => ({ gain: { setValueAtTime: (value) => calls.push(['gain', value]) }, connect: () => {}, disconnect: () => calls.push(['gain-disconnect']) }),
    close: async () => calls.push(['close'])
  };
  await playOutputTestTone('candidate', { createContext: () => context, durationMs: 500, wait: async () => {} });
  assert.deepEqual(calls.at(-1), ['close']);
  assert.ok(calls.some(([name]) => name === 'stop'));
});

test('offscreen graph verifies context.sinkId and does not set default on requested-sink failure', async () => {
  const code = await readFile(new URL('../../src/AudioSourceMixer.BrowserExtension/offscreen/offscreen.js', import.meta.url), 'utf8');
  assert.match(code, /clamp\(message\.volume \?\? graph\.volume, 0, 2\)/);
  assert.match(code, /context\.setSinkId/);
  assert.match(code, /context\.sinkId/);
  assert.match(code, /routingState = 'Failed'/);
  assert.match(code, /error = 'set-sink-failed'/);
  assert.doesNotMatch(code, /[\p{Script=Han}]/u);
  const chromeApis = [...code.matchAll(/chrome\.([A-Za-z]+)/g)].map((match) => match[1]);
  assert.deepEqual([...new Set(chromeApis)], ['runtime']);
});

test('service worker stores transitions, recovers graphs, and uses browser+tab locks without volatile tab maps', async () => {
  const code = await readFile(new URL('../../src/AudioSourceMixer.BrowserExtension/service-worker/service-worker.js', import.meta.url), 'utf8');
  assert.match(code, /state\?\.state === 'starting' \|\| state\?\.state === 'stopping'/);
  assert.match(code, /chrome\.storage\.session/);
  assert.match(code, /PENDING_OUTPUT_AUTHORIZATION_KEY/);
  assert.match(code, /withSourceLock/);
  assert.match(code, /navigator\?\.locks\?\.request/);
  assert.match(code, /mutateStates/);
  assert.match(code, /chrome\.runtime\.openOptionsPage\(\)/);
  assert.match(code, /openAuthorizationPageOnce/);
  const pendingPublish = code.indexOf("await publishOutputState(message.tabId, pending, 'none')");
  const authorizationOpen = code.indexOf('if (shouldOpenAuthorization(audio)) openAuthorizationPageInBackground()');
  assert.ok(pendingPublish >= 0 && authorizationOpen > pendingPublish,
    'PendingAuthorization ACK must be published before opening the visible authorization page');
  assert.match(code, /audio\.list/);
  assert.match(code, /chrome\.tabCapture\.getCapturedTabs\(\)/);
  assert.match(code, /registerAllActiveTabs/);
  const recoveryBody = code.slice(code.indexOf('async function recoverRuntimeState'), code.indexOf('async function getStates'));
  assert.match(recoveryBody, /if \(shouldConnectNativeOnRecovery\(graphs\)\) await ensureNativeReady\(\)/);
  assert.doesNotMatch(recoveryBody, /await ensureNativePort\(\)/);
  assert.doesNotMatch(code, /\.then\s*\(/);
  assert.doesNotMatch(code, /let nativeRetryAfter/);
  assert.doesNotMatch(code, /const tabLocks = new Map/);
  assert.doesNotMatch(code, /let stateMutation/);
  assert.match(code, /closeOffscreenIfIdle/);
  assert.match(code, /chrome\.offscreen\.closeDocument\(\)/);
});

test('extension version matches the centralized product version', async () => {
  const manifest = JSON.parse(await readFile(new URL('../../src/AudioSourceMixer.BrowserExtension/manifest.json', import.meta.url), 'utf8'));
  const props = await readFile(new URL('../../Directory.Build.props', import.meta.url), 'utf8');
  const match = props.match(/<AudioSourceMixerVersion>([^<]+)<\/AudioSourceMixerVersion>/);
  assert.ok(match);
  assert.equal(manifest.version, match[1]);
});
