import { clamp, sourceId } from '../shared/protocol.js';
import { smoothPeak } from '../shared/levels.js';
import {
  EQUALIZER_BANDS,
  createEqualizerPreset,
  decibelsToGain,
  effectiveHeadroomDb,
  normalizeEqualizer
} from '../shared/equalizer.js';
import { physicalOutputDevices, rebindOutputMapping } from '../output-authorization/mappings.js';

const graphs = new Map();

chrome.runtime.onMessage.addListener((message) => {
  if (message.type === 'audio.start') return executeResponseTask('create tab audio graph', () => startGraph(message));
  if (message.type === 'audio.update') return executeResponseTask('update tab audio graph', () => updateGraph(message));
  if (message.type === 'audio.stop')
    return executeResponseTask('stop tab audio graph', () => stopGraph(message.browser || 'chrome', message.tabId, false));
  if (message.type === 'audio.list') return listGraphs();
  return undefined;
});

function runOffscreenTask(name, action) {
  executeOffscreenTask(name, action);
}

async function executeOffscreenTask(name, action) {
  try { await action(); }
  catch (error) { console.error(`[Audio Source Mixer] ${name} failed`, error); }
}

async function executeResponseTask(name, action) {
  try { return await action(); }
  catch (error) {
    console.error(`[Audio Source Mixer] ${name} failed`, error);
    return { ok: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
  }
}

function graphKey(browser, tabId) {
  return sourceId(browser || 'chrome', tabId);
}

async function startGraph(message) {
  const browser = message.browser || 'chrome';
  const key = graphKey(browser, message.tabId);
  const existing = graphs.get(key);
  if (existing) return { ok: true, ...outputResult(existing) };
  try {
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: message.streamId } },
      video: false
    });
    const context = new AudioContext();
    await context.resume();
    const source = context.createMediaStreamSource(stream);
    const equalizerFilters = EQUALIZER_BANDS.map((definition) => {
      const filter = context.createBiquadFilter();
      filter.type = definition.filterType;
      filter.frequency.value = definition.frequencyHz;
      filter.Q.value = definition.q;
      filter.gain.value = 0;
      return filter;
    });
    const headroom = context.createGain();
    const gain = context.createGain();
    const panner = context.createStereoPanner();
    const analyser = context.createAnalyser();
    analyser.fftSize = 256;
    source.connect(equalizerFilters[0]);
    for (let index = 0; index < equalizerFilters.length - 1; index++)
      equalizerFilters[index].connect(equalizerFilters[index + 1]);
    equalizerFilters.at(-1).connect(headroom).connect(gain).connect(panner).connect(analyser).connect(context.destination);
    const graph = {
      browser, tabId: message.tabId, title: message.title || 'Untitled tab', origin: message.origin || '',
      stream, context, source, equalizerFilters, headroom, gain, panner, analyser,
      levelBuffer: new Float32Array(analyser.fftSize), smoothedPeak: 0,
      volume: 1, balance: 0, muted: false, generation: Number(message.generation) || 0,
      selectedOutputDeviceId: '', selectedOutputDeviceName: '', followSystemDefault: false,
      resolvedOutputDeviceId: '', resolvedOutputDeviceName: '',
      requestedOutputDeviceId: '', requestedOutputDeviceName: '',
      browserOutputDeviceId: '', browserOutputDeviceLabel: '', browserGroupId: '',
      effectiveSinkId: '', effectiveSinkLabel: '', routingState: 'Default',
      outputStatus: 'systemDefault', outputStatusDetail: '', correlationId: '', setSinkDurationMs: 0,
      routeQueue: Promise.resolve(),
      setSinkIdSupported: typeof context.setSinkId === 'function', error: null, errorDetail: null,
      mappingRebound: null, mappingStale: false,
      equalizer: createEqualizerPreset('off')
    };
    graphs.set(key, graph);
    applyEqualizer(graph, message.equalizer);
    for (const track of stream.getTracks()) {
      track.addEventListener('ended', () =>
        runOffscreenTask('clean up ended media track', () => stopGraph(browser, message.tabId, true)), { once: true });
    }
    const output = await enqueueOutputDevice(graph, message);
    return { ok: true, ...output };
  } catch (error) {
    return { ok: false, error: `${error.name || 'Error'}: ${error.message}` };
  }
}

