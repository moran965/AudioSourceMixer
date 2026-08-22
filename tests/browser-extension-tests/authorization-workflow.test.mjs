import test from 'node:test';
import assert from 'node:assert/strict';
import {
  candidateTestIsCurrent,
  createCandidateTestVerification,
  isConcreteOutputDeviceId,
  playOutputTestTone
} from '../../src/AudioSourceMixer.BrowserExtension/output-authorization/authorization-workflow.js';

function mediaHarness(initialDevices = [{ kind: 'audiooutput', deviceId: 'speaker-id', label: 'Speakers' }]) {
  let devices = initialDevices;
  const listeners = new Set();
  return {
    mediaDevices: {
      async enumerateDevices() { return devices; },
      addEventListener(name, listener) { if (name === 'devicechange') listeners.add(listener); },
      removeEventListener(name, listener) { if (name === 'devicechange') listeners.delete(listener); }
    },
    setDevices(value) { devices = value; },
    dispatchDeviceChange() { for (const listener of [...listeners]) listener(); },
    get listenerCount() { return listeners.size; }
  };
}

function contextHarness({ initialSinkId = 'default', setSink = async (context, id) => { context.sinkId = id; } } = {}) {
  const calls = [];
  const oscillator = {
    frequency: { value: 0 },
    connect() { calls.push('oscillator.connect'); },
    start() { calls.push('oscillator.start'); },
    stop() { calls.push('oscillator.stop'); },
    disconnect() { calls.push('oscillator.disconnect'); }
  };
  const gain = {
    gain: { setValueAtTime() { calls.push('gain.value'); } },
    connect() { calls.push('gain.connect'); },
    disconnect() { calls.push('gain.disconnect'); }
  };
  const context = {
    sinkId: initialSinkId,
    state: 'suspended',
    currentTime: 0,
    destination: {},
    async setSinkId(id) { calls.push('context.setSinkId'); await setSink(context, id); },
    async resume() { calls.push('context.resume'); context.state = 'running'; },
    createOscillator() { calls.push('context.createOscillator'); return oscillator; },
    createGain() { calls.push('context.createGain'); return gain; },
    async close() { calls.push('context.close'); context.state = 'closed'; }
  };
  return { context, calls };
}

test('strict test tone verifies the effective sink before creating or starting audible nodes', async () => {
  const media = mediaHarness();
  const audio = contextHarness();
  const constructorOptions = [];
  const result = await playOutputTestTone('speaker-id', {
    mediaDevices: media.mediaDevices,
    createContext: (options) => { constructorOptions.push(options); return audio.context; },
    wait: async () => {}, durationMs: 500
  });

  assert.deepEqual(constructorOptions, [{ sinkId: 'speaker-id', latencyHint: 'interactive' }]);
  assert.equal(result.effectiveSinkId, 'speaker-id');
  assert.ok(audio.calls.indexOf('context.setSinkId') < audio.calls.indexOf('context.createOscillator'));
  assert.ok(audio.calls.indexOf('context.resume') < audio.calls.indexOf('context.createOscillator'));
  assert.ok(audio.calls.indexOf('context.createOscillator') < audio.calls.indexOf('oscillator.start'));
  assert.deepEqual(audio.calls.slice(-4), [
    'oscillator.stop', 'oscillator.disconnect', 'gain.disconnect', 'context.close'
  ]);
  assert.equal(media.listenerCount, 0);
});

test('a fulfilled setSinkId that leaves default or another sink never creates an oscillator', async () => {
  for (const effectiveSink of ['default', 'other-device']) {
    const media = mediaHarness();
    const audio = contextHarness({ initialSinkId: effectiveSink, setSink: async () => {} });
    await assert.rejects(playOutputTestTone('speaker-id', {
      mediaDevices: media.mediaDevices, createContext: () => audio.context
    }), (error) => error.uiMessageKey === 'sinkMismatch');
    assert.ok(!audio.calls.includes('context.createOscillator'));
    assert.equal(audio.calls.at(-1), 'context.close');
  }
});

