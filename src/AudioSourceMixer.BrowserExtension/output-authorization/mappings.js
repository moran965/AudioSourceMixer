import { normalizeDeviceLabel, sourceId } from '../shared/protocol.js';

export const OUTPUT_MAPPINGS_KEY = 'outputMappingsV3';
export const LEGACY_OUTPUT_MAPPINGS_KEY = 'outputMappingsV2';
export const OUTPUT_MAPPING_SCHEMA_VERSION = 3;
export const PENDING_OUTPUT_AUTHORIZATION_KEY = 'pendingOutputAuthorizationsV3';
const VIRTUAL_OUTPUT_DEVICE_IDS = new Set(['default', 'communications']);

export function physicalOutputDevices(devices) {
  return (devices || []).filter((device) => device.kind === 'audiooutput' && device.deviceId &&
    !VIRTUAL_OUTPUT_DEVICE_IDS.has(device.deviceId));
}

export function outputMappingKey(browser, windowsEndpointId) {
  const normalizedBrowser = String(browser || '').toLowerCase();
  const endpointId = String(windowsEndpointId || '').trim();
  if (!endpointId) throw new Error('Windows endpoint ID is required.');
  return `${normalizedBrowser}:${endpointId}`;
}

export function authorizationRequestKey(browser, windowsEndpointId) {
  return outputMappingKey(browser, windowsEndpointId);
}

export function migrateOutputMappingStore(current, legacy = null) {
  if (current?.schemaVersion === OUTPUT_MAPPING_SCHEMA_VERSION && current.mappings &&
      typeof current.mappings === 'object') {
    return { schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION, mappings: { ...current.mappings } };
  }
  const source = current?.mappings || current || legacy?.mappings || legacy || {};
  const migrated = {};
  for (const [legacyKey, value] of Object.entries(source)) {
    if (!validMapping(value)) continue;
    const browser = value.browser || String(legacyKey).split(':', 1)[0];
    const endpointId = value.windowsEndpointId || String(legacyKey).slice(String(legacyKey).indexOf(':') + 1);
    if (!['chrome', 'edge'].includes(browser) || !endpointId) continue;
    const now = value.updatedAt || value.authorizedAt || new Date().toISOString();
    migrated[outputMappingKey(browser, endpointId)] = {
      browser,
      windowsEndpointId: endpointId,
      windowsEndpointName: value.windowsEndpointName || endpointId,
      browserLabel: value.browserLabel || '',
      browserGroupId: value.browserGroupId || '',
      deviceId: value.deviceId,
      verificationState: 'unverified',
      authorizedAt: value.authorizedAt || now,
      verifiedAt: null,
      lastSeenAt: value.lastSeenAt || value.lastValidatedAt || now,
      staleReason: value.staleReason || null,
      updatedAt: now
    };
  }
  return { schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION, mappings: migrated };
}

export function outputMappings(store) {
  return migrateOutputMappingStore(store).mappings;
}

export function findOutputMapping(store, browser, windowsEndpointId, windowsEndpointName = '') {
  if (!windowsEndpointId) return null;
  const mappings = outputMappings(store);
  const exact = mappings[outputMappingKey(browser, windowsEndpointId)];
  if (usableMapping(exact)) return { ...exact, matchKind: 'endpointId' };

  const normalizedName = normalizeDeviceLabel(windowsEndpointName);
  if (!normalizedName) return null;
  const legacyMatches = Object.values(mappings).filter((mapping) =>
    usableMapping(mapping) && mapping.browser === browser && !mapping.windowsEndpointId &&
    normalizeDeviceLabel(mapping.windowsEndpointName) === normalizedName);
  return legacyMatches.length === 1 ? { ...legacyMatches[0], matchKind: 'legacyName' } : null;
}

