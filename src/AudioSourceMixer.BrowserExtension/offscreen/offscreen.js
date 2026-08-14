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
  if (message.type === 'audio.start') return executeResponseTask('创建标签页音频图', () => startGraph(message));
  if (message.type === 'audio.update') return executeResponseTask('更新标签页音频图', () => updateGraph(message));
  if (message.type === 'audio.stop')
    return executeResponseTask('停止标签页音频图', () => stopGraph(message.browser || 'chrome', message.tabId, false));
  if (message.type === 'audio.list') return listGraphs();
  return undefined;
});

function runOffscreenTask(name, action) {
  executeOffscreenTask(name, action);
}

async function executeOffscreenTask(name, action) {
  try { await action(); }
  catch (error) { console.error(`[Audio Source Mixer] ${name}失败`, error); }
}

async function executeResponseTask(name, action) {
  try { return await action(); }
  catch (error) {
    console.error(`[Audio Source Mixer] ${name}失败`, error);
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
      browser, tabId: message.tabId, title: message.title || '未命名标签页', origin: message.origin || '',
      stream, context, source, equalizerFilters, headroom, gain, panner, analyser,
      levelBuffer: new Float32Array(analyser.fftSize), smoothedPeak: 0,
      volume: 1, balance: 0, muted: false, generation: Number(message.generation) || 0,
      requestedOutputDeviceId: '', requestedOutputDeviceName: '',
      browserOutputDeviceId: '', browserOutputDeviceLabel: '', browserGroupId: '',
      effectiveSinkId: '', effectiveSinkLabel: '', routingState: 'Default',
      outputStatus: '系统默认', correlationId: '', setSinkDurationMs: 0,
      routeQueue: Promise.resolve(),
      setSinkIdSupported: typeof context.setSinkId === 'function', error: null,
      mappingRebound: null, mappingStale: false,
      equalizer: createEqualizerPreset('off')
    };
    graphs.set(key, graph);
    applyEqualizer(graph, message.equalizer);
    for (const track of stream.getTracks()) {
      track.addEventListener('ended', () =>
        runOffscreenTask('清理已结束媒体轨道', () => stopGraph(browser, message.tabId, true)), { once: true });
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
  graph.requestedOutputDeviceId = message.outputDeviceId ?? graph.requestedOutputDeviceId ?? '';
  graph.requestedOutputDeviceName = message.outputDeviceName ?? graph.requestedOutputDeviceName ?? '';
  graph.browserOutputDeviceId = message.browserOutputDeviceId ?? graph.browserOutputDeviceId ?? '';
  graph.browserOutputDeviceLabel = message.browserOutputDeviceLabel ?? graph.browserOutputDeviceLabel ?? '';
  graph.browserGroupId = message.browserGroupId ?? graph.browserGroupId ?? '';
  graph.correlationId = message.correlationId || graph.correlationId || crypto.randomUUID();
  graph.setSinkIdSupported = typeof graph.context.setSinkId === 'function';
  graph.error = null;
  graph.setSinkDurationMs = 0;
  graph.mappingRebound = null;
  graph.mappingStale = false;

  if (!graph.setSinkIdSupported) {
    graph.routingState = graph.requestedOutputDeviceId ? 'Failed' : 'Default';
    graph.outputStatus = graph.requestedOutputDeviceId
      ? '路由失败：当前 Chromium 不支持 AudioContext.setSinkId()。'
      : '系统默认（setSinkId 不可用）';
    graph.error = graph.requestedOutputDeviceId ? 'AudioContext.setSinkId is unavailable.' : null;
    return outputResult(graph);
  }

  if (!graph.requestedOutputDeviceId) return setAndVerifySink(graph, '', '系统默认', 'Default');

  if (!graph.browserOutputDeviceId) {
    graph.routingState = 'PendingAuthorization';
    graph.outputStatus = `等待浏览器授权：${graph.requestedOutputDeviceName || graph.requestedOutputDeviceId}`;
    graph.error = 'No authorized browser deviceId exists for the requested Windows endpoint.';
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
    graph.outputStatus = `浏览器 deviceId 已失效，需要重新授权：${graph.requestedOutputDeviceName || graph.requestedOutputDeviceId}`;
    graph.error = 'The authorized browser deviceId is no longer visible.';
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
      graph.error = `AudioContext.sinkId mismatch: requested=${safeId(requestedSinkId)}, effective=${safeId(graph.effectiveSinkId)}`;
      graph.outputStatus = `路由失败：实际 sink 与请求不一致（${graph.error}）。`;
    } else {
      graph.routingState = successState;
      graph.outputStatus = successState === 'Default' ? '系统默认' : `已生效：${label || safeId(requestedSinkId)}`;
      graph.error = null;
    }
  } catch (error) {
    graph.setSinkDurationMs = performance.now() - startedAt;
    graph.effectiveSinkId = typeof graph.context.sinkId === 'string' ? graph.context.sinkId : '';
    graph.routingState = 'Failed';
    graph.error = `${error.name || 'Error'}: ${error.message}`;
    graph.outputStatus = `输出设备切换失败：${graph.error}。当前 sink 保持不变，未静默回退默认设备。`;
  }
  return outputResult(graph);
}

function outputResult(graph) {
  return {
    browser: graph.browser,
    tabId: graph.tabId,
    generation: graph.generation,
    correlationId: graph.correlationId,
    outputDeviceId: graph.requestedOutputDeviceId,
    outputDeviceName: graph.requestedOutputDeviceName,
    browserDeviceId: graph.browserOutputDeviceId,
    browserDeviceLabel: graph.browserOutputDeviceLabel,
    browserGroupId: graph.browserGroupId,
    effectiveSinkId: graph.effectiveSinkId,
    effectiveSinkLabel: graph.effectiveSinkLabel,
    routingState: graph.routingState,
    setSinkDurationMs: graph.setSinkDurationMs,
    setSinkIdSupported: graph.setSinkIdSupported,
    outputStatus: graph.outputStatus,
    error: graph.error,
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
    runOffscreenTask('发送标签页电平', () => chrome.runtime.sendMessage({
      type: 'offscreen.level', browser: graph.browser, tabId: graph.tabId, peak: graph.smoothedPeak
    }));
  }
}, 100);

navigator.mediaDevices.addEventListener('devicechange', () => {
  runOffscreenTask('重新验证输出设备', async () => {
    for (const graph of [...graphs.values()]) {
      try {
        const output = await enqueueOutputDevice(graph, {
          browser: graph.browser,
          tabId: graph.tabId,
          generation: graph.generation,
          outputDeviceId: graph.requestedOutputDeviceId,
          outputDeviceName: graph.requestedOutputDeviceName,
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
          routingState: 'Failed', error: `${error.name || 'Error'}: ${error.message}`
        });
      }
    }
  });
});
