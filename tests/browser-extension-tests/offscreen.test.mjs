import test from 'node:test';
import assert from 'node:assert/strict';
import { createEqualizerPreset } from '../../src/AudioSourceMixer.BrowserExtension/shared/equalizer.js';

test('offscreen graph applies 200 percent gain, verifies the effective sink, never silently falls back, and closes', async () => {
  const original = {
    chrome: globalThis.chrome,
    navigator: globalThis.navigator,
    AudioContext: globalThis.AudioContext,
    setInterval: globalThis.setInterval
  };
  let runtimeListener;
  let deviceChangeListener;
  let activeContext;
  const contexts = [];
  const runtimeMessages = [];
  const track = { addEventListener() {}, stopCalled: false, stop() { this.stopCalled = true; } };
  const stream = { getTracks: () => [track] };
  let devices = [
    { kind: 'audiooutput', deviceId: 'default', label: 'Default - Speakers' },
    { kind: 'audiooutput', deviceId: 'browser-usb', label: 'USB DAC', groupId: 'usb-group' }
  ];
  let forcedSinkId = null;

  class FakeNode {
    connect() { return this; }
    disconnect() { this.disconnected = true; }
  }
  class FakeParam {
    constructor(value = 0) { this.value = value; this.targets = []; }
    setTargetAtTime(value, time, constant) { this.value = value; this.targets.push({ value, time, constant }); }
  }
  class FakeAudioContext {
    constructor() {
      activeContext = this;
      contexts.push(this);
      this.currentTime = 0;
      this.sinkId = '';
      this.destination = new FakeNode();
      this.gainNodes = [];
      this.filters = [];
      this.pan = new FakeParam();
    }
    async resume() {}
    createMediaStreamSource() { return new FakeNode(); }
    createGain() { const node = new FakeNode(); node.gain = new FakeParam(1); this.gainNodes.push(node); return node; }
    createBiquadFilter() {
      const node = new FakeNode();
      node.frequency = new FakeParam(); node.Q = new FakeParam(); node.gain = new FakeParam();
      this.filters.push(node);
      return node;
    }
    createStereoPanner() { const node = new FakeNode(); node.pan = this.pan; return node; }
    createAnalyser() {
      const node = new FakeNode();
      node.fftSize = 256;
      node.getFloatTimeDomainData = (buffer) => buffer.fill(0);
      return node;
    }
    async setSinkId(value) { this.sinkId = forcedSinkId ?? value; }
    async close() { this.closed = true; }
  }

  globalThis.chrome = {
    runtime: {
      onMessage: { addListener(listener) { runtimeListener = listener; } },
      async sendMessage(message) { runtimeMessages.push(message); return { ok: true }; }
    }
  };
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true,
    value: {
      mediaDevices: {
        async getUserMedia() { return stream; },
        async enumerateDevices() { return devices; },
        addEventListener(type, listener) { if (type === 'devicechange') deviceChangeListener = listener; }
      }
    }
  });
  globalThis.AudioContext = FakeAudioContext;
  globalThis.setInterval = () => 1;

  try {
    await import(`../../src/AudioSourceMixer.BrowserExtension/offscreen/offscreen.js?test=${Date.now()}`);
    assert.equal(typeof runtimeListener, 'function');
    const started = await runtimeListener({ type: 'audio.start', tabId: 7, streamId: 'stream' });
    assert.equal(started.ok, true);
    const firstContext = activeContext;
    assert.equal(firstContext.filters.length, 10);
    assert.equal(firstContext.gainNodes.length, 2);
    const bass = createEqualizerPreset('bass');
    const updated = await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 10, volume: 2, balance: -0.5,
      muted: false, outputDeviceId: 'windows-usb', outputDeviceName: 'Windows USB Name', correlationId: 'corr-applied',
      browserOutputDeviceId: 'browser-usb', browserOutputDeviceLabel: 'USB DAC', browserGroupId: 'usb-group', equalizer: bass });
    assert.equal(updated.ok, true);
    assert.equal(firstContext.gainNodes[1].gain.value, 2);
    assert.equal(firstContext.pan.value, -0.5);
    assert.equal(firstContext.sinkId, 'browser-usb');
    assert.deepEqual(firstContext.filters.map((filter) => filter.gain.value), bass.bands.map((band) => band.gainDb));
    assert.ok(Math.abs(firstContext.gainNodes[0].gain.value - 0.501187) < 0.000001);
    assert.equal(updated.routingState, 'Applied');
    assert.equal(updated.effectiveSinkId, 'browser-usb');
    assert.equal(updated.browserDeviceId, 'browser-usb');
    assert.equal(updated.setSinkIdSupported, true);

    const pendingDefault = await runtimeListener({
      type: 'audio.update', browser: 'chrome', tabId: 7, generation: 10, volume: 2, balance: -0.5,
      muted: false, outputDeviceId: '', followSystemDefault: true,
      resolvedOutputDeviceId: 'windows-headphones', resolvedOutputDeviceName: 'Bluetooth Headphones'
    });
    assert.equal(pendingDefault.routingState, 'PendingAuthorization');
    assert.equal(firstContext.sinkId, 'browser-usb');

    const followedDefault = await runtimeListener({
      type: 'audio.update', browser: 'chrome', tabId: 7, generation: 10, volume: 2, balance: -0.5,
      muted: false, outputDeviceId: '', followSystemDefault: true,
      resolvedOutputDeviceId: 'windows-headphones', resolvedOutputDeviceName: 'Bluetooth Headphones',
      browserOutputDeviceId: 'browser-usb', browserOutputDeviceLabel: 'USB DAC', browserGroupId: 'usb-group'
    });
    assert.equal(followedDefault.outputDeviceId, '');
    assert.equal(followedDefault.followSystemDefault, true);
    assert.equal(followedDefault.resolvedOutputDeviceId, 'windows-headphones');
    assert.equal(followedDefault.effectiveSinkId, 'browser-usb');
    assert.equal(followedDefault.routingState, 'Applied');

    const virtualMapping = await runtimeListener({
      type: 'audio.update', browser: 'chrome', tabId: 7, generation: 11, volume: 2, balance: -0.5,
      muted: false, outputDeviceId: 'windows-usb', outputDeviceName: 'Windows USB Name', correlationId: 'corr-virtual',
      browserOutputDeviceId: 'default', browserOutputDeviceLabel: 'Default - USB DAC', browserGroupId: 'usb-group'
    });
    assert.equal(virtualMapping.routingState, 'Applied');
    assert.equal(virtualMapping.effectiveSinkId, 'browser-usb');
    assert.equal(virtualMapping.mappingRebound.matchKind, 'groupId+label');
    assert.deepEqual(virtualMapping.equalizer.bands.map((band) => band.gainDb), bass.bands.map((band) => band.gainDb));

    const stale = await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 9,
      volume: 0.25, balance: 0, muted: false, outputDeviceId: 'windows-usb', outputDeviceName: 'USB DAC',
      equalizer: createEqualizerPreset('treble') });
    assert.equal(stale.staleIgnored, true);
    assert.equal(firstContext.gainNodes[1].gain.value, 2);
    assert.deepEqual(firstContext.filters.map((filter) => filter.gain.value), bass.bands.map((band) => band.gainDb));

    devices = [{ kind: 'audiooutput', deviceId: 'default', label: 'Default - Speakers' }];
    deviceChangeListener();
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(firstContext.sinkId, 'browser-usb');
    assert.ok(runtimeMessages.some((message) => message.type === 'offscreen.outputChanged' &&
      message.routingState === 'PendingAuthorization' && message.effectiveSinkId === 'browser-usb'));

    devices.push({ kind: 'audiooutput', deviceId: 'browser-usb-rebound', label: 'USB DAC', groupId: 'usb-group' });
    deviceChangeListener();
    await new Promise((resolve) => setImmediate(resolve));
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(firstContext.sinkId, 'browser-usb-rebound');
    assert.ok(runtimeMessages.some((message) => message.type === 'offscreen.outputChanged' &&
      message.routingState === 'Applied' && message.mappingRebound?.matchKind === 'groupId+label'));

    devices.push({ kind: 'audiooutput', deviceId: 'browser-usb', label: 'USB DAC' });
    forcedSinkId = 'unexpected-sink';
    const mismatch = await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 12, volume: 2, balance: 0,
      muted: false, outputDeviceId: 'windows-usb', outputDeviceName: 'USB DAC', correlationId: 'corr-mismatch',
      browserOutputDeviceId: 'browser-usb', browserOutputDeviceLabel: 'USB DAC' });
    assert.equal(mismatch.ok, false);
    assert.equal(mismatch.routingState, 'Failed');
    assert.equal(mismatch.effectiveSinkId, 'unexpected-sink');
    assert.match(mismatch.error, /sinkId mismatch/);

    forcedSinkId = null;
    const secondStarted = await runtimeListener({ type: 'audio.start', browser: 'edge', tabId: 7, streamId: 'stream-edge' });
    assert.equal(secondStarted.ok, true);
    assert.equal(contexts.length, 2);
    const listed = await runtimeListener({ type: 'audio.list' });
    assert.deepEqual(listed.graphs.map((graph) => `${graph.browser}:${graph.tabId}`).sort(), ['chrome:7', 'edge:7']);
    await runtimeListener({ type: 'audio.stop', browser: 'edge', tabId: 7 });
    const afterIndependentStop = await runtimeListener({ type: 'audio.list' });
    assert.deepEqual(afterIndependentStop.graphs.map((graph) => `${graph.browser}:${graph.tabId}`), ['chrome:7']);
    assert.equal(contexts[0].closed, undefined);

    const disabled = await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 13,
      volume: 2, balance: 0, muted: false, outputDeviceId: '', equalizer: createEqualizerPreset('off') });
    assert.equal(disabled.equalizer.enabled, false);
    assert.ok(firstContext.filters.every((filter) => filter.gain.value === 0));
    assert.equal(firstContext.gainNodes[0].gain.value, 1);

    const rapidOne = createEqualizerPreset('flat');
    rapidOne.presetId = 'custom'; rapidOne.bands[0].gainDb = 2;
    const rapidTwo = createEqualizerPreset('flat');
    rapidTwo.presetId = 'custom'; rapidTwo.bands[0].gainDb = 7;
    await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 14,
      volume: 2, balance: 0, muted: false, outputDeviceId: '', equalizer: rapidOne });
    const rapidFinal = await runtimeListener({ type: 'audio.update', browser: 'chrome', tabId: 7, generation: 15,
      volume: 2, balance: 0, muted: false, outputDeviceId: '', equalizer: rapidTwo });
    assert.equal(rapidFinal.equalizer.bands[0].gainDb, 7);
    assert.equal(firstContext.filters[0].gain.value, 7);

    const stopped = await runtimeListener({ type: 'audio.stop', browser: 'chrome', tabId: 7 });
    assert.equal(stopped.ok, true);
    assert.equal(track.stopCalled, true);
    assert.equal(contexts[0].closed, true);
    assert.ok(firstContext.filters.every((filter) => filter.disconnected));
    assert.ok(firstContext.gainNodes.every((node) => node.disconnected));
  } finally {
    globalThis.chrome = original.chrome;
    if (original.navigator === undefined) delete globalThis.navigator;
    else Object.defineProperty(globalThis, 'navigator', { configurable: true, value: original.navigator });
    globalThis.AudioContext = original.AudioContext;
    globalThis.setInterval = original.setInterval;
  }
});