export function saveOutputMapping(store, mapping) {
  validateMapping(mapping);
  const document = migrateOutputMappingStore(store);
  const now = new Date().toISOString();
  const verified = mapping.verificationState === 'verified' && Boolean(mapping.verifiedAt);
  const stored = {
    browser: mapping.browser,
    windowsEndpointId: mapping.windowsEndpointId,
    windowsEndpointName: mapping.windowsEndpointName,
    browserLabel: mapping.browserLabel || '',
    browserGroupId: mapping.browserGroupId || '',
    deviceId: mapping.deviceId,
    verificationState: verified ? 'verified' : mapping.verificationState || 'unverified',
    authorizedAt: mapping.authorizedAt || mapping.updatedAt || now,
    verifiedAt: verified ? mapping.verifiedAt : null,
    lastSeenAt: mapping.lastSeenAt || now,
    staleReason: mapping.staleReason || null,
    updatedAt: mapping.updatedAt || now
  };
  return {
    schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION,
    mappings: { ...document.mappings, [outputMappingKey(mapping.browser, mapping.windowsEndpointId)]: stored }
  };
}

export function confirmOutputMapping(store, candidate, confirmedAt = new Date().toISOString()) {
  return saveOutputMapping(store, {
    ...candidate,
    verificationState: 'verified',
    authorizedAt: candidate.authorizedAt || confirmedAt,
    verifiedAt: confirmedAt,
    lastSeenAt: confirmedAt,
    staleReason: null,
    updatedAt: confirmedAt
  });
}

export function removeOutputMapping(store, browser, windowsEndpointId) {
  const document = migrateOutputMappingStore(store);
  const mappings = { ...document.mappings };
  delete mappings[outputMappingKey(browser, windowsEndpointId)];
  return { schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION, mappings };
}

export function clearBrowserOutputMappings(store, browser) {
  const document = migrateOutputMappingStore(store);
  return {
    schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION,
    mappings: Object.fromEntries(Object.entries(document.mappings)
      .filter(([, mapping]) => mapping.browser !== browser))
  };
}

export function markOutputMappingStale(store, browser, windowsEndpointId,
    staleReason = 'device-id-not-visible', staleAt = new Date().toISOString()) {
  const document = migrateOutputMappingStore(store);
  const key = outputMappingKey(browser, windowsEndpointId);
  const mapping = document.mappings[key];
  if (!mapping) return document;
  return {
    schemaVersion: OUTPUT_MAPPING_SCHEMA_VERSION,
    mappings: {
      ...document.mappings,
      [key]: { ...mapping, verificationState: 'needs-reauthorization', staleReason,
        lastSeenAt: staleAt, updatedAt: staleAt }
    }
  };
}

export function rebindOutputMapping(mapping, devices) {
  if (!validMapping(mapping)) return null;
  const outputs = physicalOutputDevices(devices);
  const exact = outputs.filter((device) => device.deviceId === mapping.deviceId);
  if (exact.length === 1) return rebound(mapping, exact[0], 'deviceId');

  const label = normalizeDeviceLabel(mapping.browserLabel);
  const byGroupAndLabel = outputs.filter((device) => mapping.browserGroupId && device.groupId === mapping.browserGroupId &&
    label && normalizeDeviceLabel(device.label) === label);
  if (byGroupAndLabel.length === 1) return rebound(mapping, byGroupAndLabel[0], 'groupId+label');

  const byLabel = outputs.filter((device) => label && normalizeDeviceLabel(device.label) === label);
  return byLabel.length === 1 ? rebound(mapping, byLabel[0], 'label') : null;
}

export function queueAuthorizationRequest(queue, request) {
  if (!request?.browser || !request.windowsEndpointId) throw new Error('Invalid authorization request.');
  const key = authorizationRequestKey(request.browser, request.windowsEndpointId);
  const previous = queue?.[key] || {};
  const waiterKey = sourceId(request.browser, request.tabId);
  const waiter = {
    browser: request.browser,
    tabId: request.tabId,
    correlationId: request.correlationId || '',
    generation: Number.isSafeInteger(request.generation) ? request.generation : 0,
    requestedAt: request.requestedAt || new Date().toISOString()
  };
  return {
    ...(queue || {}),
    [key]: {
      ...previous,
      browser: request.browser,
      windowsEndpointId: request.windowsEndpointId,
      windowsEndpointName: request.windowsEndpointName || request.windowsEndpointId,
      outputDevices: Array.isArray(request.outputDevices) ? request.outputDevices : previous.outputDevices || [],
      requestedAt: previous.requestedAt || waiter.requestedAt,
      updatedAt: waiter.requestedAt,
      waiters: { ...(previous.waiters || {}), [waiterKey]: waiter }
    }
  };
}