test('setSinkId rejection closes the silent context without playback', async () => {
  const media = mediaHarness();
  const audio = contextHarness({ setSink: async () => { throw new Error('device rejected'); } });
  await assert.rejects(playOutputTestTone('speaker-id', {
    mediaDevices: media.mediaDevices, createContext: () => audio.context
  }), (error) => error.uiMessageKey === 'sinkUnavailable');
  assert.ok(!audio.calls.includes('context.createOscillator'));
  assert.equal(audio.calls.at(-1), 'context.close');
});

test('a device disappearing after resume but before node creation produces no sound', async () => {
  let enumerations = 0;
  const mediaDevices = {
    async enumerateDevices() {
      enumerations++;
      return enumerations < 3 ? [{ kind: 'audiooutput', deviceId: 'speaker-id' }] : [];
    },
    addEventListener() {}, removeEventListener() {}
  };
  const audio = contextHarness();
  await assert.rejects(playOutputTestTone('speaker-id', {
    mediaDevices, createContext: () => audio.context
  }), (error) => error.uiMessageKey === 'sinkUnavailable');
  assert.ok(!audio.calls.includes('context.createOscillator'));
  assert.equal(audio.calls.at(-1), 'context.close');
});

test('devicechange during playback stops the oscillator and closes all temporary resources', async () => {
  const media = mediaHarness();
  const audio = contextHarness();
  await assert.rejects(playOutputTestTone('speaker-id', {
    mediaDevices: media.mediaDevices,
    createContext: () => audio.context,
    wait: async () => {
      media.setDevices([]);
      media.dispatchDeviceChange();
      return new Promise(() => {});
    }
  }), (error) => error.uiMessageKey === 'sinkUnavailable');
  assert.equal(audio.calls.filter((call) => call === 'oscillator.stop').length, 1);
  assert.deepEqual(audio.calls.slice(-4), [
    'oscillator.stop', 'oscillator.disconnect', 'gain.disconnect', 'context.close'
  ]);
  assert.equal(media.listenerCount, 0);
});

test('empty, default, communications, and missing physical IDs are rejected before context creation', async () => {
  for (const id of ['', 'default', 'communications']) {
    let created = false;
    await assert.rejects(playOutputTestTone(id, {
      mediaDevices: mediaHarness().mediaDevices,
      createContext: () => { created = true; return contextHarness().context; }
    }), (error) => error.uiMessageKey === 'sinkUnavailable');
    assert.equal(created, false);
    assert.equal(isConcreteOutputDeviceId(id), false);
  }
  await assert.rejects(playOutputTestTone('missing-id', {
    mediaDevices: mediaHarness([]).mediaDevices,
    createContext: () => contextHarness().context
  }), (error) => error.uiMessageKey === 'sinkUnavailable');
});

test('verification proof is bound to browser, endpoint, device, candidate, and device-list generations', () => {
  const candidateA = { browser: 'edge', windowsEndpointId: 'endpoint-a', deviceId: 'device-a',
    candidateGeneration: 1, deviceListGeneration: 4 };
  const verified = createCandidateTestVerification(candidateA, 1, 4, 'verified', 'device-a', '2026-08-22T00:00:00Z');
  assert.equal(candidateTestIsCurrent({ ...candidateA, testVerification: verified }), true);
  assert.equal(candidateTestIsCurrent({ ...candidateA, deviceId: 'device-b', testVerification: verified }), false);
  assert.equal(candidateTestIsCurrent({ ...candidateA, candidateGeneration: 2, testVerification: verified }), false);
  assert.equal(candidateTestIsCurrent({ ...candidateA, deviceListGeneration: 5, testVerification: verified }), false);
  assert.equal(candidateTestIsCurrent({ ...candidateA, browser: 'chrome', testVerification: verified }), false);
});