async function updateGraph(message) {
  const browser = message.browser || 'chrome';
  const graph = graphs.get(graphKey(browser, message.tabId));
  if (!graph) return { ok: false, error: 'Audio graph not found.', generation: message.generation || 0 };
  const generation = Number.isSafeInteger(message.generation) ? message.generation : graph.generation;
  if (generation < graph.generation) return { ok: true, staleIgnored: true, ...outputResult(graph) };
  graph.generation = generation;
  graph.volume = clamp(message.volume ?? graph.volume, 0, 2);
  graph.balance = clamp(message.balance ?? graph.balance, -1, 1);
  graph.muted = Boolean(message.muted);
  graph.gain.gain.setTargetAtTime(graph.muted ? 0 : graph.volume, graph.context.currentTime, 0.01);
  graph.panner.pan.setTargetAtTime(graph.balance, graph.context.currentTime, 0.01);
  applyEqualizer(graph, message.equalizer ?? graph.equalizer);
  const output = await enqueueOutputDevice(graph, message);
  return { ok: output.routingState !== 'Failed', ...output };
}

function applyEqualizer(graph, settings) {
  const equalizer = normalizeEqualizer(settings);
  const now = graph.context.currentTime;
  for (let index = 0; index < graph.equalizerFilters.length; index++) {
    const gainDb = equalizer.enabled ? equalizer.bands[index].gainDb : 0;
    graph.equalizerFilters[index].gain.setTargetAtTime(gainDb, now, 0.01);
  }
  graph.headroom.gain.setTargetAtTime(decibelsToGain(effectiveHeadroomDb(equalizer)), now, 0.01);
  graph.equalizer = equalizer;
}

async function enqueueOutputDevice(graph, message) {
  const previous = graph.routeQueue;
  let release;
  graph.routeQueue = new Promise((resolve) => { release = resolve; });
  try {
    try { await previous; } catch {}
    if (Number.isSafeInteger(message.generation) && message.generation < graph.generation)
      return { staleIgnored: true, ...outputResult(graph) };
    return await applyOutputDevice(graph, message);
  } finally { release(); }
}