export function pendingAuthorizationState(state) {
  const name = state.resolvedOutputDeviceName || state.outputDeviceName ||
    state.resolvedOutputDeviceId || state.outputDeviceId;
  return {
    ...state,
    routingState: 'PendingAuthorization',
    outputStatus: state.followSystemDefault ? 'authorizationRequiredForDefault' : 'authorizationRequired',
    outputStatusDetail: name,
    error: 'authorization-required'
  };
}

export function removeAuthorizationRequest(queue, browser, windowsEndpointId) {
  const updated = { ...(queue || {}) };
  delete updated[authorizationRequestKey(browser, windowsEndpointId)];
  return updated;
}

export function removeAuthorizationWaiters(queue, browser, windowsEndpointId, waiterKeys) {
  const updated = { ...(queue || {}) };
  const key = authorizationRequestKey(browser, windowsEndpointId);
  const request = updated[key];
  if (!request) return updated;

  const keys = new Set((waiterKeys || []).filter((value) => typeof value === 'string' && value));
  if (keys.size === 0) {
    delete updated[key];
    return updated;
  }

  const waiters = Object.fromEntries(Object.entries(request.waiters || {})
    .filter(([waiterKey]) => !keys.has(waiterKey)));
  if (Object.keys(waiters).length === 0) delete updated[key];
  else updated[key] = { ...request, waiters };
  return updated;
}

export function pendingAuthorizationRequests(queue, browser) {
  return Object.values(queue || {})
    .filter((request) => request.browser === browser && request.windowsEndpointId)
    .sort((left, right) => String(left.requestedAt).localeCompare(String(right.requestedAt)));
}

export function mappingIsVisible(mapping, devices) {
  return Boolean(rebindOutputMapping(mapping, devices)?.matchKind === 'deviceId');
}

export function mappingDisplayState(mapping, devices) {
  if (mapping.verificationState === 'needs-reauthorization') return 'needs-reauthorization';
  if (!mappingIsVisible(mapping, devices)) return 'unavailable';
  return mapping.verificationState === 'verified' ? 'verified' : 'unverified';
}

function rebound(mapping, device, matchKind) {
  return {
    ...mapping,
    deviceId: device.deviceId,
    browserLabel: device.label || mapping.browserLabel || '',
    browserGroupId: device.groupId || mapping.browserGroupId || '',
    lastSeenAt: new Date().toISOString(),
    staleReason: null,
    matchKind
  };
}

function validateMapping(mapping) {
  if (!mapping || !['chrome', 'edge'].includes(mapping.browser)) throw new Error('Unsupported browser mapping.');
  if (typeof mapping.windowsEndpointId !== 'string' || !mapping.windowsEndpointId.trim())
    throw new Error('Windows endpoint ID is required.');
  if (!normalizeDeviceLabel(mapping.windowsEndpointName)) throw new Error('Windows endpoint name is required.');
  if (typeof mapping.deviceId !== 'string' || !mapping.deviceId) throw new Error('Browser deviceId is required.');
  if (VIRTUAL_OUTPUT_DEVICE_IDS.has(mapping.deviceId))
    throw new Error('A concrete browser output device is required; default and communications are not physical mappings.');
}

function validMapping(mapping) {
  return Boolean(mapping && typeof mapping.deviceId === 'string' && mapping.deviceId);
}

function usableMapping(mapping) {
  return validMapping(mapping) && mapping.verificationState === 'verified';
}