async function applyOutputDevice(graph, message) {
  const previousRequestedOutputDeviceId = graph.requestedOutputDeviceId ?? '';
  graph.selectedOutputDeviceId = message.outputDeviceId ?? graph.selectedOutputDeviceId ?? '';
  graph.selectedOutputDeviceName = message.outputDeviceName ?? graph.selectedOutputDeviceName ?? '';
  graph.followSystemDefault = message.followSystemDefault ?? graph.followSystemDefault ?? false;
  graph.resolvedOutputDeviceId = message.resolvedOutputDeviceId ?? graph.resolvedOutputDeviceId ?? '';
  graph.resolvedOutputDeviceName = message.resolvedOutputDeviceName ?? graph.resolvedOutputDeviceName ?? '';
  graph.requestedOutputDeviceId = graph.followSystemDefault
    ? graph.resolvedOutputDeviceId : graph.selectedOutputDeviceId;
  graph.requestedOutputDeviceName = graph.followSystemDefault
    ? graph.resolvedOutputDeviceName : graph.selectedOutputDeviceName;
  const requestedEndpointChanged = previousRequestedOutputDeviceId !== graph.requestedOutputDeviceId;
  graph.browserOutputDeviceId = Object.hasOwn(message, 'browserOutputDeviceId')
    ? (message.browserOutputDeviceId ?? '')
    : (requestedEndpointChanged ? '' : (graph.browserOutputDeviceId ?? ''));
  graph.browserOutputDeviceLabel = Object.hasOwn(message, 'browserOutputDeviceLabel')
    ? (message.browserOutputDeviceLabel ?? '')
    : (requestedEndpointChanged ? '' : (graph.browserOutputDeviceLabel ?? ''));
  graph.browserGroupId = Object.hasOwn(message, 'browserGroupId')
    ? (message.browserGroupId ?? '')
    : (requestedEndpointChanged ? '' : (graph.browserGroupId ?? ''));
  graph.correlationId = message.correlationId || graph.correlationId || crypto.randomUUID();
  graph.setSinkIdSupported = typeof graph.context.setSinkId === 'function';
  graph.error = null;
  graph.errorDetail = null;
  graph.outputStatusDetail = '';
  graph.setSinkDurationMs = 0;
  graph.mappingRebound = null;
  graph.mappingStale = false;

  if (!graph.setSinkIdSupported) {
    graph.routingState = graph.requestedOutputDeviceId ? 'Failed' : 'Default';
    graph.outputStatus = graph.requestedOutputDeviceId ? 'setSinkIdUnavailable' : 'systemDefaultSetSinkUnavailable';
    graph.error = graph.requestedOutputDeviceId ? 'set-sink-id-unavailable' : null;
    return outputResult(graph);
  }

  if (!graph.requestedOutputDeviceId) {
    if (!graph.followSystemDefault) return setAndVerifySink(graph, '', '', 'Default');
    graph.routingState = 'PendingAuthorization';
    graph.outputStatus = 'defaultEndpointUnresolved';
    graph.error = 'default-endpoint-unresolved';
    return outputResult(graph);
  }

  if (!graph.browserOutputDeviceId) {
    graph.routingState = 'PendingAuthorization';
    graph.outputStatus = 'authorizationRequired';
    graph.outputStatusDetail = graph.requestedOutputDeviceName || graph.requestedOutputDeviceId;
    graph.error = 'authorization-required';
    return outputResult(graph);
  }

  const devices = await navigator.mediaDevices.enumerateDevices();
  const outputs = physicalOutputDevices(devices);
  let target = outputs.find((device) => device.deviceId === graph.browserOutputDeviceId);
  if (!target) {
    const rebound = rebindOutputMapping({
      browser: graph.browser,
      windowsEndpointId: graph.requestedOutputDeviceId,
      windowsEndpointName: graph.requestedOutputDeviceName,
      deviceId: graph.browserOutputDeviceId,
      browserLabel: graph.browserOutputDeviceLabel,
      browserGroupId: graph.browserGroupId
    }, outputs);
    if (rebound && rebound.matchKind !== 'deviceId') {
      target = outputs.find((device) => device.deviceId === rebound.deviceId);
      graph.browserOutputDeviceId = rebound.deviceId;
      graph.browserOutputDeviceLabel = rebound.browserLabel;
      graph.browserGroupId = rebound.browserGroupId;
      graph.mappingRebound = rebound;
    }
  }
  if (!target) {
    graph.routingState = 'PendingAuthorization';
    graph.outputStatus = 'mappingStale';
    graph.outputStatusDetail = graph.requestedOutputDeviceName || graph.requestedOutputDeviceId;
    graph.error = 'mapping-stale';
    graph.mappingStale = true;
    return outputResult(graph);
  }

  graph.browserOutputDeviceLabel = target.label || graph.browserOutputDeviceLabel;
  graph.browserGroupId = target.groupId || graph.browserGroupId;
  return setAndVerifySink(graph, target.deviceId, target.label || graph.requestedOutputDeviceName, 'Applied');
}

async function setAndVerifySink(graph, requestedSinkId, label, successState) {
  const startedAt = performance.now();
  try {
    await graph.context.setSinkId(requestedSinkId);
    graph.setSinkDurationMs = performance.now() - startedAt;
    graph.effectiveSinkId = typeof graph.context.sinkId === 'string' ? graph.context.sinkId : '';
    graph.effectiveSinkLabel = label || '';
    if (graph.effectiveSinkId !== requestedSinkId || (requestedSinkId && !graph.effectiveSinkId)) {
      graph.routingState = 'Failed';
      graph.error = 'sink-mismatch';
      graph.errorDetail = `requested=${safeId(requestedSinkId)}, effective=${safeId(graph.effectiveSinkId)}`;
      graph.outputStatus = 'sinkMismatch';
    } else {
      graph.routingState = successState;
      graph.outputStatus = successState === 'Default' ? 'systemDefault' : 'applied';
      graph.outputStatusDetail = label || safeId(requestedSinkId);
      graph.error = null;
      graph.errorDetail = null;
    }
  } catch (error) {
    graph.setSinkDurationMs = performance.now() - startedAt;
    graph.effectiveSinkId = typeof graph.context.sinkId === 'string' ? graph.context.sinkId : '';
    graph.routingState = 'Failed';
    graph.error = 'set-sink-failed';
    graph.errorDetail = `${error.name || 'Error'}: ${error.message}`;
    graph.outputStatus = 'setSinkFailed';
  }
  return outputResult(graph);
}

function outputResult(graph) {
  return {
    browser: graph.browser,
    tabId: graph.tabId,
    generation: graph.generation,
    correlationId: graph.correlationId,
    outputDeviceId: graph.selectedOutputDeviceId,
    outputDeviceName: graph.selectedOutputDeviceName,
    followSystemDefault: graph.followSystemDefault,
    resolvedOutputDeviceId: graph.resolvedOutputDeviceId,
    resolvedOutputDeviceName: graph.resolvedOutputDeviceName,
    browserDeviceId: graph.browserOutputDeviceId,
    browserDeviceLabel: graph.browserOutputDeviceLabel,
    browserGroupId: graph.browserGroupId,
    effectiveSinkId: graph.effectiveSinkId,
    effectiveSinkLabel: graph.effectiveSinkLabel,
    routingState: graph.routingState,
    setSinkDurationMs: graph.setSinkDurationMs,
    setSinkIdSupported: graph.setSinkIdSupported,
    outputStatus: graph.outputStatus,
    outputStatusDetail: graph.outputStatusDetail,
    error: graph.error,
    errorDetail: graph.errorDetail,
    mappingRebound: graph.mappingRebound,
    mappingStale: graph.mappingStale,
    equalizer: graph.equalizer
  };
}

function listGraphs() {
  return {
    ok: true,
    graphs: [...graphs.values()].map((graph) => ({
      ...outputResult(graph), title: graph.title, origin: graph.origin,
      volume: graph.volume, balance: graph.balance, muted: graph.muted,
      equalizer: graph.equalizer, state: 'active'
    }))
  };
}

function safeId(value) {
  if (!value) return '(default/empty)';
  return value.length <= 12 ? value : `${value.slice(0, 8)}…${value.slice(-4)}`;
}

async function stopGraph(browser, tabId, streamAlreadyEnded) {
  const key = graphKey(browser, tabId);
  const graph = graphs.get(key);
  if (!graph) return { ok: true };
  graphs.delete(key);
  try {
    for (const node of [graph.source, ...graph.equalizerFilters, graph.headroom, graph.gain, graph.panner, graph.analyser])
      try { node.disconnect(); } catch {}
    if (!streamAlreadyEnded)
      for (const track of graph.stream.getTracks()) try { track.stop(); } catch {}
    try { await graph.context.close(); } catch {}
  } finally {
    if (streamAlreadyEnded) await chrome.runtime.sendMessage({ type: 'offscreen.tabEnded', browser, tabId });
  }
  return { ok: true };
}

setInterval(() => {
  for (const graph of graphs.values()) {
    graph.analyser.getFloatTimeDomainData(graph.levelBuffer);
    let peak = 0;
    for (const sample of graph.levelBuffer) peak = Math.max(peak, Math.abs(sample));
    graph.smoothedPeak = smoothPeak(graph.smoothedPeak, peak, 100);
    runOffscreenTask('send tab level', () => chrome.runtime.sendMessage({
      type: 'offscreen.level', browser: graph.browser, tabId: graph.tabId, peak: graph.smoothedPeak
    }));
  }
}, 100);

navigator.mediaDevices.addEventListener('devicechange', () => {
  runOffscreenTask('revalidate output device', async () => {
    for (const graph of [...graphs.values()]) {
      try {
        const output = await enqueueOutputDevice(graph, {
          browser: graph.browser,
          tabId: graph.tabId,
          generation: graph.generation,
          outputDeviceId: graph.selectedOutputDeviceId,
          outputDeviceName: graph.selectedOutputDeviceName,
          followSystemDefault: graph.followSystemDefault,
          resolvedOutputDeviceId: graph.resolvedOutputDeviceId,
          resolvedOutputDeviceName: graph.resolvedOutputDeviceName,
          browserOutputDeviceId: graph.browserOutputDeviceId,
          browserOutputDeviceLabel: graph.browserOutputDeviceLabel,
          browserGroupId: graph.browserGroupId,
          correlationId: graph.correlationId || crypto.randomUUID()
        });
        await chrome.runtime.sendMessage({ type: 'offscreen.outputChanged', ...output });
      } catch (error) {
        await chrome.runtime.sendMessage({
          type: 'offscreen.outputChanged', browser: graph.browser, tabId: graph.tabId,
          generation: graph.generation, correlationId: graph.correlationId,
          routingState: 'Failed', outputStatus: 'deviceRevalidationFailed', error: 'device-revalidation-failed',
          errorDetail: `${error.name || 'Error'}: ${error.message}`
        });
      }
    }
  });
});
